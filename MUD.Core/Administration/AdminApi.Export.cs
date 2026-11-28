using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MongoDB.Bson;
using MUD.Contracts;
using MUD.Core.GameObjects;
using MUD.Core.Import;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;

namespace MUD.Core.Administration
{
    // Area export for the website (specs/ADMIN_API.md "Area export"). Read-only: no audit row, nothing written, no file on the server.
    public sealed partial class AdminApi
    {
        // The reply stays under gRPC's default 4 MB message limit; a larger file is refused with this text.
        public const int MaxExportBytes = 3 * 1024 * 1024;
        public const string ExportTooLargeMessage = "The area file is larger than 3 MB, which is too large to download here. Export it by room range with the in-game 'export' command instead.";
        public const string ExportUnavailableMessage = "The area could not be read. Nothing was exported.";

        public ExportAreaReply ExportArea(ExportAreaRequest request)
        {
            // Game thread, like the other room reads that need the live rooms to agree with the database; the role check is inside the same hop.
            return Mutate(request.Token, AdminRole.Creator, actor => BuildExport((request.Area ?? string.Empty).Trim()));
        }

        private ExportAreaReply BuildExport(string prefix)
        {
            var reply = new ExportAreaReply();
            if (prefix.Length == 0 || !AreaParser.PrefixPattern.IsMatch(prefix)) { return reply; }
            AreaExportResult result;
            try
            {
                var store = new LiveRoomImportStore(_rooms);
                if (store.FindIdsByArea(prefix).Count == 0) { return reply; }
                result = new AreaExporter(store, _areas).ExportArea(prefix);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Generic result: no exception text, no stored values.
                _log("Area export failed (" + ex.GetType().Name + ").");
                throw new AdminCallException(AdminFailure.Unavailable, ExportUnavailableMessage);
            }

            reply.Found = true;
            if (!result.Success)
            {
                reply.Errors.AddRange(result.Errors);
                return reply;
            }
            if (Encoding.UTF8.GetByteCount(result.Yaml) > MaxExportBytes)
            {
                reply.Errors.Add(ExportTooLargeMessage);
                return reply;
            }
            reply.Yaml = result.Yaml;
            reply.RoomCount = result.RoomCount;
            if (result.GeneratedRoomCount > 0)
            {
                // The exporter does not write a room's generator provenance (x-gen), so the file holds these rooms as ordinary rooms:
                // importing it keeps their text as it is and never regenerates (and so never wipes) them.
                reply.Notes.Add(string.Format("{0} room{1} {2} generated. The file keeps {3} text as ordinary rooms without generator provenance; re-importing does not regenerate {4}.",
                    result.GeneratedRoomCount, result.GeneratedRoomCount == 1 ? string.Empty : "s", result.GeneratedRoomCount == 1 ? "is" : "are",
                    result.GeneratedRoomCount == 1 ? "its" : "their", result.GeneratedRoomCount == 1 ? "it" : "them"));
            }
            return reply;
        }

        // The importer's store over the live game: a room that is cached is read from the live room (an edit may still be queued for the
        // database), every other room from its stored document. Read-only: Upsert is never called by the exporter.
        private sealed class LiveRoomImportStore : IRoomImportStore
        {
            private readonly IRoomRepository _repository;
            private readonly HashSet<string> _cached;

            public LiveRoomImportStore(IRoomRepository repository)
            {
                _repository = repository;
                var snapshots = repository as ISnapshotRepository;
                _cached = snapshots == null ? new HashSet<string>() : new HashSet<string>(snapshots.CachedIds(), StringComparer.Ordinal);
            }

            public BsonDocument Find(string id)
            {
                Room live = _cached.Contains(id) ? _repository.Get(id) : null;
                return live != null ? live.ToBsonDocument() : _repository.GetRawDocument(id);
            }

            public List<string> FindIdsByArea(string area)
            {
                var ids = new HashSet<string>(_repository.GetIdsByField("Area", area), StringComparer.Ordinal);
                foreach (string key in _cached)
                {
                    Room live = _repository.Get(key);
                    if (live == null) { continue; }
                    if (live.Area == area) { ids.Add(key); } else { ids.Remove(key); }
                }
                return ids.ToList();
            }

            public void Upsert(string id, BsonDocument set, BsonDocument setOnInsert)
            {
                throw new InvalidOperationException("Export is read-only.");
            }
        }
    }
}
