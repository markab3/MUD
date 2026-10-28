using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MUD.Core.GameObjects;

namespace MUD.Core.Import
{
    // Persistence seam for the importer. Works on raw documents so that the importer can only
    // ever issue field-level writes (never a whole-document replace).
    public interface IRoomImportStore
    {
        // Raw stored document for the room _id, or null.
        BsonDocument Find(string id);

        // _ids of stored rooms whose Area equals the given prefix.
        List<string> FindIdsByArea(string area);

        // Upsert: $set the given fields, and $setOnInsert the insert defaults.
        void Upsert(string id, BsonDocument set, BsonDocument setOnInsert);
    }

    public enum ImportAction { Insert, Update, Unchanged }

    public class RoomImportPlan
    {
        public string Key { get; set; }

        public ImportAction Action { get; set; }

        public List<string> ChangedFields { get; set; } = new List<string>();
    }

    public class ImportResult
    {
        public List<string> Errors { get; set; } = new List<string>();

        public List<RoomImportPlan> Plan { get; set; } = new List<RoomImportPlan>();

        // Rooms stored under this area whose key is no longer in the file. Never deleted.
        public List<string> Orphans { get; set; } = new List<string>();

        // Behavior entries of the file that were NOT written because the stored room already has an entry with the
        // same (hook, module, type). Reported, never silently dropped (owner decision D39).
        public List<string> SkippedBehaviors { get; set; } = new List<string>();

        // Stored entries replaced by the file's entry because the replace flag was set.
        public List<string> ReplacedBehaviors { get; set; } = new List<string>();

        public bool DryRun { get; set; }

        public bool Success { get { return Errors.Count == 0; } }

        public int Count(ImportAction action) { return Plan.Count(p => p.Action == action); }

        public string Summary()
        {
            if (!Success)
            {
                return string.Format("Import failed with {0} error(s); nothing was written.", Errors.Count);
            }
            return string.Format("{0}{1} inserted, {2} updated, {3} unchanged, {4} orphaned.",
                DryRun ? "Dry run: " : "Import complete: ", Count(ImportAction.Insert), Count(ImportAction.Update), Count(ImportAction.Unchanged), Orphans.Count);
        }
    }

    // Implements specs/ROOM_FILES.md section 6: validate the whole file, plan, then apply with
    // field-level upserts that touch definition fields only.
    public class AreaImporter
    {
        // The complete definition-field list (spec section 5). Everything else is state.
        public static readonly string[] DefinitionFields = new[]
        {
            "ShortDescription", "LongDescription", "Handles", "Adjectives", "Plurals",
            "Exits", "Features", "search", "behaviors", "Area", "X", "Y", "Z"
        };

        private readonly IRoomImportStore _store;

        public AreaImporter(IRoomImportStore store)
        {
            _store = store;
        }

        // replaceBehaviors: by default a behavior entry in the file whose (hook, module, type) already exists on the
        // stored room is reported and skipped, and stored entries absent from the file are kept (the database is the
        // source of truth, D39). With the flag the file's entry replaces the stored one; absent entries are still kept.
        public ImportResult Import(string yaml, bool dryRun = false, bool replaceBehaviors = false)
        {
            var result = new ImportResult { DryRun = dryRun };

            // Phase 1: parse and validate. Read-only.
            var parsed = AreaParser.Parse(yaml);
            result.Errors.AddRange(parsed.Errors);
            if (parsed.Area == null) { return result; }
            var area = parsed.Area;
            Validate(area, result);
            if (!result.Success) { return result; }

            // Phase 2: plan. Read-only.
            var writes = new List<Tuple<string, BsonDocument, BsonDocument>>();
            foreach (var room in area.Rooms)
            {
                var existing = _store.Find(room.FullKey);
                var desired = BuildDesiredFields(room, area.Prefix, existing, result, replaceBehaviors);
                var changed = new BsonDocument();
                foreach (var element in desired)
                {
                    if (existing == null || !existing.GetValue(element.Name, BsonNull.Value).Equals(element.Value))
                    {
                        changed.Add(element);
                    }
                }

                var plan = new RoomImportPlan { Key = room.FullKey, ChangedFields = changed.Names.ToList() };
                if (existing == null) { plan.Action = ImportAction.Insert; }
                else if (changed.ElementCount > 0) { plan.Action = ImportAction.Update; }
                else { plan.Action = ImportAction.Unchanged; }
                result.Plan.Add(plan);

                if (plan.Action == ImportAction.Insert)
                {
                    writes.Add(Tuple.Create(room.FullKey, changed, new BsonDocument("Items", new BsonArray())));
                }
                else if (plan.Action == ImportAction.Update)
                {
                    writes.Add(Tuple.Create(room.FullKey, changed, (BsonDocument)null));
                }
            }

            var fileKeys = new HashSet<string>(area.Rooms.Select(r => r.FullKey));
            result.Orphans = _store.FindIdsByArea(area.Prefix).Where(id => !fileKeys.Contains(id)).OrderBy(id => id).ToList();

            // Phase 3: apply.
            if (!dryRun)
            {
                foreach (var write in writes)
                {
                    try
                    {
                        _store.Upsert(write.Item1, write.Item2, write.Item3);
                    }
                    catch (Exception ex)
                    {
                        // Safe to re-run: the import is idempotent.
                        result.Errors.Add(string.Format("Write failed for '{0}': {1}. Rooms written before the failure: {2}. Re-run the import to finish.",
                            write.Item1, ex.Message, string.Join(", ", writes.TakeWhile(w => w != write).Select(w => w.Item1))));
                        break;
                    }
                }
            }
            return result;
        }

        // Validation over the whole parsed file. Mutates the area: expands 'reverse' exits.
        private void Validate(AreaDefinition area, ImportResult result)
        {
            var errors = result.Errors;
            var rooms = new Dictionary<string, RoomDefinition>();
            foreach (var group in area.Rooms.GroupBy(r => r.FullKey))
            {
                if (group.Count() > 1) { errors.Add(string.Format("Duplicate room key '{0}' ({1} times).", group.Key, group.Count())); }
                rooms[group.Key] = group.First();
            }

            foreach (var room in area.Rooms)
            {
                if (MUD.Data.RoomRules.LooksLikeObjectId(room.FullKey))
                {
                    errors.Add(string.Format("Room '{0}': authored keys must not look like generated ObjectIds.", room.FullKey));
                }

                foreach (var dup in MUD.Data.RoomRules.DuplicateDirections(room.Exits.Select(e => e.Dir)))
                {
                    errors.Add(string.Format("Room '{0}': duplicate exit '{1}'.", room.FullKey, dup));
                }
                foreach (var dup in room.Features.GroupBy(f => (f.Short ?? "").ToLowerInvariant()).Where(g => g.Count() > 1))
                {
                    errors.Add(string.Format("Room '{0}': duplicate feature '{1}'.", room.FullKey, dup.Key));
                }
                foreach (var dup in room.Properties.GroupBy(p => p.Id).Where(g => g.Key != null && g.Count() > 1))
                {
                    errors.Add(string.Format("Room '{0}': duplicate property id '{1}'.", room.FullKey, dup.Key));
                }
            }

            // Expand 'reverse' exits (in memory, before the dangling check).
            foreach (var room in area.Rooms)
            {
                foreach (var exit in room.Exits.Where(e => !string.IsNullOrEmpty(e.Reverse)).ToList())
                {
                    RoomDefinition destination;
                    if (!rooms.TryGetValue(exit.To, out destination))
                    {
                        errors.Add(string.Format("Room '{0}', exit '{1}': 'reverse' requires the destination '{2}' to be in this file.", room.FullKey, exit.Dir, exit.To));
                        continue;
                    }
                    var existingBack = destination.Exits.FirstOrDefault(e => string.Equals(e.Dir, exit.Reverse, StringComparison.OrdinalIgnoreCase));
                    if (existingBack == null)
                    {
                        destination.Exits.Add(new ExitDefinition { Dir = exit.Reverse, To = room.FullKey });
                    }
                    else if (existingBack.To != room.FullKey)
                    {
                        errors.Add(string.Format("Room '{0}', exit '{1}': reverse '{2}' conflicts with exit '{2}' of '{3}', which leads to '{4}'.",
                            room.FullKey, exit.Dir, exit.Reverse, destination.FullKey, existingBack.To));
                    }
                }
            }

            // Dangling exits and cross-file key collisions. Reads the store.
            foreach (var room in area.Rooms)
            {
                foreach (var exit in room.Exits)
                {
                    if (exit.To != null && !rooms.ContainsKey(exit.To) && _store.Find(exit.To) == null)
                    {
                        errors.Add(string.Format("Room '{0}', exit '{1}': destination '{2}' does not exist.", room.FullKey, exit.Dir, exit.To));
                    }
                }

                var stored = _store.Find(room.FullKey);
                if (stored != null)
                {
                    var storedArea = stored.GetValue("Area", BsonNull.Value);
                    if (!storedArea.IsBsonNull && storedArea.AsString != area.Prefix)
                    {
                        errors.Add(string.Format("Room '{0}' already exists and belongs to area '{1}'.", room.FullKey, storedArea.AsString));
                    }
                }
            }
        }

        // Builds the complete desired value of every definition field for one room, merging in
        // state that lives inside matched list elements.
        private BsonDocument BuildDesiredFields(RoomDefinition def, string prefix, BsonDocument existing, ImportResult result, bool replaceBehaviors)
        {
            var carrier = new Room(null)
            {
                Id = def.FullKey,
                ShortDescription = def.Short,
                LongDescription = def.Long,
                Handles = new List<string>(def.Handles),
                Adjectives = new List<string>(def.Adjectives),
                Plurals = new List<string>(def.Plurals),
                Area = prefix,
                X = def.X,
                Y = def.Y,
                Z = def.Z,
                Exits = def.Exits.Select(e => new Exit { Name = e.Dir, DestinationId = e.To }).ToList(),
                Search = new MUD.Core.Components.Search { Entries = def.Properties.Select(ToSearchEntry).ToList() },
                Behaviors = def.Behaviors.Select(b => b.Clone()).ToList(),
                Features = def.Features.Select(f =>
                {
                    var feature = new Feature
                    {
                        Id = def.FullKey + "#" + f.Short.ToLowerInvariant(),
                        ShortDescription = f.Short,
                        LongDescription = f.Long,
                        RoomLine = f.RoomLine,
                        DetailDescription = f.Detail,
                        Handles = new List<string>(f.Handles),
                        Adjectives = new List<string>(f.Adjectives),
                        Plurals = new List<string>(f.Plurals),
                    };
                    return feature;
                }).ToList()
            };
            var carrierDoc = carrier.ToBsonDocument();

            var desired = new BsonDocument();
            foreach (var field in DefinitionFields)
            {
                desired[field] = carrierDoc.GetValue(field, BsonNull.Value);
            }

            if (existing != null)
            {
                // Keep state inside matched elements (spec 6.2 element-level merge).
                // The search component keeps the stored cooldown (nextRewardAt) of every entry that has the same id.
                var existingSearch = existing.GetValue("search", BsonNull.Value) as BsonDocument;
                var existingEntries = existingSearch == null ? null : existingSearch.GetValue("entries", BsonNull.Value) as BsonArray;
                var mergedEntries = MergeElements(existingEntries, desired["search"].AsBsonDocument["entries"].AsBsonArray, SearchEntryId);
                desired["search"] = new BsonDocument("entries", mergedEntries);

                var existingFeatures = existing.GetValue("Features", BsonNull.Value) as BsonArray;
                var features = MergeElements(existingFeatures, desired["Features"].AsBsonArray, ByShort);
                desired["Features"] = features;

                var existingBehaviors = existing.GetValue("behaviors", BsonNull.Value) as BsonArray;
                var merged = MergeBehaviors(def.FullKey, existingBehaviors, desired["behaviors"].AsBsonArray, result, replaceBehaviors);
                if (merged.Count == 0 && existingBehaviors == null) { desired.Remove("behaviors"); }
                else { desired["behaviors"] = merged; }
            }
            return desired;
        }

        // Collision-safe merge of behavior entries (owner decision D39). Stored entries are always kept, in their
        // order. A file entry whose (hook, module, type) is already stored is skipped and reported, or replaces the
        // stored entry in place when replaceBehaviors is set. File entries that are new are appended in file order.
        private static BsonArray MergeBehaviors(string roomKey, BsonArray stored, BsonArray authored, ImportResult result, bool replaceBehaviors)
        {
            var merged = new BsonArray();
            if (stored != null) { foreach (var value in stored) { merged.Add(value.DeepClone()); } }

            foreach (var authoredValue in authored)
            {
                var authoredDoc = authoredValue.AsBsonDocument;
                var label = string.Format("{0} {1} {2}", authoredDoc["hook"], authoredDoc["module"], authoredDoc["type"]);
                int index = -1;
                for (int i = 0; i < merged.Count; i++)
                {
                    var storedDoc = merged[i] as BsonDocument;
                    if (storedDoc != null && SameBinding(storedDoc, authoredDoc)) { index = i; break; }
                }

                if (index < 0) { merged.Add(authoredDoc); }
                else if (replaceBehaviors)
                {
                    if (!merged[index].Equals(authoredDoc)) { result.ReplacedBehaviors.Add(string.Format("Room '{0}': behavior ({1}) replaced.", roomKey, label)); }
                    merged[index] = authoredDoc;
                }
                else if (!merged[index].Equals(authoredDoc))
                {
                    // An identical stored entry is already in place: nothing to report. Only a differing one is a collision.
                    result.SkippedBehaviors.Add(string.Format("Room '{0}': behavior ({1}) already exists; skipped.", roomKey, label));
                }
            }
            return merged;
        }

        private static bool SameBinding(BsonDocument a, BsonDocument b)
        {
            return a.GetValue("hook", BsonNull.Value).Equals(b.GetValue("hook", BsonNull.Value))
                && a.GetValue("module", BsonNull.Value).Equals(b.GetValue("module", BsonNull.Value))
                && a.GetValue("type", BsonNull.Value).Equals(b.GetValue("type", BsonNull.Value));
        }

        private static BsonValue ById(BsonDocument doc) { return doc.GetValue("_id", BsonNull.Value); }

        private static BsonValue ByShort(BsonDocument doc)
        {
            var value = doc.GetValue("ShortDescription", BsonNull.Value);
            return value.IsString ? (BsonValue)value.AsString.ToLowerInvariant() : value;
        }

        // Replaces a list in file order. For elements matching an existing one, starts from the
        // stored element (keeping its _id and any fields we do not author, i.e. state) and
        // overlays the authored fields.
        private static BsonArray MergeElements(BsonArray existing, BsonArray authored, Func<BsonDocument, BsonValue> identity)
        {
            var merged = new BsonArray();
            foreach (var authoredValue in authored)
            {
                var authoredDoc = authoredValue.AsBsonDocument;
                var match = existing == null ? null : existing.OfType<BsonDocument>().FirstOrDefault(e => identity(e).Equals(identity(authoredDoc)));
                if (match == null) { merged.Add(authoredDoc); continue; }

                var combined = match.DeepClone().AsBsonDocument;
                foreach (var element in authoredDoc)
                {
                    if (element.Name == "_id" || element.Name == "_t") { continue; }
                    combined[element.Name] = element.Value;
                }
                merged.Add(combined);
            }
            return merged;
        }

        private static BsonValue SearchEntryId(BsonDocument doc) { return doc.GetValue("id", BsonNull.Value); }

        private static MUD.Core.Components.SearchEntry ToSearchEntry(SearchPropertyDefinition def)
        {
            return new MUD.Core.Components.SearchEntry
            {
                EntryId = def.Id,
                Target = def.Target,
                RefreshMilliseconds = def.RefreshMs,
                Reward = new InventoryItem
                {
                    Id = def.Id + ".reward",
                    ShortDescription = def.Reward.Short,
                    LongDescription = def.Reward.Long,
                    Handles = new List<string>(def.Reward.Handles),
                    Adjectives = new List<string>(def.Reward.Adjectives),
                    Plurals = new List<string>(def.Reward.Plurals)
                }
            };
        }
    }
}
