using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;

namespace MUD.Data
{
    public abstract class Entity
    {
        [BsonId(IdGenerator = typeof(StringObjectIdGenerator))]
        [BsonRepresentation(BsonType.ObjectId)]
        public string _id { get; set; }

        protected DateTime _lastSave = DateTime.Now;

        public void Map(Entity target)
        {
            // list of writable properties of the destination
            List<PropertyInfo> tprops = this.GetType().GetTypeInfo().GetProperties().Where(x => x.CanWrite == true).ToList();

            foreach (var prop in tprops)
            {
                // check whether source object has the the property
                var sp = this.GetType().GetProperty(prop.Name);
                if (sp != null)
                {
                    // if yes, copy the value to the matching property
                    var value = sp.GetValue(this, null);
                    target.GetType().GetProperty(prop.Name).SetValue(target, value, null);
                }
            };
        }
    }
}