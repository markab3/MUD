using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MongoDB.Bson;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using MUD.Core.Generation.Terrains;
using MUD.Core.Import;

namespace MUD.Core.Generation
{
    public enum RoomGenOutcome { Replaced, Unchanged, NotOwned, MapNotFound, Error, Updated, NotGenerated }

    public enum FeatureUpdateOutcome { Updated, Current, TemplateDeleted, NewerInRoom, Conflict }

    public class FeatureUpdateLine
    {
        public string Short { get; set; }
        public string TemplateId { get; set; }
        public FeatureUpdateOutcome Outcome { get; set; }
        public int FromVersion { get; set; }
        public int ToVersion { get; set; }
    }

    public class RoomGenLine
    {
        public string Key { get; set; }
        public RoomGenOutcome Outcome { get; set; }
        public string Detail { get; set; }
        public List<FeatureUpdateLine> Features { get; set; } = new List<FeatureUpdateLine>();
    }

    public class GenerationReport
    {
        public List<string> Errors { get; set; } = new List<string>();
        public List<RoomGenLine> Rooms { get; set; } = new List<RoomGenLine>();
        // Map regen only: rooms of the prefix that are no longer in the map. Never deleted.
        public List<string> Orphans { get; set; } = new List<string>();
        public bool DryRun { get; set; }
        public int LibraryVersion { get; set; }
        // The audit rows could not be written, so nothing was changed.
        public bool AuditFailed { get; set; }
        // The writes failed part way (the importer reports which); re-running finishes the job.
        public bool WriteFailed { get; set; }

        public int Count(RoomGenOutcome outcome) { return Rooms.Count(r => r.Outcome == outcome); }

        public int CountFeatures(FeatureUpdateOutcome outcome) { return Rooms.SelectMany(r => r.Features).Count(f => f.Outcome == outcome); }

        // One line per room, then the counts. Suitable for the creator's console.
        public List<string> Describe(bool update)
        {
            var lines = new List<string>();
            foreach (var error in Errors) { lines.Add("error: " + error); }
            foreach (var room in Rooms)
            {
                var text = room.Key + ": " + OutcomeText(room.Outcome) + (string.IsNullOrEmpty(room.Detail) ? "" : " (" + room.Detail + ")");
                lines.Add(text);
                foreach (var f in room.Features.Where(f => f.Outcome != FeatureUpdateOutcome.Current || true))
                {
                    lines.Add(string.Format("    {0} [{1}]: {2}{3}", f.Short, f.TemplateId, FeatureText(f.Outcome),
                        f.Outcome == FeatureUpdateOutcome.Updated ? string.Format(" v{0}->v{1}", f.FromVersion, f.ToVersion) : ""));
                }
            }
            foreach (var orphan in Orphans) { lines.Add("orphaned (kept): " + orphan); }
            if (AuditFailed) { lines.Add("The audit record could not be written. Nothing was changed."); }
            else if (WriteFailed) { lines.Add("A write failed part way. Re-run to finish."); }
            else if (update)
            {
                lines.Add(string.Format("{0}{1} feature(s) updated, {2} unchanged, {3} template deleted (left as is), {4} newer in room, {5} conflict, in {6} room(s).",
                    DryRun ? "Dry run: " : "", CountFeatures(FeatureUpdateOutcome.Updated), CountFeatures(FeatureUpdateOutcome.Current),
                    CountFeatures(FeatureUpdateOutcome.TemplateDeleted), CountFeatures(FeatureUpdateOutcome.NewerInRoom), CountFeatures(FeatureUpdateOutcome.Conflict), Rooms.Count));
            }
            else
            {
                lines.Add(string.Format("{0}{1} regenerated, {2} unchanged, {3} not owned, {4} not generated, {5} map not found, {6} error.",
                    DryRun ? "Dry run: " : "", Count(RoomGenOutcome.Replaced), Count(RoomGenOutcome.Unchanged), Count(RoomGenOutcome.NotOwned),
                    Count(RoomGenOutcome.NotGenerated), Count(RoomGenOutcome.MapNotFound), Count(RoomGenOutcome.Error)));
            }
            return lines;
        }

        private static string OutcomeText(RoomGenOutcome o)
        {
            switch (o)
            {
                case RoomGenOutcome.Replaced: return "regenerated";
                case RoomGenOutcome.Unchanged: return "unchanged";
                case RoomGenOutcome.NotOwned: return "not owned (stored room is hand-authored or from another map)";
                case RoomGenOutcome.MapNotFound: return "map not found";
                case RoomGenOutcome.Updated: return "updated";
                case RoomGenOutcome.NotGenerated: return "not a generated room";
                default: return "error";
            }
        }

        private static string FeatureText(FeatureUpdateOutcome o)
        {
            switch (o)
            {
                case FeatureUpdateOutcome.Updated: return "updated";
                case FeatureUpdateOutcome.Current: return "unchanged";
                case FeatureUpdateOutcome.TemplateDeleted: return "template deleted, left unchanged";
                case FeatureUpdateOutcome.NewerInRoom: return "newer than the template, left unchanged";
                default: return "conflict (duplicate short), left unchanged";
            }
        }
    }

    // One audit row announced before a room is written.
    public class GenAuditEntry
    {
        public string Key { get; set; }
        public string Field { get; set; }
        public string Summary { get; set; }
    }

    // Records the entries (audit first) and returns the function that marks them applied or rejected afterwards.
    // Returns null when the rows could not be written; the service then changes nothing.
    public delegate Action<bool> GenAuditBegin(IReadOnlyList<GenAuditEntry> entries);

    // Regen and update of generated rooms (specs/TERRAIN_GENERATORS.md section 9). Every write goes through the
    // importer, the only writer of authored content. The store should refresh live objects after a write
    // (RoomRepositoryImportStore does: the repository re-reads the stored room into the cached one).
    public class GenerationService
    {
        public const string RegenField = "*regen*";
        public const string UpdateField = "*update*";

        private readonly IRoomImportStore _store;
        private readonly AreaImporter _importer;
        private readonly Func<ILibrary> _library;
        private readonly string _contentDirectory;
        private readonly GenAuditBegin _audit;

        // library: returns a current snapshot, or null when there is none; may throw when the database is down.
        public GenerationService(IRoomImportStore store, Func<ILibrary> library, string contentDirectory, GenAuditBegin audit)
        {
            _store = store ?? throw new ArgumentNullException("store");
            _importer = new AreaImporter(_store);
            _library = library;
            _contentDirectory = contentDirectory;
            _audit = audit ?? (entries => (applied => { }));
        }

        private static readonly ImportOptions ReplaceOptions = new ImportOptions { ReplaceGenerated = true };

        // ------------------------------------------------------------------ selection

        // Keys of the generated rooms of an area prefix (rooms with stored provenance), sorted.
        public List<string> GeneratedKeysOfArea(string prefix)
        {
            return _store.FindIdsByArea(prefix ?? "").Where(id => GenOf(_store.Find(id)) != null).OrderBy(id => id, StringComparer.Ordinal).ToList();
        }

        // Keys of the generated rooms whose provenance names this map. The map's prefix is read from its file when it exists.
        public List<string> GeneratedKeysOfMap(string map)
        {
            var prefix = map;
            var path = MapPath(map);
            if (path != null && File.Exists(path))
            {
                var parsed = MapGenerator.Generate(File.ReadAllText(path, Encoding.UTF8));
                if (parsed.Success) { prefix = parsed.Prefix; }
            }
            return _store.FindIdsByArea(prefix ?? "")
                .Where(id => { var g = GenOf(_store.Find(id)); return g != null && Str(g, "map") == map; })
                .OrderBy(id => id, StringComparer.Ordinal).ToList();
        }

        // ------------------------------------------------------------------ regen

        // Regenerates every room of the map (map re-import): description, features, properties and behaviors are replaced
        // by the generator's output; exits follow the grid; items and occupants stay.
        public GenerationReport RegenerateMap(string map, bool dryRun, string newSeed)
        {
            var report = new GenerationReport { DryRun = dryRun };
            var library = OpenLibrary(report);
            if (library == null) { return report; }
            var path = MapPath(map);
            if (path == null) { report.Errors.Add("Invalid map name. Use the file name without extension."); return report; }
            if (!File.Exists(path)) { report.Errors.Add("No such map: " + map); return report; }

            var generated = MapGenerator.Generate(File.ReadAllText(path, Encoding.UTF8), map, library, null, string.IsNullOrEmpty(newSeed) ? null : newSeed);
            if (!generated.Success) { report.Errors.AddRange(generated.Errors); return report; }

            var dry = _importer.Import(generated.Yaml, new ImportOptions { ReplaceGenerated = true, DryRun = true });
            if (!dry.Success) { report.Errors.AddRange(dry.Errors); return report; }
            report.Orphans = dry.Orphans;
            foreach (var plan in dry.Plan) { report.Rooms.Add(LineFor(plan, dry, null)); }

            if (!dryRun)
            {
                var changed = dry.Plan.Where(p => p.Action != ImportAction.Unchanged && !dry.NotOwned.Contains(p.Key)).Select(p => p.Key).ToList();
                var summary = "regenerated from map " + map + (string.IsNullOrEmpty(newSeed) ? "" : " with a new seed") + ", library version " + library.Version;
                Apply(report, changed, RegenField, summary, () => _importer.Import(generated.Yaml, ReplaceOptions));
            }
            return report;
        }

        // Regenerates the named rooms: each is generated again from its map with its stored seed (or newSeed).
        public GenerationReport RegenerateRooms(IEnumerable<string> keys, bool dryRun, string newSeed)
        {
            var report = new GenerationReport { DryRun = dryRun };
            var library = OpenLibrary(report);
            if (library == null) { return report; }

            var groups = new Dictionary<Tuple<string, string>, List<string>>();
            foreach (var key in (keys ?? new string[0]).Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).Distinct())
            {
                var gen = GenOf(_store.Find(key));
                if (_store.Find(key) == null) { report.Rooms.Add(new RoomGenLine { Key = key, Outcome = RoomGenOutcome.Error, Detail = "no such room" }); continue; }
                if (gen == null) { report.Rooms.Add(new RoomGenLine { Key = key, Outcome = RoomGenOutcome.NotGenerated }); continue; }
                var map = Str(gen, "map");
                var seed = string.IsNullOrEmpty(newSeed) ? Str(gen, "seed") : newSeed;
                if (map == null || MapPath(map) == null || !File.Exists(MapPath(map)))
                {
                    report.Rooms.Add(new RoomGenLine { Key = key, Outcome = RoomGenOutcome.MapNotFound, Detail = map });
                    continue;
                }
                var group = Tuple.Create(map, seed);
                if (!groups.ContainsKey(group)) { groups[group] = new List<string>(); }
                groups[group].Add(key);
            }

            var pending = new List<Tuple<string, List<string>>>();   // partial area yaml, changed keys
            foreach (var group in groups)
            {
                var map = group.Key.Item1;
                var generated = MapGenerator.Generate(File.ReadAllText(MapPath(map), Encoding.UTF8), map, library, null, group.Key.Item2);
                if (!generated.Success)
                {
                    foreach (var key in group.Value) { report.Rooms.Add(new RoomGenLine { Key = key, Outcome = RoomGenOutcome.Error, Detail = "the map has errors: " + generated.Errors.First() }); }
                    continue;
                }
                var present = AreaParser.Parse(generated.Yaml).Area.Rooms.Select(r => r.FullKey).ToHashSet();
                foreach (var key in group.Value.Where(k => !present.Contains(k)))
                {
                    report.Rooms.Add(new RoomGenLine { Key = key, Outcome = RoomGenOutcome.Error, Detail = "the room is no longer in the map" });
                }
                var wanted = group.Value.Where(present.Contains).ToList();
                if (wanted.Count == 0) { continue; }

                var partial = OnlyRooms(generated.Yaml, generated.Prefix, wanted);
                var dry = _importer.Import(partial, new ImportOptions { ReplaceGenerated = true, DryRun = true });
                if (!dry.Success)
                {
                    foreach (var key in wanted) { report.Rooms.Add(new RoomGenLine { Key = key, Outcome = RoomGenOutcome.Error, Detail = dry.Errors.First() }); }
                    continue;
                }
                foreach (var plan in dry.Plan) { report.Rooms.Add(LineFor(plan, dry, null)); }
                var changed = dry.Plan.Where(p => p.Action != ImportAction.Unchanged && !dry.NotOwned.Contains(p.Key)).Select(p => p.Key).ToList();
                if (changed.Count > 0) { pending.Add(Tuple.Create(partial, changed)); }
            }

            if (!dryRun && pending.Count > 0)
            {
                var all = pending.SelectMany(p => p.Item2).ToList();
                Apply(report, all, RegenField, "regenerated from its map, library version " + library.Version + (string.IsNullOrEmpty(newSeed) ? "" : " with a new seed"), () =>
                {
                    var combined = new ImportResult();
                    foreach (var p in pending)
                    {
                        var r = _importer.Import(p.Item1, ReplaceOptions);
                        combined.Errors.AddRange(r.Errors);
                        if (!r.Success) { break; }
                    }
                    return combined;
                });
            }
            return report;
        }

        // ------------------------------------------------------------------ update

        // Refreshes the template-referenced features of the named generated rooms. Features without a templateId, the
        // description, provenance, exits, properties and behaviors are never touched.
        public GenerationReport UpdateRooms(IEnumerable<string> keys, bool dryRun)
        {
            var report = new GenerationReport { DryRun = dryRun };
            var library = OpenLibrary(report);
            if (library == null) { return report; }

            var pending = new List<Tuple<string, BsonArray>>();
            foreach (var key in (keys ?? new string[0]).Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).Distinct())
            {
                var doc = _store.Find(key);
                if (doc == null) { report.Rooms.Add(new RoomGenLine { Key = key, Outcome = RoomGenOutcome.Error, Detail = "no such room" }); continue; }
                if (GenOf(doc) == null) { report.Rooms.Add(new RoomGenLine { Key = key, Outcome = RoomGenOutcome.NotGenerated }); continue; }

                var line = new RoomGenLine { Key = key, Outcome = RoomGenOutcome.Unchanged };
                var features = doc.GetValue("Features", BsonNull.Value) as BsonArray ?? new BsonArray();
                var rewritten = new BsonArray(features.Select(f => f.DeepClone()));
                for (int i = 0; i < rewritten.Count; i++)
                {
                    var feature = rewritten[i] as BsonDocument;
                    if (feature == null) { continue; }
                    var templateId = Str(feature, "TemplateId");
                    if (string.IsNullOrEmpty(templateId)) { continue; }   // authored feature: never read for change

                    var shortText = Str(feature, "ShortDescription");
                    var stored = feature.GetValue("TemplateVersion", BsonNull.Value);
                    int from = stored.IsNumeric ? stored.ToInt32() : 0;
                    var template = library.FeatureById(templateId);
                    var l = new FeatureUpdateLine { Short = shortText, TemplateId = templateId, FromVersion = from, ToVersion = from };
                    if (template == null) { l.Outcome = FeatureUpdateOutcome.TemplateDeleted; }
                    else if (template.Version < from) { l.Outcome = FeatureUpdateOutcome.NewerInRoom; l.ToVersion = template.Version; }
                    else if (template.Version == from) { l.Outcome = FeatureUpdateOutcome.Current; l.ToVersion = from; }
                    else
                    {
                        l.ToVersion = template.Version;
                        var newShort = template.Short ?? "";
                        var duplicate = !string.Equals(newShort, shortText, StringComparison.OrdinalIgnoreCase)
                            && rewritten.OfType<BsonDocument>().Any(o => !ReferenceEquals(o, feature) && string.Equals(Str(o, "ShortDescription"), newShort, StringComparison.OrdinalIgnoreCase));
                        if (duplicate) { l.Outcome = FeatureUpdateOutcome.Conflict; }
                        else
                        {
                            l.Outcome = FeatureUpdateOutcome.Updated;
                            feature["_id"] = key + "#" + newShort.ToLowerInvariant();
                            feature["ShortDescription"] = newShort;
                            feature["LongDescription"] = template.Long ?? "";
                            feature["RoomLine"] = string.IsNullOrEmpty(template.RoomLine) ? (BsonValue)BsonNull.Value : template.RoomLine;
                            feature["DetailDescription"] = string.IsNullOrEmpty(template.Detail) ? (BsonValue)BsonNull.Value : template.Detail;
                            feature["Handles"] = new BsonArray((template.Handles ?? new List<string>()).Select(h => (BsonValue)h));
                            feature["Adjectives"] = new BsonArray((template.Adjectives ?? new List<string>()).Select(a => (BsonValue)a));
                            feature["TemplateVersion"] = template.Version;
                        }
                    }
                    line.Features.Add(l);
                }
                if (line.Features.Any(f => f.Outcome == FeatureUpdateOutcome.Updated))
                {
                    line.Outcome = RoomGenOutcome.Updated;
                    pending.Add(Tuple.Create(key, rewritten));
                }
                report.Rooms.Add(line);
            }

            if (!dryRun && pending.Count > 0)
            {
                Apply(report, pending.Select(p => p.Item1).ToList(), UpdateField, "template-referenced features refreshed, library version " + library.Version, () =>
                {
                    var result = new ImportResult();
                    foreach (var p in pending)
                    {
                        try { _importer.WriteFeatures(p.Item1, p.Item2); }
                        catch (Exception ex) when (!(ex is OutOfMemoryException)) { result.Errors.Add("Write failed for '" + p.Item1 + "': " + ex.Message); break; }
                    }
                    return result;
                });
            }
            return report;
        }

        // ------------------------------------------------------------------ helpers

        // Audit first, then write, then mark the audit rows applied or rejected.
        private void Apply(GenerationReport report, List<string> keys, string field, string summary, Func<ImportResult> write)
        {
            if (keys.Count == 0) { return; }
            var finish = _audit(keys.Select(k => new GenAuditEntry { Key = k, Field = field, Summary = summary }).ToList());
            if (finish == null) { report.AuditFailed = true; MarkAll(report, keys); return; }
            ImportResult result;
            try { result = write(); }
            catch (Exception ex) when (!(ex is OutOfMemoryException)) { result = new ImportResult(); result.Errors.Add(ex.Message); }
            finish(result.Success);
            if (!result.Success) { report.WriteFailed = true; report.Errors.AddRange(result.Errors); }
        }

        // The audit failed: nothing was written, so those rooms did not change.
        private static void MarkAll(GenerationReport report, List<string> keys)
        {
            foreach (var line in report.Rooms.Where(r => keys.Contains(r.Key)))
            {
                line.Outcome = RoomGenOutcome.Error;
                line.Detail = "not written";
                line.Features.Clear();
            }
        }

        private ILibrary OpenLibrary(GenerationReport report)
        {
            if (_library == null) { report.Errors.Add("The terrain library is not available."); return null; }
            try
            {
                var library = _library();
                if (library == null) { report.Errors.Add("The terrain library is not available."); return null; }
                report.LibraryVersion = library.Version;
                return library;
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                report.Errors.Add("The terrain library could not be read (" + ex.GetType().Name + "). Nothing was changed.");
                return null;
            }
        }

        private string MapPath(string map)
        {
            if (string.IsNullOrWhiteSpace(_contentDirectory) || string.IsNullOrWhiteSpace(map) || !AreaParser.PrefixPattern.IsMatch(map)) { return null; }
            return Path.Combine(_contentDirectory, MapFileService.MapSubdirectory, map + ".map.yaml");
        }

        private static RoomGenLine LineFor(RoomImportPlan plan, ImportResult dry, string detail)
        {
            var line = new RoomGenLine { Key = plan.Key, Detail = detail };
            if (dry.NotOwned.Contains(plan.Key)) { line.Outcome = RoomGenOutcome.NotOwned; }
            else if (plan.Action == ImportAction.Unchanged) { line.Outcome = RoomGenOutcome.Unchanged; }
            else { line.Outcome = RoomGenOutcome.Replaced; if (plan.Action == ImportAction.Insert) { line.Detail = "created"; } }
            return line;
        }

        // The generated area file with only the wanted rooms (full keys) left in its room list.
        private static string OnlyRooms(string yaml, string prefix, List<string> fullKeys)
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            var root = (YamlMappingNode)stream.Documents[0].RootNode;
            var rooms = (YamlSequenceNode)root.Children[new YamlScalarNode("rooms")];
            var keep = new YamlSequenceNode();
            foreach (var node in rooms.Children.OfType<YamlMappingNode>())
            {
                var key = ((YamlScalarNode)node.Children[new YamlScalarNode("key")]).Value;
                if (fullKeys.Contains(prefix + "." + key)) { keep.Add(node); }
            }
            root.Children[new YamlScalarNode("rooms")] = keep;
            var sb = new StringBuilder();
            using (var writer = new StringWriter(sb) { NewLine = "\n" }) { stream.Save(writer, false); }
            return sb.ToString();
        }

        private static BsonDocument GenOf(BsonDocument doc)
        {
            return doc == null ? null : doc.GetValue("gen", BsonNull.Value) as BsonDocument;
        }

        private static string Str(BsonDocument doc, string name)
        {
            var v = doc.GetValue(name, BsonNull.Value);
            return v.IsString ? v.AsString : null;
        }
    }
}
