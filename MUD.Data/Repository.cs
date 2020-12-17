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
            _collection.InsertOne(model);

            if (!_modelCache.ContainsKey(model.Id))
            {
                _modelCache.Add(model.Id, model);
            }
        }

        public bool Update(TModel model)
        {
            var result = _collection.ReplaceOne<TModel>((m => m.Id == model.Id), model);
            var success = (result != null && result.IsModifiedCountAvailable && result.ModifiedCount == 1);

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