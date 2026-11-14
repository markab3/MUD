using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MUD.Core.Components
{
    // One entry of an entity's behavior list (owner decisions D5, D53). The entity stores only this reference; the
    // code that runs is resolved from (Module, Type) at call time, in a later phase. Stored as an element of the
    // embedded array "behaviors": { hook, module, type, priority, config, schemaVersion }.
    [BsonIgnoreExtraElements]
    public class BehaviorRef
    {
        // Largest accepted size of Config once serialized to BSON, in bytes. Owner decision D53 leaves the number to
        // the card; it keeps an entity document small. Flagged in the pull request.
        public const int MaxConfigBytes = 4096;

        // One of BehaviorHooks.All.
        [BsonElement("hook")]
        public string Hook { get; set; }

        // The module that owns the behavior code (for example "desert"). Any name is accepted (D55).
        [BsonElement("module")]
        public string Module { get; set; }

        // The behavior type within the module (for example "DesertTempleTrap").
        [BsonElement("type")]
        public string Type { get; set; }

        // Stored and copied; handler order is not significant (D7), so core gives it no meaning yet.
        [BsonElement("priority")]
        public int Priority { get; set; }

        // Behavior settings as a database document (D53). Never null.
        [BsonElement("config")]
        public BsonDocument Config
        {
            get { return _config ?? (_config = new BsonDocument()); }
            set { _config = value; }
        }

        private BsonDocument _config;

        // Stored, copied and editable, but core does not compare it (D51).
        [BsonElement("schemaVersion")]
        public int SchemaVersion { get; set; } = 1;

        public BehaviorRef() { }

        public BehaviorRef(string hook, string module, string type, int priority = 0, BsonDocument config = null, int schemaVersion = 1)
        {
            Hook = hook;
            Module = module;
            Type = type;
            Priority = priority;
            Config = config == null ? new BsonDocument() : config.DeepClone().AsBsonDocument;
            SchemaVersion = schemaVersion;
        }

        // An independent copy: changing the copy's Config does not change this entry.
        public BehaviorRef Clone()
        {
            return new BehaviorRef(Hook, Module, Type, Priority, Config, SchemaVersion);
        }

        // Two entries are the same binding when hook, module and type are equal (ordinal). Priority, config and
        // schema version are settings of that binding (the collision rule of owner decision D39).
        public bool HasSameBinding(string hook, string module, string type)
        {
            return string.Equals(Hook, hook, StringComparison.Ordinal)
                && string.Equals(Module, module, StringComparison.Ordinal)
                && string.Equals(Type, type, StringComparison.Ordinal);
        }

        // Returns null when the entry may be stored, otherwise the reason it may not. No check is made on which
        // module name is used (D55) or on the schema version (D51).
        public string Validate()
        {
            if (!BehaviorHooks.IsKnown(Hook))
            {
                return "unknown hook '" + Hook + "' (known hooks: " + string.Join(", ", BehaviorHooks.All) + ").";
            }
            if (!IsValidName(Module)) { return "the module name must be non-empty and contain no spaces."; }
            if (!IsValidName(Type)) { return "the type name must be non-empty and contain no spaces."; }
            if (Config.ToBson().Length > MaxConfigBytes)
            {
                return "the config is larger than " + MaxConfigBytes + " bytes.";
            }
            return null;
        }

        private static bool IsValidName(string name)
        {
            if (string.IsNullOrEmpty(name)) { return false; }
            foreach (char c in name)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c)) { return false; }
            }
            return true;
        }

        public override string ToString()
        {
            return Hook + " " + Module + " " + Type;
        }
    }
}
