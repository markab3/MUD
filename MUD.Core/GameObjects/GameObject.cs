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

        public List<string> Adjectives { get; set; } = new List<string>();

        public List<string> Handles { get; set; } = new List<string>();

        public List<ExtendedProperty> ExtendedProperties { get; set; } = new List<ExtendedProperty>();

        public GameObject() { }

        public virtual string Examine()
        {
            return LongDescription;
        }
        public virtual bool IsMatchForString(string input)
        {
            var results = new List<string>();

            results.Add(ShortDescription.ToLower());

            foreach (string currentHandle in Handles)
            {
                foreach (string currentAdjective in Adjectives)
                {
                    results.Add(string.Format("{0} {1}", currentAdjective.ToLower(), currentHandle.ToLower()));
                }
            }

            return results.Contains(input.ToLower());
        }
    }
}