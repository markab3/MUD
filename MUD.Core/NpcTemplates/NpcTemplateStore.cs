using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MUD.Core.NpcTemplates
{
    // An NPC template (specs/NPC_TEMPLATES.md): a reusable NPC definition kept in the library, organised by area. A data library only: nothing in
    // the game reads it yet. The id is "<area>.<local>" like a room key. Lists are never null. A stat that is null is "not set": the default
    // applies (NpcTemplateRules.Default*), and nothing is stored for it.
    public sealed class NpcTemplateEntry
    {
        public string Id { get; set; }
        public string Area { get; set; }
        public string Short { get; set; }
        public string Long { get; set; }
        public List<string> Handles { get; set; } = new List<string>();
        public List<string> Adjectives { get; set; } = new List<string>();
        public List<string> Plurals { get; set; } = new List<string>();
        public int? MaxHealth { get; set; }
        public int? MaxStamina { get; set; }
        public int? MaxAlacrity { get; set; }
        public long? Experience { get; set; }
        public int? Strength { get; set; }
        public int? Dexterity { get; set; }
        public int? Intelligence { get; set; }
        public int? Wisdom { get; set; }
        public int? Constitution { get; set; }
        public int? Charisma { get; set; }
        // 1 on create, +1 on every edit that changes something.
        public int Version { get; set; } = 1;
    }

    // Persistence seam of the NPC templates (collection "npc_template"). Core is the only process that uses it. Methods may throw when the
    // database is unreachable; callers decide how to report that.
    public interface INpcTemplateStore
    {
        // Ordered by id (ordinal); area null or empty = all areas.
        IReadOnlyList<NpcTemplateEntry> List(string area);
        NpcTemplateEntry Get(string id);
        // False when a record with this id already exists (an existing record is never overwritten).
        bool Insert(NpcTemplateEntry entry, string updatedBy);
        // False when no record with this id exists.
        bool Replace(NpcTemplateEntry entry, string updatedBy);
        // False when no record with this id exists.
        bool Delete(string id);
        int CountInArea(string area);
    }

    public static class NpcTemplateDocuments
    {
        public const string Collection = "npc_template";

        public static BsonDocument ToDocument(NpcTemplateEntry e)
        {
            var d = new BsonDocument
            {
                { "_id", e.Id }, { "area", e.Area ?? string.Empty }, { "short", e.Short ?? string.Empty }, { "long", e.Long ?? string.Empty },
                { "handles", new BsonArray(e.Handles ?? new List<string>()) },
                { "adjectives", new BsonArray(e.Adjectives ?? new List<string>()) },
                { "plurals", new BsonArray(e.Plurals ?? new List<string>()) },
            };
            Put(d, "maxHealth", e.MaxHealth); Put(d, "maxStamina", e.MaxStamina); Put(d, "maxAlacrity", e.MaxAlacrity);
            if (e.Experience.HasValue) { d["experience"] = e.Experience.Value; }
            Put(d, "strength", e.Strength); Put(d, "dexterity", e.Dexterity); Put(d, "intelligence", e.Intelligence);
            Put(d, "wisdom", e.Wisdom); Put(d, "constitution", e.Constitution); Put(d, "charisma", e.Charisma);
            d["version"] = e.Version;
            return d;
        }

        // Tolerant of missing or wrongly typed fields: lists come back empty, never null; a stat that is missing or not a number is not set.
        public static NpcTemplateEntry From(BsonDocument d)
        {
            return new NpcTemplateEntry
            {
                Id = Text(d, "_id"), Area = Text(d, "area") ?? string.Empty, Short = Text(d, "short") ?? string.Empty, Long = Text(d, "long") ?? string.Empty,
                Handles = Strings(d, "handles"), Adjectives = Strings(d, "adjectives"), Plurals = Strings(d, "plurals"),
                MaxHealth = Int(d, "maxHealth"), MaxStamina = Int(d, "maxStamina"), MaxAlacrity = Int(d, "maxAlacrity"),
                Experience = d.TryGetValue("experience", out var x) && x.IsNumeric ? x.ToInt64() : (long?)null,
                Strength = Int(d, "strength"), Dexterity = Int(d, "dexterity"), Intelligence = Int(d, "intelligence"),
                Wisdom = Int(d, "wisdom"), Constitution = Int(d, "constitution"), Charisma = Int(d, "charisma"),
                Version = d.TryGetValue("version", out var v) && v.IsNumeric ? v.ToInt32() : 1,
            };
        }

        private static void Put(BsonDocument d, string name, int? value) { if (value.HasValue) { d[name] = value.Value; } }

        private static int? Int(BsonDocument d, string name) { return d.TryGetValue(name, out var v) && v.IsNumeric ? v.ToInt32() : (int?)null; }

        private static string Text(BsonDocument d, string name) { return d.TryGetValue(name, out var v) && v.IsString ? v.AsString : null; }

        private static List<string> Strings(BsonDocument d, string name)
        {
            if (!d.TryGetValue(name, out var v) || !v.IsBsonArray) { return new List<string>(); }
            return v.AsBsonArray.Where(x => x.IsString).Select(x => x.AsString).ToList();
        }
    }

    /// <summary>The NPC templates in MongoDB (collection "npc_template"). Not run against a database in the authoring sandbox.</summary>
    public sealed class MongoNpcTemplateStore : INpcTemplateStore
    {
        private readonly IMongoCollection<BsonDocument> _collection;

        public MongoNpcTemplateStore(IMongoDatabase database)
        {
            _collection = database.GetCollection<BsonDocument>(NpcTemplateDocuments.Collection);
        }

        public IReadOnlyList<NpcTemplateEntry> List(string area)
        {
            FilterDefinition<BsonDocument> filter = string.IsNullOrEmpty(area) ? new BsonDocument() : new BsonDocument("area", area);
            return _collection.Find(filter).Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToList().Select(NpcTemplateDocuments.From).ToList();
        }

        public NpcTemplateEntry Get(string id)
        {
            var d = _collection.Find(new BsonDocument("_id", id)).FirstOrDefault();
            return d == null ? null : NpcTemplateDocuments.From(d);
        }

        public bool Insert(NpcTemplateEntry entry, string updatedBy)
        {
            try { _collection.InsertOne(Stamp(NpcTemplateDocuments.ToDocument(entry), updatedBy)); return true; }
            catch (MongoWriteException ex) when (ex.WriteError != null && ex.WriteError.Category == ServerErrorCategory.DuplicateKey) { return false; }
        }

        public bool Replace(NpcTemplateEntry entry, string updatedBy)
        {
            var d = Stamp(NpcTemplateDocuments.ToDocument(entry), updatedBy);
            return _collection.ReplaceOne(new BsonDocument("_id", d["_id"]), d).MatchedCount > 0;
        }

        public bool Delete(string id)
        {
            return _collection.DeleteOne(new BsonDocument("_id", id)).DeletedCount > 0;
        }

        public int CountInArea(string area)
        {
            return (int)_collection.CountDocuments(new BsonDocument("area", area));
        }

        private static BsonDocument Stamp(BsonDocument d, string updatedBy)
        {
            d["updatedAt"] = DateTime.UtcNow;
            d["updatedBy"] = updatedBy ?? string.Empty;
            return d;
        }
    }
}
