using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MUD.Contracts;
using MUD.Core.Generation;
using MUD.Core.Import;

namespace MUD.Core.Administration
{
    // Regen and update of generated rooms (specs/TERRAIN_GENERATORS.md sections 9 and 10).
    public sealed partial class AdminApi
    {
        public const int MaxGenerationKeys = 2000;

        public GenerationReply RegenerateMap(RegenerateMapRequest request)
        {
            return Generate(request.Token, request.DryRun, false, svc => svc.RegenerateMap(request.Map, request.DryRun, request.NewSeed));
        }

        public GenerationReply RegenerateRooms(RegenerateRoomsRequest request)
        {
            return Generate(request.Token, request.DryRun, false, svc => svc.RegenerateRooms(request.Keys, request.DryRun, request.NewSeed));
        }

        public GenerationReply UpdateRooms(UpdateRoomsRequest request)
        {
            return Generate(request.Token, request.DryRun, true, svc => svc.UpdateRooms(request.Keys, request.DryRun));
        }

        // Legend colours of every map file (web map view only). Reads files; no game state. Files that are not maps give nothing.
        public TerrainColorsReply ListTerrainColors(TokenRequest request)
        {
            Authorize(request.Token, AdminRole.Creator);
            var reply = new TerrainColorsReply();
            if (string.IsNullOrWhiteSpace(_contentDirectory)) { return reply; }
            string dir = System.IO.Path.Combine(_contentDirectory, MapFileService.MapSubdirectory);
            if (!System.IO.Directory.Exists(dir)) { return reply; }
            const string suffix = ".map.yaml";
            foreach (string path in System.IO.Directory.GetFiles(dir, "*" + suffix).OrderBy(p => p, StringComparer.Ordinal))
            {
                string name = System.IO.Path.GetFileName(path);
                name = name.Substring(0, name.Length - suffix.Length);
                try
                {
                    var colors = MapGenerator.ReadLegendColors(System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8));
                    foreach (var pair in colors.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        reply.Colors.Add(new TerrainColor { Map = name, Terrain = pair.Key, Color = pair.Value });
                    }
                }
                catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { }
            }
            return reply;
        }

        // Game thread (the repository write refreshes the live rooms there), creator or admin, audited first.
        private GenerationReply Generate(string token, bool dryRun, bool update, Func<GenerationService, GenerationReport> run)
        {
            return Mutate(token, AdminRole.Creator, actor =>
            {
                if (_libraryCache == null) { return new GenerationReply { Result = AdminResult.Refused, DryRun = dryRun, Errors = { LibraryUnavailableMessage } }; }
                var audits = new List<Tuple<GenerationService, List<AuditRecord>>>();
                var service = new GenerationService(new RoomRepositoryImportStore(_rooms), () => { _libraryCache.Refresh(); return _libraryCache; }, _contentDirectory,
                    entries =>
                    {
                        List<AuditRecord> records = entries.SelectMany(e => BuildEntries(actor, "room", e.Key, e.Key, new List<AuditChange> { new AuditChange(e.Field, BsonNull.Value, e.Summary) })).ToList();
                        if (!TryAudit(records)) { return null; }
                        return applied => Finish(records, applied);
                    });
                try
                {
                    return ToReply(run(service), update);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Generic result: no exception text, no stored values.
                    _log("Generation failed (" + ex.GetType().Name + ").");
                    return new GenerationReply { Result = AdminResult.Failed, DryRun = dryRun };
                }
            });
        }

        private static GenerationReply ToReply(GenerationReport report, bool update)
        {
            var reply = new GenerationReply { DryRun = report.DryRun, LibraryVersion = report.LibraryVersion };
            reply.Errors.AddRange(report.Errors);
            reply.Orphans.AddRange(report.Orphans);
            foreach (MUD.Core.Generation.RoomGenLine line in report.Rooms)
            {
                var r = new MUD.Contracts.RoomGenLine { Key = line.Key, Outcome = ToContract(line.Outcome), Detail = line.Detail ?? string.Empty };
                r.Features.AddRange(line.Features.Select(f => new MUD.Contracts.FeatureUpdateLine
                {
                    FeatureShort = f.Short ?? string.Empty, TemplateId = f.TemplateId ?? string.Empty, Outcome = ToContract(f.Outcome), FromVersion = f.FromVersion, ToVersion = f.ToVersion,
                }));
                reply.Rooms.Add(r);
            }
            if (update)
            {
                reply.UpdatedCount = report.CountFeatures(MUD.Core.Generation.FeatureUpdateOutcome.Updated);
                reply.UnchangedCount = report.CountFeatures(MUD.Core.Generation.FeatureUpdateOutcome.Current);
                reply.DeletedTemplateCount = report.CountFeatures(MUD.Core.Generation.FeatureUpdateOutcome.TemplateDeleted);
            }
            else
            {
                reply.UpdatedCount = report.Count(MUD.Core.Generation.RoomGenOutcome.Replaced);
                reply.UnchangedCount = report.Count(MUD.Core.Generation.RoomGenOutcome.Unchanged);
            }

            if (report.AuditFailed) { reply.Result = AdminResult.AuditFailed; }
            else if (report.WriteFailed) { reply.Result = AdminResult.Failed; }
            else if (report.Errors.Count > 0 && report.Rooms.Count == 0) { reply.Result = report.Errors.Any(e => e.StartsWith("The terrain library")) ? AdminResult.Refused : AdminResult.Invalid; }
            else if (reply.UpdatedCount == 0 && !report.Rooms.Any(r => r.Outcome == MUD.Core.Generation.RoomGenOutcome.Error || r.Outcome == MUD.Core.Generation.RoomGenOutcome.MapNotFound || r.Outcome == MUD.Core.Generation.RoomGenOutcome.NotGenerated || r.Outcome == MUD.Core.Generation.RoomGenOutcome.NotOwned)) { reply.Result = AdminResult.NoChange; }
            else { reply.Result = AdminResult.Ok; }
            return reply;
        }

        private static MUD.Contracts.RoomGenOutcome ToContract(MUD.Core.Generation.RoomGenOutcome o)
        {
            switch (o)
            {
                case MUD.Core.Generation.RoomGenOutcome.Replaced: return MUD.Contracts.RoomGenOutcome.RoomGenReplaced;
                case MUD.Core.Generation.RoomGenOutcome.Unchanged: return MUD.Contracts.RoomGenOutcome.RoomGenUnchanged;
                case MUD.Core.Generation.RoomGenOutcome.NotOwned: return MUD.Contracts.RoomGenOutcome.RoomGenNotOwned;
                case MUD.Core.Generation.RoomGenOutcome.MapNotFound: return MUD.Contracts.RoomGenOutcome.RoomGenMapNotFound;
                case MUD.Core.Generation.RoomGenOutcome.Updated: return MUD.Contracts.RoomGenOutcome.RoomGenUpdated;
                case MUD.Core.Generation.RoomGenOutcome.NotGenerated: return MUD.Contracts.RoomGenOutcome.RoomGenNotGenerated;
                default: return MUD.Contracts.RoomGenOutcome.RoomGenError;
            }
        }

        private static MUD.Contracts.FeatureUpdateOutcome ToContract(MUD.Core.Generation.FeatureUpdateOutcome o)
        {
            switch (o)
            {
                case MUD.Core.Generation.FeatureUpdateOutcome.Updated: return MUD.Contracts.FeatureUpdateOutcome.FeatureUpdated;
                case MUD.Core.Generation.FeatureUpdateOutcome.Current: return MUD.Contracts.FeatureUpdateOutcome.FeatureCurrent;
                case MUD.Core.Generation.FeatureUpdateOutcome.TemplateDeleted: return MUD.Contracts.FeatureUpdateOutcome.FeatureTemplateDeleted;
                case MUD.Core.Generation.FeatureUpdateOutcome.NewerInRoom: return MUD.Contracts.FeatureUpdateOutcome.FeatureNewerInRoom;
                default: return MUD.Contracts.FeatureUpdateOutcome.FeatureConflict;
            }
        }
    }
}
