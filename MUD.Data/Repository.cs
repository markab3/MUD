using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using MongoDB.Driver;

namespace MUD.Data
{
    // TODO: MAKE ME THREADSAFE!
    public abstract class Repository<TModel> : IRepository<TModel> where TModel : Entity
    {
        private IMongoCollection<TModel> _collection;

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

            _collection.InsertOne(model);

            if (!_modelCache.ContainsKey(model.Id))
            {
                _modelCache.Add(model.Id, model);
            }
        }

        public bool Update(TModel model)
        {
            if (string.IsNullOrWhiteSpace(model.Id)) { return false; }

            // Entities always carry an Id (generated or authored key), so a first save arrives here too: upsert.
            var result = _collection.ReplaceOne<TModel>((m => m.Id == model.Id), model, new ReplaceOptions { IsUpsert = true });
            // Not ModifiedCount: saving an unchanged document modifies nothing but still succeeds.
            var success = (result != null && result.IsAcknowledged && (result.MatchedCount == 1 || result.UpsertedId != null));

            if (success && !_modelCache.ContainsKey(model.Id))
            {
                _modelCache.Add(model.Id, model);
            }

            return success;
        }

        public bool Delete(TModel model)
        {
            var result = _collection.DeleteOne<TModel>((m => m.Id == model.Id));
            var success = (result != null && result.DeletedCount == 1);

            if (success && _modelCache.ContainsKey(model.Id))
            {
                _modelCache.Remove(model.Id);
            }

            return success;
        }

        public TModel Get(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) { return null; }

            if (_modelCache.ContainsKey(id))
            {
                return _modelCache[id];
            }

            var foundModel = _collection.Find<TModel>((m => m.Id == id)).FirstOrDefault();
            if (foundModel != null && !_modelCache.ContainsKey(foundModel.Id))
            {
                _modelCache.Add(foundModel.Id, foundModel);
            }

            return foundModel;
        }

        public IEnumerable<TModel> GetAll()
        {
            var results = _collection.AsQueryable().ToEnumerable<TModel>();

            if (results != null)
            {
                foreach (TModel currentModel in results)
                {
                    if (!_modelCache.ContainsKey(currentModel.Id))
                    {
                        _modelCache.Add(currentModel.Id, currentModel);
                    }
                }
            }

            return results;
        }

        private IMongoCollection<MongoDB.Bson.BsonDocument> RawCollection
        {
            get { return _collection.Database.GetCollection<MongoDB.Bson.BsonDocument>(_collection.CollectionNamespace.CollectionName); }
        }

        public MongoDB.Bson.BsonDocument GetRawDocument(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) { return null; }
            return RawCollection.Find(new MongoDB.Bson.BsonDocument("_id", id)).FirstOrDefault();
        }

        public List<string> GetIdsByField(string fieldName, string value)
        {
            return RawCollection.Find(new MongoDB.Bson.BsonDocument(fieldName, value))
                .Project(new MongoDB.Bson.BsonDocument("_id", 1))
                .ToList()
                .Select(d => d["_id"].AsString)
                .ToList();
        }

        public void UpsertFields(string id, MongoDB.Bson.BsonDocument set, MongoDB.Bson.BsonDocument setOnInsert)
        {
            if (string.IsNullOrWhiteSpace(id)) { throw new ArgumentException("An id is required.", nameof(id)); }

            var update = new MongoDB.Bson.BsonDocument();
            if (set != null && set.ElementCount > 0) { update["$set"] = set; }
            if (setOnInsert != null && setOnInsert.ElementCount > 0) { update["$setOnInsert"] = setOnInsert; }
            if (update.ElementCount == 0) { return; }

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

        // Called with the live (cached) instance and a freshly loaded copy. Default: evict.
        protected virtual void RefreshCached(TModel cached, TModel fresh)
        {
            _modelCache.Remove(cached.Id);
        }

        public IEnumerable<TModel> Search(Expression<Func<TModel, bool>> matchExpression)
        {
            var results = _collection.Find<TModel>(matchExpression).ToEnumerable<TModel>();

            if (results != null)
            {
                foreach (TModel currentModel in results)
                {
                    if (!_modelCache.ContainsKey(currentModel.Id))
                    {
                        _modelCache.Add(currentModel.Id, currentModel);
                    }
                }
            }

            return results;
        }
    }
}