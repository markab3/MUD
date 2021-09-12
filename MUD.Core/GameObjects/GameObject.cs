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

        public List<string> Plurals { get; set; }

        public List<string> Adjectives { get; set; } = new List<string>();

        public List<string> Handles { get; set; } = new List<string>();

        public List<ExtendedProperty> ExtendedProperties { get; set; } = new List<ExtendedProperty>();

        public GameObject() { }

        public virtual string Examine()
        {
            return LongDescription;
        }

        public virtual bool IsPluralMatchForString(string input)
        {
            var results = new List<string>();
            foreach (string currentPlural in Plurals)
            {
                results.Add(currentPlural.ToLower());
                foreach (string currentAdjective in Adjectives)
                {
                    results.Add(string.Format("{0} {1}", currentAdjective.ToLower(), currentPlural.ToLower()));
                }
            }

            return results.Contains(input.ToLower());
        }

        public virtual bool IsMatchForString(string input)
        {
            var results = new List<string>();

            if (!string.IsNullOrWhiteSpace(ShortDescription))
            {
                results.Add(ShortDescription.ToLower());
            }

            foreach (string currentHandle in Handles)
            {
                results.Add(currentHandle.ToLower());
                foreach (string currentAdjective in Adjectives)
                {
                    results.Add(string.Format("{0} {1}", currentAdjective.ToLower(), currentHandle.ToLower()));
                }
            }

            return results.Contains(input.ToLower());
        }
    }
}