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

        private List<TModel> _modelCache; // This is helpful for anything other than searches.. womp womp.

        public Repository(IMongoClient dbClient, string databaseName, string collectionName)
        {
            var database = dbClient.GetDatabase(databaseName);
            _collection = database.GetCollection<TModel>(collectionName);
            _modelCache = new List<TModel>();
        }

        public void Insert(TModel model)
        {
            var matchedCacheItem = _modelCache.FirstOrDefault(m => m._id == model._id);
            if (matchedCacheItem == null)
            {
                _modelCache.Add(model);
            }
            _collection.InsertOne(model);
        }

        public bool Update(TModel model)
        {
            var matchedCacheItem = _modelCache.FirstOrDefault(m => m._id == model._id);
            if (matchedCacheItem == null)
            {
                _modelCache.Add(model);
            }
            else
            {
                _modelCache.Remove(matchedCacheItem);
                _modelCache.Add(model);
            }

            var result = _collection.ReplaceOne<TModel>((m => m._id == model._id), model);
            return (result != null && result.IsModifiedCountAvailable && result.ModifiedCount == 1);
        }

        public bool Delete(TModel model)
        {
            var matchedCacheItem = _modelCache.FirstOrDefault(m => m._id == model._id);
            if (matchedCacheItem != null)
            {
                _modelCache.Add(model);
            }
            var result = _collection.DeleteOne<TModel>((m => m._id == model._id));
            return (result != null && result.DeletedCount == 1);
        }

        public TModel Get(string id)
        {
            var matchedCacheItem = _modelCache.FirstOrDefault(m => m._id == id);
            if (matchedCacheItem != null)
            {
                return matchedCacheItem;
            }
            return _collection.Find<TModel>((m => m._id == id)).FirstOrDefault();
        }

        public IEnumerable<TModel> GetAll()
        {
            var results = _collection.AsQueryable().ToEnumerable<TModel>();
            if (results != null)
            {
                _modelCache.AddRange(results);
            }
            return _collection.AsQueryable().ToEnumerable<TModel>();
        }

        public IEnumerable<TModel> Search(Expression<Func<TModel, bool>> matchExpression)
        {
            var results = _collection.Find<TModel>(matchExpression).ToEnumerable();
            if (results != null)
            {
                _modelCache.AddRange(results);
            }
            return results;
        }
    }
}