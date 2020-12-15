using System;
using MongoDB.Driver;
using MUD.Data;

namespace MUD.Core
{
    public abstract class GameObject: Entity
    {

        public string ShortDescription { get; set; }

        public string LongDescription { get; set; }

        public GameObject(IMongoDatabase db) : base(db)       {        }

        public virtual string Examine()
        {
            return LongDescription;
        }
    }
}