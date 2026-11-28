using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MUD.Core.Generation.Terrains
{
    // Persistence seam of the detail-into-long migration: raw documents only, because neither Feature nor FeatureTemplate knows `detail` any more.
    public interface IFeatureDetailStore
    {
        // Rooms that have at least one embedded feature with a DetailDescription element (even a null one). Each document carries _id and Features.
        IReadOnlyList<BsonDocument> RoomsWithFeatureDetail();
        // Field-level write on one room: $set the given dotted paths, $unset the listed ones.
        void UpdateRoom(string id, BsonDocument set, IReadOnlyList<string> unset);
        // Terrain feature templates that still have a `detail` element.
        IReadOnlyList<BsonDocument> TemplatesWithDetail();
        void UpdateTemplate(string id, BsonDocument set, IReadOnlyList<string> unset);
    }

    public sealed class MongoFeatureDetailStore : IFeatureDetailStore
    {
        private readonly IMongoCollection<BsonDocument> _rooms;
        private readonly IMongoCollection<BsonDocument> _templates;

        public MongoFeatureDetailStore(IMongoDatabase database)
        {
            _rooms = database.GetCollection<BsonDocument>("room");
            _templates = database.GetCollection<BsonDocument>(LibraryDocuments.FeaturesCollection);
        }

        public IReadOnlyList<BsonDocument> RoomsWithFeatureDetail()
        {
            return _rooms.Find(new BsonDocument("Features.DetailDescription", new BsonDocument("$exists", true)))
                .Project<BsonDocument>(Builders<BsonDocument>.Projection.Include("Features")).ToList();
        }

        public void UpdateRoom(string id, BsonDocument set, IReadOnlyList<string> unset) { Update(_rooms, id, set, unset); }

        public IReadOnlyList<BsonDocument> TemplatesWithDetail()
        {
            return _templates.Find(new BsonDocument("detail", new BsonDocument("$exists", true))).ToList();
        }

        public void UpdateTemplate(string id, BsonDocument set, IReadOnlyList<string> unset) { Update(_templates, id, set, unset); }

        private static void Update(IMongoCollection<BsonDocument> collection, string id, BsonDocument set, IReadOnlyList<string> unset)
        {
            var update = new BsonDocument();
            if (set.ElementCount > 0) { update["$set"] = set; }
            if (unset.Count > 0) { update["$unset"] = new BsonDocument(unset.Select(u => new BsonElement(u, 1))); }
            collection.UpdateOne(new BsonDocument("_id", id), update);
        }
    }

    public sealed class FeatureDetailMigrationResult
    {
        // Room features whose look text was replaced by their detail (long differed, or was empty).
        public int RoomFeaturesMerged;
        // Room features that only lost an empty / null / identical detail element.
        public int RoomFeaturesCleaned;
        public int RoomsChanged;
        // Templates whose long was replaced by their detail (and whose version was bumped).
        public int TemplatesMerged;
        public int TemplatesCleaned;
        public bool Changed { get { return RoomsChanged > 0 || TemplatesMerged > 0 || TemplatesCleaned > 0; } }
    }

    // Startup step, run before the terrain library migration: a feature has ONE look text, `long`. Where a stored feature or template has a
    // non-empty detail, long becomes that detail (detail is what players have been seeing; the old long was never shown and is discarded) and the
    // detail element is removed. Where detail is empty the element is just removed and long is kept. Everything else (handles, templateId,
    // templateVersion, behaviours) is untouched. A template's version is bumped only when its long actually changed, so `update` sees it; the
    // library version is not bumped. Idempotent: once no document has a detail element the step finds nothing to do.
    public static class FeatureDetailMigration
    {
        public static FeatureDetailMigrationResult Run(IFeatureDetailStore store, Action<string> log)
        {
            log = log ?? (_ => { });
            var result = new FeatureDetailMigrationResult();

            foreach (var room in store.RoomsWithFeatureDetail())
            {
                var id = room.GetValue("_id", BsonNull.Value);
                var features = room.GetValue("Features", BsonNull.Value) as BsonArray;
                if (!id.IsString || features == null) { continue; }
                var set = new BsonDocument();
                var unset = new List<string>();
                int merged = 0, cleaned = 0;
                for (int i = 0; i < features.Count; i++)
                {
                    var feature = features[i] as BsonDocument;
                    if (feature == null || !feature.Contains("DetailDescription")) { continue; }
                    var detail = feature["DetailDescription"];
                    var text = detail.IsString ? detail.AsString : string.Empty;
                    var oldLong = feature.GetValue("LongDescription", BsonNull.Value);
                    if (text.Length > 0 && !(oldLong.IsString && oldLong.AsString == text))
                    {
                        set["Features." + i + ".LongDescription"] = text;
                        merged++;
                    }
                    else { cleaned++; }
                    unset.Add("Features." + i + ".DetailDescription");
                }
                if (unset.Count == 0) { continue; }
                store.UpdateRoom(id.AsString, set, unset);
                result.RoomsChanged++;
                result.RoomFeaturesMerged += merged;
                result.RoomFeaturesCleaned += cleaned;
            }

            foreach (var template in store.TemplatesWithDetail())
            {
                var id = template.GetValue("_id", BsonNull.Value);
                if (!id.IsString) { continue; }
                var detail = template.GetValue("detail", BsonNull.Value);
                var text = detail.IsString ? detail.AsString : string.Empty;
                var oldLong = template.GetValue("long", BsonNull.Value);
                var set = new BsonDocument();
                if (text.Length > 0 && !(oldLong.IsString && oldLong.AsString == text))
                {
                    var version = template.GetValue("version", 1);
                    set["long"] = text;
                    set["version"] = (version.IsNumeric ? version.ToInt32() : 1) + 1;
                    result.TemplatesMerged++;
                }
                else { result.TemplatesCleaned++; }
                store.UpdateTemplate(id.AsString, set, new[] { "detail" });
            }

            if (result.Changed)
            {
                log(string.Format("Feature detail migration: {0} room feature(s) in {1} room(s) had their long text replaced by their detail and {2} lost an empty or identical detail; " +
                    "{3} terrain feature template(s) had long replaced by detail (version bumped) and {4} lost an empty or identical detail. " +
                    "The old long text of a merged feature was never shown to players and is discarded. Generated rooms keep their templateVersion until `update` is run.",
                    result.RoomFeaturesMerged, result.RoomsChanged, result.RoomFeaturesCleaned, result.TemplatesMerged, result.TemplatesCleaned));
            }
            return result;
        }
    }
}
