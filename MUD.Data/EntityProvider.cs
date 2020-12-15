using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using MongoDB.Driver;

namespace MUD.Data
{
    // TODO: MAKE ME THREADSAFE!
    public abstract class EntityProvider<TModel> : IEntityProvider<TModel> where TModel : Entity
    {
        private IMongoCollection<TModel> _collection;

        private List<TModel> _modelCache; // This is helpful for anything other than searches.. womp womp.

        public EntityProvider(IMongoClient dbClient, string databaseName, string collectionName)
        {
            var database = dbClient.GetDatabase(databaseName);
            _collection = database.GetCollection<TModel>(collectionName);
            _modelCache = new List<TModel>();
        }
        
        public TModel Get(string id)
        {
            var matchedCacheItem = _modelCache.FirstOrDefault(m => m.Id == id);
            if (matchedCacheItem != null)
            {
                return matchedCacheItem;
            }
            return _collection.Find<TModel>((m => m.Id == id)).FirstOrDefault();
        }

        public IEnumerable<TModel> GetAll()
        {
            var results = _collection.AsQueryable().ToEnumerable<TModel>();
            if (results != null)
            {
                _modelCache.AddRange(results);
            }
            return results;
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