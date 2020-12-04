
using System;
using MongoDB.Driver;

namespace MUD.Main
{
    public class Item
    {
        protected IMongoDatabase _db;

        protected DateTime _lastSave = DateTime.Now;

        public string Id { get; set; }

        public string ShortDescription { get; set; }

        public string LongDescription { get; set; }

        public Item(IMongoClient dbClient)
        {
            _db = dbClient.GetDatabase("testmud");
        }

        public virtual string Examine()
        {
            return LongDescription;
        }

        public virtual void Update()
        {
            var collection = _db.GetCollection<Item>("testingstuff");
            if (string.IsNullOrWhiteSpace(Id))
            {
                collection.InsertOne(this);
            }
            else
            {
                var result = collection.ReplaceOne<Item>((m => m.Id == this.Id), this);

            }
        }
    }
}