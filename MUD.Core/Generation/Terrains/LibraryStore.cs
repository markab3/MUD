using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MUD.Core.Generation.Terrains
{
    // Persistence seam for the terrain library (specs/TERRAIN_GENERATORS.md section 6.3). Core is the only process that
    // uses it. All methods may throw when the database is unreachable; callers decide how to report that.
    public interface ILibraryCollection<T> where T : class
    {
        // Ordered by id (ordinal).
        IReadOnlyList<T> List();
        T Get(string id);
        // False when an entity with this id already exists (nothing is written).
        bool Insert(T entity, string updatedBy);
        // False when no entity with this id exists (nothing is written).
        bool Replace(T entity, string updatedBy);
        // False when no entity with this id exists.
        bool Delete(string id);
    }

    public interface ILibraryStore
    {
        ILibraryCollection<DescriptionFragment> Fragments { get; }
        ILibraryCollection<FeatureTemplate> Features { get; }
        ILibraryCollection<NeighborPhrase> Phrases { get; }
        // Terrain types (collection terrain_type): the owners of the three entity kinds above. Same persistence rules.
        ILibraryCollection<TerrainType> TerrainTypes { get; }

        // The library version counter (terrain_meta "library"); null when the meta document does not exist (fresh install).
        int? GetVersion();
        // Creates the meta document with this version. No-op when it exists.
        void InitMeta(int version);
        // Increments the counter and returns the new value. Throws when there is no meta document.
        int BumpVersion();
    }

    // Document mapping, shared by the Mongo store and by the audit rows of the admin API (field names = the documents' field names).
    public static class LibraryDocuments
    {
        public const string FragmentsCollection = "terrain_fragments";
        public const string FeaturesCollection = "terrain_features";
        public const string PhrasesCollection = "terrain_neighbor_phrases";
        public const string MetaCollection = "terrain_meta";
        public const string MetaId = "library";

        public static BsonDocument ToDocument(DescriptionFragment f)
        {
            return Legacy(new BsonDocument
            {
                { "_id", f.Id }, { "terrain", f.Terrain ?? string.Empty }, { "slot", f.Slot ?? string.Empty }, { "text", f.Text ?? string.Empty },
                { "weight", f.Weight }, { "tags", Array(f.Tags) },
            }, f);
        }

        public static BsonDocument ToDocument(FeatureTemplate f)
        {
            return Legacy(new BsonDocument
            {
                { "_id", f.Id }, { "terrain", f.Terrain ?? string.Empty }, { "short", f.Short ?? string.Empty }, { "long", f.Long ?? string.Empty },
                { "handles", Array(f.Handles) }, { "adjectives", Array(f.Adjectives) },
                { "roomLine", f.RoomLine ?? string.Empty },
                { "weight", f.Weight }, { "chance", f.Chance }, { "version", f.Version }, { "tags", Array(f.Tags) },
            }, f);
        }

        public static BsonDocument ToDocument(NeighborPhrase p)
        {
            return Legacy(new BsonDocument
            {
                { "_id", p.Id }, { "terrain", p.Terrain ?? string.Empty }, { "neighborTerrain", p.NeighborTerrain ?? string.Empty },
                { "text", p.Text ?? string.Empty }, { "weight", p.Weight }, { "merge", p.Merge },
            }, p);
        }

        // The retired `terrains` array is written back only when the document had one (migrated documents keep it; it is ignored from now on).
        private static BsonDocument Legacy(BsonDocument d, ITerrainScoped entity)
        {
            if (entity.LegacyTerrains != null && entity.LegacyTerrains.Count > 0) { d["terrains"] = Array(entity.LegacyTerrains); }
            return d;
        }

        public static DescriptionFragment FragmentFrom(BsonDocument d)
        {
            return new DescriptionFragment
            {
                Id = Text(d, "_id"), Terrain = Empty(Text(d, "terrain")), LegacyTerrains = List(d, "terrains"), Slot = Text(d, "slot"), Text = Text(d, "text"),
                Weight = Int(d, "weight", 1), Tags = List(d, "tags"),
            };
        }

        public static FeatureTemplate FeatureFrom(BsonDocument d)
        {
            return new FeatureTemplate
            {
                Id = Text(d, "_id"), Terrain = Empty(Text(d, "terrain")), LegacyTerrains = List(d, "terrains"), Short = Text(d, "short"), Long = Text(d, "long"),
                Handles = List(d, "handles"), Adjectives = List(d, "adjectives"),
                RoomLine = Empty(Text(d, "roomLine")),
                Weight = Int(d, "weight", 1), Chance = d.TryGetValue("chance", out var c) && c.IsNumeric ? c.ToDouble() : 1.0,
                Version = Int(d, "version", 1), Tags = List(d, "tags"),
            };
        }

        public static NeighborPhrase PhraseFrom(BsonDocument d)
        {
            return new NeighborPhrase
            {
                Id = Text(d, "_id"), Terrain = Empty(Text(d, "terrain")), LegacyTerrains = List(d, "terrains"), NeighborTerrain = Text(d, "neighborTerrain"), Text = Text(d, "text"),
                Weight = Int(d, "weight", 1), Merge = !d.TryGetValue("merge", out var m) || !m.IsBoolean || m.AsBoolean,
            };
        }

        private static BsonArray Array(IEnumerable<string> items) { return new BsonArray((items ?? new string[0]).Select(i => (BsonValue)i)); }
        private static string Text(BsonDocument d, string name) { return d.TryGetValue(name, out var v) && v.IsString ? v.AsString : null; }
        private static string Empty(string value) { return string.IsNullOrEmpty(value) ? null : value; }
        private static int Int(BsonDocument d, string name, int fallback) { return d.TryGetValue(name, out var v) && v.IsNumeric ? v.ToInt32() : fallback; }
        private static List<string> List(BsonDocument d, string name)
        {
            var result = new List<string>();
            if (d.TryGetValue(name, out var v) && v.IsBsonArray) { foreach (var i in v.AsBsonArray) { if (i.IsString) { result.Add(i.AsString); } } }
            return result;
        }
    }

    /// <summary>The library in MongoDB: collections terrain_fragments, terrain_features, terrain_neighbor_phrases, terrain_meta. Not run against a database in the authoring sandbox.</summary>
    public sealed class MongoLibraryStore : ILibraryStore
    {
        private sealed class Collection<T> : ILibraryCollection<T> where T : class
        {
            private readonly IMongoCollection<BsonDocument> _collection;
            private readonly Func<T, BsonDocument> _toDocument;
            private readonly Func<BsonDocument, T> _fromDocument;

            public Collection(IMongoCollection<BsonDocument> collection, Func<T, BsonDocument> toDocument, Func<BsonDocument, T> fromDocument)
            {
                _collection = collection; _toDocument = toDocument; _fromDocument = fromDocument;
            }

            public IReadOnlyList<T> List()
            {
                return _collection.Find(new BsonDocument()).Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToList()
                    .Select(_fromDocument).ToList();
            }

            public T Get(string id)
            {
                var d = _collection.Find(new BsonDocument("_id", id)).FirstOrDefault();
                return d == null ? null : _fromDocument(d);
            }

            public bool Insert(T entity, string updatedBy)
            {
                try { _collection.InsertOne(Stamp(_toDocument(entity), updatedBy)); return true; }
                catch (MongoWriteException ex) when (ex.WriteError != null && ex.WriteError.Category == ServerErrorCategory.DuplicateKey) { return false; }
            }

            public bool Replace(T entity, string updatedBy)
            {
                var d = Stamp(_toDocument(entity), updatedBy);
                return _collection.ReplaceOne(new BsonDocument("_id", d["_id"]), d).MatchedCount > 0;
            }

            public bool Delete(string id)
            {
                return _collection.DeleteOne(new BsonDocument("_id", id)).DeletedCount > 0;
            }

            private static BsonDocument Stamp(BsonDocument d, string updatedBy)
            {
                d["updatedAt"] = DateTime.UtcNow;
                d["updatedBy"] = updatedBy ?? string.Empty;
                return d;
            }
        }

        private readonly IMongoCollection<BsonDocument> _meta;

        public MongoLibraryStore(IMongoDatabase database)
        {
            var fragments = database.GetCollection<BsonDocument>(LibraryDocuments.FragmentsCollection);
            var features = database.GetCollection<BsonDocument>(LibraryDocuments.FeaturesCollection);
            var phrases = database.GetCollection<BsonDocument>(LibraryDocuments.PhrasesCollection);
            _meta = database.GetCollection<BsonDocument>(LibraryDocuments.MetaCollection);
            Fragments = new Collection<DescriptionFragment>(fragments, LibraryDocuments.ToDocument, LibraryDocuments.FragmentFrom);
            Features = new Collection<FeatureTemplate>(features, LibraryDocuments.ToDocument, LibraryDocuments.FeatureFrom);
            Phrases = new Collection<NeighborPhrase>(phrases, LibraryDocuments.ToDocument, LibraryDocuments.PhraseFrom);
            TerrainTypes = new Collection<TerrainType>(database.GetCollection<BsonDocument>(TerrainTypeDocuments.Collection), TerrainTypeDocuments.ToDocument, TerrainTypeDocuments.From);
            // Index on terrain for each entity collection (section 6.3). Index creation is idempotent.
            var keys = Builders<BsonDocument>.IndexKeys.Ascending("terrain");
            foreach (var c in new[] { fragments, features, phrases }) { c.Indexes.CreateOne(new CreateIndexModel<BsonDocument>(keys)); }
        }

        public ILibraryCollection<DescriptionFragment> Fragments { get; private set; }
        public ILibraryCollection<FeatureTemplate> Features { get; private set; }
        public ILibraryCollection<NeighborPhrase> Phrases { get; private set; }
        public ILibraryCollection<TerrainType> TerrainTypes { get; private set; }

        public int? GetVersion()
        {
            var d = _meta.Find(new BsonDocument("_id", LibraryDocuments.MetaId)).FirstOrDefault();
            return d == null ? (int?)null : d["version"].ToInt32();
        }

        public void InitMeta(int version)
        {
            try { _meta.InsertOne(new BsonDocument { { "_id", LibraryDocuments.MetaId }, { "version", version }, { "seededAt", DateTime.UtcNow } }); }
            catch (MongoWriteException ex) when (ex.WriteError != null && ex.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
        }

        public int BumpVersion()
        {
            var d = _meta.FindOneAndUpdate(new BsonDocument("_id", LibraryDocuments.MetaId),
                new BsonDocument("$inc", new BsonDocument("version", 1)),
                new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After });
            if (d == null) { throw new InvalidOperationException("The library has no meta document."); }
            return d["version"].ToInt32();
        }
    }

    // ILibrary over a store. Generators call ILibrary many times per run, so it reads one immutable snapshot (section 6.4:
    // "a single snapshot read at the start of a run") and keeps it until Refresh() is called (the admin API does after every write).
    public sealed class StoreLibrary : ILibrary
    {
        private readonly ILibraryStore _store;
        private readonly object _gate = new object();
        private InMemoryLibrary _snapshot;

        public StoreLibrary(ILibraryStore store)
        {
            _store = store ?? throw new ArgumentNullException("store");
        }

        // Reads the store now and replaces the cached snapshot.
        public ILibrary Refresh()
        {
            var fresh = new InMemoryLibrary { Version = _store.GetVersion() ?? 0 };
            // A legacy document without a terrain (not migrated) is never returned: the InMemoryLibrary queries match on Terrain.
            fresh.FragmentList.AddRange(_store.Fragments.List());
            fresh.FeatureList.AddRange(_store.Features.List());
            fresh.PhraseList.AddRange(_store.Phrases.List());
            lock (_gate) { _snapshot = fresh; }
            return fresh;
        }

        private InMemoryLibrary Current()
        {
            lock (_gate) { if (_snapshot != null) { return _snapshot; } }
            Refresh();
            lock (_gate) { return _snapshot; }
        }

        public int Version { get { return Current().Version; } }
        public IReadOnlyList<DescriptionFragment> Fragments(string terrain, string slot) { return Current().Fragments(terrain, slot); }
        public IReadOnlyList<FeatureTemplate> Features(string terrain) { return Current().Features(terrain); }
        public IReadOnlyList<NeighborPhrase> Phrases(string roomTerrain, string neighborTerrain) { return Current().Phrases(roomTerrain, neighborTerrain); }
        public FeatureTemplate FeatureById(string id) { return Current().FeatureById(id); }
    }
}
