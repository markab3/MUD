using System;
using System.Collections.Generic;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MUD.Core.Components;
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

        // Module-owned components, stored as components.<module>.<name>. Core does not interpret the data: it is
        // kept as BSON and written back exactly as it was read, including components of modules that are not
        // loaded. Core components are typed fields, not entries here. Use the methods below; they validate names.
        [BsonElement("components")]
        public Dictionary<string, Dictionary<string, BsonDocument>> Components
        {
            // Never null, including for a stored document that has no components element.
            get { return _components ?? (_components = new Dictionary<string, Dictionary<string, BsonDocument>>()); }
            set { _components = value; }
        }

        private Dictionary<string, Dictionary<string, BsonDocument>> _components;

        public GameObject() { }

        public bool TryGetComponent(string fullName, out BsonDocument data)
        {
            data = null;
            if (!ComponentName.TryParseModuleName(fullName, out string moduleName, out string componentName)) { return false; }
            if (Components == null || !Components.TryGetValue(moduleName, out var named) || named == null) { return false; }
            return named.TryGetValue(componentName, out data);
        }

        public bool HasComponent(string fullName)
        {
            return TryGetComponent(fullName, out _);
        }

        // Stores a copy of the data, so later changes to the caller's document do not alter the entity.
        public void SetComponent(string fullName, BsonDocument data)
        {
            if (data == null) { throw new ArgumentNullException(nameof(data)); }
            if (!ComponentName.TryParseModuleName(fullName, out string moduleName, out string componentName))
            {
                throw new ArgumentException("A module component name is '<module>.<name>': '" + fullName + "'", nameof(fullName));
            }
            if (Components == null) { Components = new Dictionary<string, Dictionary<string, BsonDocument>>(); }
            if (!Components.TryGetValue(moduleName, out var named) || named == null)
            {
                named = new Dictionary<string, BsonDocument>();
                Components[moduleName] = named;
            }
            named[componentName] = data.DeepClone().AsBsonDocument;
        }

        // Returns true when a component was removed. An emptied module entry is dropped so no empty documents are stored.
        public bool RemoveComponent(string fullName)
        {
            if (!ComponentName.TryParseModuleName(fullName, out string moduleName, out string componentName)) { return false; }
            if (Components == null || !Components.TryGetValue(moduleName, out var named) || named == null) { return false; }
            bool removed = named.Remove(componentName);
            if (named.Count == 0) { Components.Remove(moduleName); }
            return removed;
        }

        public virtual string Examine()
        {
            return LongDescription;
        }

        // Examination by a particular Living. Defaults to Examine() so existing overrides keep working.
        public virtual string Examine(Living examiner)
        {
            return Examine();
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