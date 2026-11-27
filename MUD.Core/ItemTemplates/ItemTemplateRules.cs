using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MUD.Core.Components;
using MUD.Data;

namespace MUD.Core.ItemTemplates
{
    // Field rules of an item template (specs/ITEM_TEMPLATES.md). Check methods take already normalized text and return a fixed sentence or null.
    public static class ItemTemplateRules
    {
        public const int MaxWords = 32;          // entries per handles / adjectives / plurals list
        public const int MaxBehaviors = 20;
        public const int MaxComponentBytes = BehaviorRef.MaxConfigBytes;

        // The id is "<area>.<local>": the area prefix, a dot, and a local key (^[a-z0-9_]+$). Returns the problem or null.
        public static string CheckId(string id, string area)
        {
            string problem = RoomRules.CheckAreaPrefix(area);
            if (problem != null) { return problem; }
            if (string.IsNullOrEmpty(id)) { return "id is required."; }
            if (id.Length > RoomRules.MaxKey) { return "id must be at most " + RoomRules.MaxKey + " characters."; }
            if (!id.StartsWith(area + ".", StringComparison.Ordinal)) { return "id must start with the area prefix and a dot (the id is '<area>.<local key>')."; }
            return RoomRules.CheckLocalKey(id.Substring(area.Length + 1));
        }

        // Null lists count as empty. Entries are words (RoomRules.CheckFeatureWord); the list may not repeat an entry.
        public static string CheckWords(string label, IList<string> words)
        {
            if (words == null) { return null; }
            if (words.Count > MaxWords) { return label + " may have at most " + MaxWords + " entries."; }
            foreach (string w in words)
            {
                string problem = RoomRules.CheckFeatureWord(label + " entry", w);
                if (problem != null) { return problem; }
            }
            if (words.Distinct(StringComparer.Ordinal).Count() != words.Count) { return label + " lists an entry twice."; }
            return null;
        }

        // Component data: empty = none; otherwise a JSON object no larger than MaxComponentBytes once stored.
        public static string ParseComponents(string json, out BsonDocument parsed)
        {
            parsed = null;
            if (string.IsNullOrWhiteSpace(json)) { return null; }
            try { parsed = BsonDocument.Parse(json); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return "component data must be a JSON object."; }
            if (parsed.ToBson().Length > MaxComponentBytes) { parsed = null; return "component data is larger than " + MaxComponentBytes + " bytes."; }
            return null;
        }

        // A behavior entry as BehaviorRef.Validate checks it, with its config parsed from JSON (empty = {}).
        public static string ParseBehavior(string hook, string module, string type, int priority, string configJson, out BehaviorRef parsed)
        {
            parsed = null;
            BsonDocument config = new BsonDocument();
            if (!string.IsNullOrWhiteSpace(configJson))
            {
                try { config = BsonDocument.Parse(configJson); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { return "the behavior config must be a JSON object."; }
            }
            // Fixed sentences: BehaviorRef.Validate echoes the hook, so that case is answered here.
            if (!BehaviorHooks.IsKnown(hook)) { return "behavior: unknown hook (known hooks: " + string.Join(", ", BehaviorHooks.All) + ")."; }
            var entry = new BehaviorRef(hook, module, type, priority, config);
            string problem = entry.Validate();
            if (problem != null) { return "behavior: " + problem; }
            parsed = entry;
            return null;
        }
    }
}
