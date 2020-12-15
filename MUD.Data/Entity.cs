using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MongoDB.Driver;

namespace MUD.Data
{
    public abstract class Entity
    {
        public string Id { get; set; }
        protected IMongoDatabase _db;

        protected DateTime _lastSave = DateTime.Now;

        protected string _dbCollection = null;

        public Entity (IMongoDatabase db) {
            _db = db;
        }

        public virtual bool Update()
        {
            if (string.IsNullOrWhiteSpace(_dbCollection))
            {
                throw new ArgumentNullException("No DB collection provided.");
            }

            var collection = _db.GetCollection<Entity>(_dbCollection);
            if (string.IsNullOrWhiteSpace(Id))
            {
                collection.InsertOne(this);
                _lastSave = DateTime.Now;
                return true;
            }
            else
            {
                var result = collection.ReplaceOne<Entity>((m => m.Id == this.Id), this);
                _lastSave = DateTime.Now;
                return result != null && result.IsModifiedCountAvailable && result.ModifiedCount == 1;
            }
        }

        // public void LoadEntity(Entity entity)
        // {
        //     entity.Map(this);
        // }

        // public void Map(Entity target)
        // {
        //     // list of writable properties of the destination
        //     List<PropertyInfo> tprops = this.GetType().GetTypeInfo().GetProperties().Where(x => x.CanWrite == true).ToList();

        //     foreach (var prop in tprops)
        //     {
        //         // check whether source object has the the property
        //         var sp = this.GetType().GetProperty(prop.Name);
        //         if (sp != null)
        //         {
        //             // if yes, copy the value to the matching property
        //             var value = sp.GetValue(this, null);
        //             target.GetType().GetProperty(prop.Name).SetValue(target, value, null);
        //         }
        //     };
        // }
    }
}