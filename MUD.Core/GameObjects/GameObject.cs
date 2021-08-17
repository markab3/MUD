using System;
using System.Collections.Generic;
using MUD.Core.Properties;
using MUD.Data;

namespace MUD.Core.GameObjects
{
    public abstract class GameObject : Entity
    {

        public string ShortDescription { get; set; }

        public string LongDescription { get; set; }

        public List<ExtendedProperty> ExtendedProperties { get; set; } = new List<ExtendedProperty>();

        public GameObject() { }

        public virtual string Examine()
        {
            return LongDescription;
        }
    }
}