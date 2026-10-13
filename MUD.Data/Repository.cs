using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using MongoDB.Driver;

namespace MUD.Data
{
    // Thread safety: every access to the model cache and every collection call happens under _sync, so the
    // cache cannot be corrupted and an id always maps to exactly one live instance. The lock is held across
    // the Mongo round trip on purpose (simple, keeps cache and DB in step). The live model objects themselves
    // are NOT locked: game logic is expected to mutate them from the command-queue thread.
    //
    // Persistence is write-through: callers persist a mutation immediately (Update / UpdateFields). There is no
    // deferred queue, so there is nothing to flush on shutdown. See specs/RUNTIME_STATE.md.
    public abstract class Repository<TModel> : IRepository<TModel> where TModel : Entity
    {
        private readonly object _sync = new object();

        private IMongoCollection<TModel> _collection;

        // Never evicted (except on Delete): the cached instance IS the live game object (rooms hold occupants).
        private Dictionary<string, TModel> _modelCache; // This is helpful for anything other than searches.. womp womp.

        public Repository(IMongoClient dbClient, string databaseName, string collectionName)
        {
            var database = dbClient.GetDatabase(databaseName);
            _collection = database.GetCollection<TModel>(collectionName);
            _modelCache = new Dictionary<string, TModel>();
        }

        public void Insert(TModel model)
        {
            if (string.IsNullOrWhiteSpace(model.Id))
            {
                throw new ArgumentException("Entity must have an Id before it can be inserted.", nameof(model));
            }

            lock (_sync)
            {
                try
                {
                    _collection.InsertOne(model);
                }
                catch (Exception ex) when (DuplicateKeyException.IsDuplicateKey(ex))
                {
                    throw new DuplicateKeyException(ex);
                }

                if (!_modelCache.ContainsKey(model.Id))
                {
                    _modelCache.Add(model.Id, model);
                }
            }
        }

        public bool Update(TModel model)
        {
            if (string.IsNullOrWhiteSpace(model.Id)) { return false; }

            lock (_sync)
            {
                // Entities always carry an Id (generated or authored key), so a first save arrives here too: upsert.
                ReplaceOneResult result;
                try
                {
                    result = _collection.ReplaceOne<TModel>((m => m.Id == model.Id), model, new ReplaceOptions { IsUpsert = true });
                }
                catch (Exception ex) when (DuplicateKeyException.IsDuplicateKey(ex))
                {
                    throw new DuplicateKeyException(ex);
                }
                // Not ModifiedCount: saving an unchanged document modifies nothing but still succeeds.
                var success = (result != null && result.IsAcknowledged && (result.MatchedCount == 1 || result.UpsertedId != null));

                if (success && !_modelCache.ContainsKey(model.Id))
                {
                    _modelCache.Add(model.Id, model);
                }

                return success;
            }
        }

        // Write-through of runtime state: $set only the given top-level fields of an existing document.
        // Unlike Update this never replaces the document, so fields written by the importer are never
        // overwritten with a stale in-memory copy. Returns false when the document does not exist.
        // The cache is not touched: the live object is the source of the values.
        // Idempotent: creating an index that already exists with the same definition is a no-op.
        // Documents where the field is missing, null or not a string are left out of the index (partial index),
        // so unkeyed legacy records never collide with each other. Throws if the server refuses the index.
        protected void CreateUniqueStringIndex(string fieldName, string indexName)
        {
            var options = new CreateIndexOptions<TModel>
            {
                Unique = true,
                Name = indexName,
                PartialFilterExpression = new MongoDB.Bson.BsonDocument(fieldName, new MongoDB.Bson.BsonDocument("$type", "string"))
            };
            lock (_sync)
            {
                _collection.Indexes.CreateOne(new CreateIndexModel<TModel>(new MongoDB.Bson.BsonDocument(fieldName, 1), options));
            }
        }

        public bool UpdateFields(string id, MongoDB.Bson.BsonDocument set)
        {
            if (string.IsNullOrWhiteSpace(id) || set == null || set.ElementCount == 0) { return false; }

            lock (_sync)
            {
                var update = new MongoDB.Bson.BsonDocument("$set", set);
                var result = RawCollection.UpdateOne(new MongoDB.Bson.BsonDocument("_id", id), new BsonDocumentUpdateDefinition<MongoDB.Bson.BsonDocument>(update));
                return result != null && result.IsAcknowledged && result.MatchedCount == 1;
            }
        }

        public bool Delete(TModel model)
        {
            lock (_sync)
            {
                var result = _collection.DeleteOne<TModel>((m => m.Id == model.Id));
                var success = (result != null && result.DeletedCount == 1);

                if (success && _modelCache.ContainsKey(model.Id))
                {
                    _modelCache.Remove(model.Id);
                }

                return success;
            }
        }

        public TModel Get(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) { return null; }

            lock (_sync)
            {
                TModel cached;
                if (_modelCache.TryGetValue(id, out cached))
                {
                    return cached;
                }

                var foundModel = _collection.Find<TModel>((m => m.Id == id)).FirstOrDefault();
                if (foundModel != null)
                {
                    _modelCache[foundModel.Id] = foundModel;
                }

                return foundModel;
            }
        }

        public IEnumerable<TModel> GetAll()
        {
            lock (_sync)
            {
                return Resolve(_collection.Find<TModel>(FilterDefinition<TModel>.Empty).ToList());
            }
        }

        public IEnumerable<TModel> Search(Expression<Func<TModel, bool>> matchExpression)
        {
            lock (_sync)
            {
                return Resolve(_collection.Find<TModel>(matchExpression).ToList());
            }
        }

        // Caller holds _sync. Query results are materialised inside the lock and mapped onto the cached live
        // instances, so a query never hands out a second copy of an object that is already live (whose
        // in-game changes would then be lost) and never enumerates a cursor outside the lock.
        private List<TModel> Resolve(List<TModel> found)
        {
            var resolved = new List<TModel>(found.Count);
            foreach (TModel currentModel in found)
            {
                TModel live;
                if (!_modelCache.TryGetValue(currentModel.Id, out live))
                {
                    live = currentModel;
                    _modelCache.Add(live.Id, live);
                }
                resolved.Add(live);
            }
            return resolved;
        }

        private IMongoCollection<MongoDB.Bson.BsonDocument> RawCollection
        {
            get { return _collection.Database.GetCollection<MongoDB.Bson.BsonDocument>(_collection.CollectionNamespace.CollectionName); }
        }

        public MongoDB.Bson.BsonDocument GetRawDocument(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) { return null; }
            lock (_sync)
            {
                return RawCollection.Find(new MongoDB.Bson.BsonDocument("_id", id)).FirstOrDefault();
            }
        }

        public List<string> GetIdsByField(string fieldName, string value)
        {
            lock (_sync)
            {
                return RawCollection.Find(new MongoDB.Bson.BsonDocument(fieldName, value))
                    .Project(new MongoDB.Bson.BsonDocument("_id", 1))
                    .ToList()
                    .Select(d => d["_id"].AsString)
                    .ToList();
            }
        }

        public void UpsertFields(string id, MongoDB.Bson.BsonDocument set, MongoDB.Bson.BsonDocument setOnInsert)
        {
            if (string.IsNullOrWhiteSpace(id)) { throw new ArgumentException("An id is required.", nameof(id)); }

            var update = new MongoDB.Bson.BsonDocument();
            if (set != null && set.ElementCount > 0) { update["$set"] = set; }
            if (setOnInsert != null && setOnInsert.ElementCount > 0) { update["$setOnInsert"] = setOnInsert; }
            if (update.ElementCount == 0) { return; }

            lock (_sync)
            {
                RawCollection.UpdateOne(new MongoDB.Bson.BsonDocument("_id", id), new BsonDocumentUpdateDefinition<MongoDB.Bson.BsonDocument>(update), new UpdateOptions { IsUpsert = true });

                // Never leave a stale live object behind: a later whole-document save of it would undo this write.
                TModel cached;
                if (_modelCache.TryGetValue(id, out cached))
                {
                    var fresh = _collection.Find<TModel>((m => m.Id == id)).FirstOrDefault();
                    if (fresh == null) { _modelCache.Remove(id); }
                    else { RefreshCached(cached, fresh); }
                }
            }
        }

        // Called with the live (cached) instance and a freshly loaded copy, under _sync. Default: evict.
        protected virtual void RefreshCached(TModel cached, TModel fresh)
        {
            _modelCache.Remove(cached.Id);
        }
    }
}
