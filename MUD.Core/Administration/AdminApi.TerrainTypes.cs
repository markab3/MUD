using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MUD.Contracts;
using MUD.Core.Generation.Terrains;
using MUD.Data;

namespace MUD.Core.Administration
{
    // Terrain type calls of the Admin service (specs/ADMIN_API.md "Terrain types"). A terrain type belongs to an area and owns the library
    // entries scoped to it. Built on the Areas pattern: storage seam ILibraryStore.TerrainTypes, audit first, Mutate on the game thread.
    public sealed partial class AdminApi
    {
        public const string TerrainTypeUnavailableMessage = "The terrain library is not available.";
        public const string TerrainTypeAlreadyExistsMessage = "A terrain type with that id already exists.";
        public const string TerrainTypeNotFoundMessage = "No terrain type with that id exists.";
        public const string TerrainTypeWriteFailedMessage = "The terrain type could not be written. Nothing was changed.";
        public const string TerrainTypeNothingChangedMessage = "Nothing was changed.";
        public const string TerrainTypeEntity = "terrain_type";

        // ------------------------------------------------------------------ reads (creator or admin)

        public ListTerrainTypesReply ListTerrainTypes(ListTerrainTypesRequest request)
        {
            Authorize(request.Token, AdminRole.Creator);
            var reply = new ListTerrainTypesReply();
            if (_library == null) { return reply; }
            string area = (request.Area ?? string.Empty).Trim();
            reply.TerrainTypes.AddRange(ReadLibrary(() => _library.TerrainTypes.List()).Where(t => area.Length == 0 || t.Area == area).Select(ToRecord));
            return reply;
        }

        public GetTerrainTypeReply GetTerrainType(TerrainTypeIdRequest request)
        {
            Authorize(request.Token, AdminRole.Creator);
            TerrainType found = _library == null ? null : ReadLibrary(() => _library.TerrainTypes.Get((request.Id ?? string.Empty).Trim()));
            return found == null ? new GetTerrainTypeReply { Found = false } : new GetTerrainTypeReply { Found = true, TerrainType = ToRecord(found) };
        }

        // ------------------------------------------------------------------ writes (creator or admin, audited first)

        public TerrainTypeEditReply CreateTerrainType(SaveTerrainTypeRequest request)
        {
            return Mutate(request.Token, AdminRole.Creator, actor => SaveTerrainType(actor, request.TerrainType, create: true));
        }

        public TerrainTypeEditReply UpdateTerrainType(SaveTerrainTypeRequest request)
        {
            return Mutate(request.Token, AdminRole.Creator, actor => SaveTerrainType(actor, request.TerrainType, create: false));
        }

        public TerrainTypeEditReply DeleteTerrainType(TerrainTypeIdRequest request)
        {
            return Mutate(request.Token, AdminRole.Creator, actor => RemoveTerrainType(actor, request.Id));
        }

        private TerrainTypeEditReply SaveTerrainType(Actor actor, TerrainTypeRecord record, bool create)
        {
            if (_library == null || _areas == null) { return TerrainTypeReply(AdminResult.Refused, TerrainTypeUnavailableMessage); }
            if (record == null) { return TerrainTypeReply(AdminResult.Invalid, null, new[] { AdminPlayerValidator.Error("record", "The terrain type is missing.") }); }

            string id = (record.Id ?? string.Empty).Trim();
            string name = RoomRules.NormalizeText(record.Name ?? string.Empty);
            string description = RoomRules.NormalizeText(record.Description ?? string.Empty);
            var errors = new List<FieldError>();
            string problem = TerrainTypeRules.CheckId(id);
            if (problem != null) { errors.Add(AdminPlayerValidator.Error("Id", problem)); }
            problem = AreaRules.CheckName(name);
            if (problem != null) { errors.Add(AdminPlayerValidator.Error("Name", AdminPlayerValidator.Capitalize(problem))); }
            problem = AreaRules.CheckDescription(description);
            if (problem != null) { errors.Add(AdminPlayerValidator.Error("Description", AdminPlayerValidator.Capitalize(problem))); }
            if (errors.Count > 0) { return TerrainTypeReply(AdminResult.Invalid, null, errors); }

            TerrainType stored;
            try
            {
                // The area must have a record (the website offers only existing areas); only a create can name a new one.
                if (create && _areas.Get(TerrainTypeRules.AreaOf(id)) == null)
                {
                    return TerrainTypeReply(AdminResult.Invalid, null, new[] { AdminPlayerValidator.Error("Id", "There is no area with that prefix. Create the area first.") });
                }
                stored = _library.TerrainTypes.Get(id);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return TerrainTypeFailed("read", id, ex); }
            if (create && stored != null) { return TerrainTypeReply(AdminResult.AlreadyExists, TerrainTypeAlreadyExistsMessage); }
            if (!create && stored == null) { return TerrainTypeReply(AdminResult.NotFound, TerrainTypeNotFoundMessage); }

            var entry = new TerrainType { Id = id, Name = name, Description = description };
            var rows = new List<AuditChange>();
            if (create) { rows.Add(new AuditChange("*create*", BsonNull.Value, TerrainTypeDocuments.ToDocument(entry))); }
            else
            {
                // Only the name and the description can change; the id (and so the area) is the key.
                if (!string.Equals(stored.Name, name, StringComparison.Ordinal)) { rows.Add(new AuditChange("name", stored.Name ?? string.Empty, name)); }
                if (!string.Equals(stored.Description ?? string.Empty, description, StringComparison.Ordinal)) { rows.Add(new AuditChange("description", stored.Description ?? string.Empty, description)); }
                if (rows.Count == 0) { return TerrainTypeReply(AdminResult.NoChange, TerrainTypeNothingChangedMessage, null, stored); }
            }

            List<AuditRecord> entries = BuildEntries(actor, TerrainTypeEntity, id, id, rows);
            if (!TryAudit(entries)) { return TerrainTypeReply(AdminResult.AuditFailed, null); }

            bool written;
            try { written = create ? _library.TerrainTypes.Insert(entry, actor.Player.PlayerName) : _library.TerrainTypes.Replace(entry, actor.Player.PlayerName); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Finish(entries, false);
                return TerrainTypeFailed("write", id, ex);
            }
            Finish(entries, written);
            // Someone else created or removed the id between our read and our write.
            if (!written) { return create ? TerrainTypeReply(AdminResult.AlreadyExists, TerrainTypeAlreadyExistsMessage) : TerrainTypeReply(AdminResult.NotFound, TerrainTypeNotFoundMessage); }
            return TerrainTypeReply(AdminResult.Ok, null, null, entry);
        }

        // What still points at a terrain type: scoped entries and phrases that name it as neighbour.
        private sealed class TerrainTypeUse
        {
            public int Fragments, Features, Phrases, AsNeighbor;
            public bool Any { get { return Fragments + Features + Phrases + AsNeighbor > 0; } }
        }

        private TerrainTypeUse UseOfTerrainType(string id)
        {
            return new TerrainTypeUse
            {
                Fragments = _library.Fragments.List().Count(e => e.Terrain == id),
                Features = _library.Features.List().Count(e => e.Terrain == id),
                Phrases = _library.Phrases.List().Count(e => e.Terrain == id),
                AsNeighbor = _library.Phrases.List().Count(e => e.NeighborTerrain == id),
            };
        }

        private static string Plural(int n, string one, string many) { return n + " " + (n == 1 ? one : many); }

        public static string TerrainTypeInUseMessage(int fragments, int features, int phrases, int asNeighbor)
        {
            var parts = new List<string>();
            if (fragments > 0) { parts.Add(Plural(fragments, "description fragment", "description fragments")); }
            if (features > 0) { parts.Add(Plural(features, "feature template", "feature templates")); }
            if (phrases > 0) { parts.Add(Plural(phrases, "neighbour phrase", "neighbour phrases")); }
            if (asNeighbor > 0) { parts.Add(Plural(asNeighbor, "neighbour phrase", "neighbour phrases") + " (as neighbour terrain)"); }
            string list = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[parts.Count - 1];
            return "This terrain type is still in use by " + list + ". Move or delete them first. Nothing was changed.";
        }

        private TerrainTypeEditReply RemoveTerrainType(Actor actor, string rawId)
        {
            if (_library == null) { return TerrainTypeReply(AdminResult.Refused, TerrainTypeUnavailableMessage); }
            string id = (rawId ?? string.Empty).Trim();
            TerrainType stored;
            TerrainTypeUse use = null;
            try
            {
                stored = id.Length == 0 ? null : _library.TerrainTypes.Get(id);
                if (stored != null) { use = UseOfTerrainType(id); }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return TerrainTypeFailed("read", id, ex); }
            if (stored == null) { return TerrainTypeReply(AdminResult.NotFound, TerrainTypeNotFoundMessage); }
            if (use.Any) { return TerrainTypeReply(AdminResult.Refused, TerrainTypeInUseMessage(use.Fragments, use.Features, use.Phrases, use.AsNeighbor), null, stored); }

            List<AuditRecord> entries = BuildEntries(actor, TerrainTypeEntity, id, id, new List<AuditChange> { new AuditChange("*delete*", TerrainTypeDocuments.ToDocument(stored), BsonNull.Value) });
            if (!TryAudit(entries)) { return TerrainTypeReply(AdminResult.AuditFailed, null); }

            bool deleted;
            try { deleted = _library.TerrainTypes.Delete(id); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Finish(entries, false);
                return TerrainTypeFailed("delete", id, ex);
            }
            Finish(entries, deleted);
            return deleted ? TerrainTypeReply(AdminResult.Ok, null) : TerrainTypeReply(AdminResult.NotFound, TerrainTypeNotFoundMessage);
        }

        // Terrain types of an area, for the area delete refusal. Zero when there is no library.
        private int CountTerrainTypesInArea(string area)
        {
            return _library == null ? 0 : _library.TerrainTypes.List().Count(t => t.Area == area);
        }

        private TerrainTypeEditReply TerrainTypeFailed(string what, string id, Exception ex)
        {
            // Generic result: no exception text, no values.
            _log("Terrain type " + what + " of " + id + " failed (" + ex.GetType().Name + ").");
            return TerrainTypeReply(AdminResult.Failed, TerrainTypeWriteFailedMessage);
        }

        private static TerrainTypeEditReply TerrainTypeReply(AdminResult result, string message, IEnumerable<FieldError> errors = null, TerrainType entry = null)
        {
            var reply = new TerrainTypeEditReply { Result = result, Message = message ?? string.Empty };
            if (errors != null) { reply.Errors.AddRange(errors); }
            if (entry != null) { reply.TerrainType = ToRecord(entry); }
            return reply;
        }

        public static TerrainTypeRecord ToRecord(TerrainType t)
        {
            return new TerrainTypeRecord { Id = t.Id ?? string.Empty, Area = t.Area, Name = t.Name ?? string.Empty, Description = t.Description ?? string.Empty };
        }
    }
}
