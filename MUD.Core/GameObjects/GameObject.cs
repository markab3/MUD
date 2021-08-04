using System;
using MongoDB.Driver;
using MUD.Data;

namespace MUD.Core.GameObjects
{
    public abstract class GameObject: Entity
    {

        public string ShortDescription { get; set; }

        public string LongDescription { get; set; }

        public GameObject() {}

        public virtual string Examine()
        {
            return LongDescription;
        }
    }
}