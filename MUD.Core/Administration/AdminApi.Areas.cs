using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MUD.Contracts;
using MUD.Core.Areas;
using MUD.Core.GameObjects;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;

namespace MUD.Core.Administration
{
    // Area calls of the Admin service (specs/ADMIN_API.md "Areas"). An area record is organisational: id = the prefix, a name and a short description.
    public sealed partial class AdminApi
    {
        public const string AreaUnavailableMessage = "The area list is not available.";
        public const string AreaAlreadyExistsMessage = "An area with that prefix already exists.";
        public const string AreaNotFoundMessage = "No area with that prefix exists.";
        public const string AreaWriteFailedMessage = "The area could not be written. Nothing was changed.";
        public const string AreaNothingChangedMessage = "Nothing was changed.";
        public const string AreaEntity = "area";

        private readonly IAreaStore _areas;

        // ------------------------------------------------------------------ reads (creator or admin)

        public ListAreasReply ListAreas(TokenRequest request)
        {
            Authorize(request.Token, AdminRole.Creator);
            var reply = new ListAreasReply();
            if (_areas == null) { return reply; }
            IReadOnlyList<AreaEntry> all = ReadAreas(() => _areas.List());
            Dictionary<string, int> counts = ReadAreas(() => RoomCountsByArea());
            reply.Areas.AddRange(all.Select(a => ToRecord(a, CountOf(counts, a.Id))));
            return reply;
        }

        public GetAreaReply GetArea(AreaIdRequest request)
        {
            Authorize(request.Token, AdminRole.Creator);
            AreaEntry found = _areas == null ? null : ReadAreas(() => _areas.Get((request.Id ?? string.Empty).Trim()));
            if (found == null) { return new GetAreaReply { Found = false }; }
            Dictionary<string, int> counts = ReadAreas(() => RoomCountsByArea());
            return new GetAreaReply { Found = true, Area = ToRecord(found, CountOf(counts, found.Id)) };
        }

        // The store throws when the database cannot be reached; the caller sees "core could not answer" (gRPC Unavailable), not a result code.
        private T ReadAreas<T>(Func<T> read)
        {
            try { return read(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log("Area read failed (" + ex.GetType().Name + ").");
                throw new AdminCallException(AdminFailure.Unavailable, "The areas could not be read.");
            }
        }

        // Rooms per Area value. A room edited here may still be queued for the database, so the live room wins (as in ListRooms).
        private Dictionary<string, int> RoomCountsByArea()
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            ISnapshotRepository snapshots = _rooms as ISnapshotRepository;
            HashSet<string> cached = snapshots == null ? new HashSet<string>() : new HashSet<string>(snapshots.CachedIds(), StringComparer.Ordinal);
            foreach (BsonDocument document in _queries.ListRooms())
            {
                string area;
                string key = document["_id"].ToString();
                Room live = cached.Contains(key) ? _rooms.Get(key) : null;
                if (live != null) { area = live.Area; }
                else { area = document.TryGetValue("Area", out BsonValue v) && v.IsString ? v.AsString : null; }
                if (string.IsNullOrEmpty(area)) { continue; }
                counts[area] = CountOf(counts, area) + 1;
            }
            return counts;
        }

        private static int CountOf(Dictionary<string, int> counts, string id) { return counts.TryGetValue(id, out int n) ? n : 0; }

        // ------------------------------------------------------------------ writes (creator or admin, audited first)

        public AreaEditReply CreateArea(SaveAreaRequest request)
        {
            return Mutate(request.Token, AdminRole.Creator, actor => SaveArea(actor, request.Area, create: true));
        }

        public AreaEditReply UpdateArea(SaveAreaRequest request)
        {
            return Mutate(request.Token, AdminRole.Creator, actor => SaveArea(actor, request.Area, create: false));
        }

        public AreaEditReply DeleteArea(AreaIdRequest request)
        {
            return Mutate(request.Token, AdminRole.Creator, actor => RemoveArea(actor, request.Id));
        }

        private AreaEditReply SaveArea(Actor actor, AreaRecord record, bool create)
        {
            if (_areas == null) { return AreaReply(AdminResult.Refused, AreaUnavailableMessage); }
            if (record == null) { return AreaReply(AdminResult.Invalid, null, new[] { AdminPlayerValidator.Error("record", "The area is missing.") }); }

            string id = (record.Id ?? string.Empty).Trim();
            string name = RoomRules.NormalizeText(record.Name ?? string.Empty);
            string description = RoomRules.NormalizeText(record.Description ?? string.Empty);
            var errors = new List<FieldError>();
            string problem = RoomRules.CheckAreaPrefix(id);
            if (problem != null) { errors.Add(AdminPlayerValidator.Error("Id", AdminPlayerValidator.Capitalize(problem))); }
            problem = AreaRules.CheckName(name);
            if (problem != null) { errors.Add(AdminPlayerValidator.Error("Name", AdminPlayerValidator.Capitalize(problem))); }
            problem = AreaRules.CheckDescription(description);
            if (problem != null) { errors.Add(AdminPlayerValidator.Error("Description", AdminPlayerValidator.Capitalize(problem))); }
            if (errors.Count > 0) { return AreaReply(AdminResult.Invalid, null, errors); }

            AreaEntry stored;
            try { stored = _areas.Get(id); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return AreaFailed("read", id, ex); }
            if (create && stored != null) { return AreaReply(AdminResult.AlreadyExists, AreaAlreadyExistsMessage); }
            if (!create && stored == null) { return AreaReply(AdminResult.NotFound, AreaNotFoundMessage); }

            var entry = new AreaEntry { Id = id, Name = name, Description = description };
            var rows = new List<AuditChange>();
            if (create) { rows.Add(new AuditChange("*create*", BsonNull.Value, AreaDocuments.ToDocument(entry))); }
            else
            {
                // Only the name and the description can change; the id is the key.
                if (!string.Equals(stored.Name, name, StringComparison.Ordinal)) { rows.Add(new AuditChange("name", stored.Name ?? string.Empty, name)); }
                if (!string.Equals(stored.Description ?? string.Empty, description, StringComparison.Ordinal)) { rows.Add(new AuditChange("description", stored.Description ?? string.Empty, description)); }
                if (rows.Count == 0) { return AreaReply(AdminResult.NoChange, AreaNothingChangedMessage, null, stored); }
            }

            List<AuditRecord> entries = BuildEntries(actor, AreaEntity, id, id, rows);
            if (!TryAudit(entries)) { return AreaReply(AdminResult.AuditFailed, null); }

            bool written;
            try { written = create ? _areas.Insert(entry, actor.Player.PlayerName) : _areas.Replace(entry, actor.Player.PlayerName); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Finish(entries, false);
                return AreaFailed("write", id, ex);
            }
            Finish(entries, written);
            // Someone else created or removed the id between our read and our write.
            if (!written) { return create ? AreaReply(AdminResult.AlreadyExists, AreaAlreadyExistsMessage) : AreaReply(AdminResult.NotFound, AreaNotFoundMessage); }
            return AreaReply(AdminResult.Ok, null, null, entry);
        }

        private AreaEditReply RemoveArea(Actor actor, string rawId)
        {
            if (_areas == null) { return AreaReply(AdminResult.Refused, AreaUnavailableMessage); }
            string id = (rawId ?? string.Empty).Trim();
            AreaEntry stored;
            int roomCount, terrainTypeCount;
            try
            {
                stored = id.Length == 0 ? null : _areas.Get(id);
                roomCount = stored == null ? 0 : CountOf(RoomCountsByArea(), id);
                terrainTypeCount = stored == null ? 0 : CountTerrainTypesInArea(id);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return AreaFailed("read", id, ex); }
            if (stored == null) { return AreaReply(AdminResult.NotFound, AreaNotFoundMessage); }
            // Item templates are not counted (see specs/ADMIN_API.md "Areas"); rooms and terrain types are.
            if (roomCount > 0 || terrainTypeCount > 0)
            {
                return AreaReply(AdminResult.Refused, AreaInUseMessage(roomCount, terrainTypeCount), null, stored);
            }

            List<AuditRecord> entries = BuildEntries(actor, AreaEntity, id, id, new List<AuditChange> { new AuditChange("*delete*", AreaDocuments.ToDocument(stored), BsonNull.Value) });
            if (!TryAudit(entries)) { return AreaReply(AdminResult.AuditFailed, null); }

            bool deleted;
            try { deleted = _areas.Delete(id); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Finish(entries, false);
                return AreaFailed("delete", id, ex);
            }
            Finish(entries, deleted);
            return deleted ? AreaReply(AdminResult.Ok, null) : AreaReply(AdminResult.NotFound, AreaNotFoundMessage);
        }

        // "3 rooms still use this area. ...", "2 terrain types still use this area. ...", "1 room and 4 terrain types still use this area. ...".
        public static string AreaInUseMessage(int rooms, int terrainTypes)
        {
            var parts = new List<string>();
            if (rooms > 0) { parts.Add(rooms == 1 ? "1 room" : rooms + " rooms"); }
            if (terrainTypes > 0) { parts.Add(terrainTypes == 1 ? "1 terrain type" : terrainTypes + " terrain types"); }
            bool singular = parts.Count == 1 && (rooms == 1 || terrainTypes == 1);
            return string.Join(" and ", parts) + (singular ? " still uses" : " still use") + " this area. Move or delete them first. Nothing was changed.";
        }

        // The backstop of CreateRoom and MoveRoom: an area with no record is refused. With no area store (not configured) nothing can be checked.
        private FieldError MissingAreaError(string area, string field)
        {
            if (_areas == null) { return null; }
            if (_areas.Get(area) != null) { return null; }
            return AdminPlayerValidator.Error(field, "There is no area with that prefix. Create the area first.");
        }

        private AreaEditReply AreaFailed(string what, string id, Exception ex)
        {
            // Generic result: no exception text, no values.
            _log("Area " + what + " of " + id + " failed (" + ex.GetType().Name + ").");
            return AreaReply(AdminResult.Failed, AreaWriteFailedMessage);
        }

        private static AreaEditReply AreaReply(AdminResult result, string message, IEnumerable<FieldError> errors = null, AreaEntry entry = null)
        {
            var reply = new AreaEditReply { Result = result, Message = message ?? string.Empty };
            if (errors != null) { reply.Errors.AddRange(errors); }
            if (entry != null) { reply.Area = ToRecord(entry, 0); }
            return reply;
        }

        public static AreaRecord ToRecord(AreaEntry a, int roomCount)
        {
            return new AreaRecord { Id = a.Id ?? string.Empty, Name = a.Name ?? string.Empty, Description = a.Description ?? string.Empty, RoomCount = roomCount };
        }
    }
}
