using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Driver;
using MUD.Core.Components;

namespace MUD.Core.ItemTemplates
{
    // An item template (specs/ITEM_TEMPLATES.md): a reusable item definition kept in the library. The id is "<area>.<local>" like a room key,
    // so the area is the key prefix. Lists are never null.
    public sealed class ItemTemplateEntry
    {
        public string Id { get; set; }
        public string Area { get; set; }
        public string Short { get; set; }
        public string Long { get; set; }
        public List<string> Handles { get; set; } = new List<string>();
        public List<string> Adjectives { get; set; } = new List<string>();
        public List<string> Plurals { get; set; } = new List<string>();
        // Free component data; null = none.
        public BsonDocument Components { get; set; }
        public List<BehaviorRef> Behaviors { get; set; } = new List<BehaviorRef>();
        // 1 on create, +1 on every edit that changes something.
        public int Version { get; set; } = 1;
    }

    // Persistence seam of the item templates (collection "item_template"). Core is the only process that uses it. Methods may throw when the
    // database is unreachable; callers decide how to report that.
    public interface IItemTemplateStore
    {
        // Ordered by id (ordinal); area null or empty = all areas.
        IReadOnlyList<ItemTemplateEntry> List(string area);
        ItemTemplateEntry Get(string id);
        // False when a record with this id already exists (an existing record is never overwritten).
        bool Insert(ItemTemplateEntry entry, string updatedBy);
        // False when no record with this id exists.
        bool Replace(ItemTemplateEntry entry, string updatedBy);
        // False when no record with this id exists.
        bool Delete(string id);
        int CountInArea(string area);
    }

    public static class ItemTemplateDocuments
    {
        public const string Collection = "item_template";

        public static BsonDocument ToDocument(ItemTemplateEntry e)
        {
            var d = new BsonDocument
            {
                { "_id", e.Id }, { "area", e.Area ?? string.Empty }, { "short", e.Short ?? string.Empty }, { "long", e.Long ?? string.Empty },
                { "handles", new BsonArray(e.Handles ?? new List<string>()) },
                { "adjectives", new BsonArray(e.Adjectives ?? new List<string>()) },
                { "plurals", new BsonArray(e.Plurals ?? new List<string>()) },
            };
            if (e.Components != null) { d["components"] = e.Components.DeepClone(); }
            d["behaviors"] = new BsonArray((e.Behaviors ?? new List<BehaviorRef>()).Where(b => b != null).Select(b => new BsonDocument
            {
                { "hook", b.Hook ?? string.Empty }, { "module", b.Module ?? string.Empty }, { "type", b.Type ?? string.Empty },
                { "priority", b.Priority }, { "config", b.Config.DeepClone() }, { "schemaVersion", b.SchemaVersion },
            }));
            d["version"] = e.Version;
            return d;
        }

        // Tolerant of missing or wrongly typed fields: lists come back empty, never null.
        public static ItemTemplateEntry From(BsonDocument d)
        {
            var e = new ItemTemplateEntry
            {
                Id = Text(d, "_id"), Area = Text(d, "area") ?? string.Empty, Short = Text(d, "short") ?? string.Empty, Long = Text(d, "long") ?? string.Empty,
                Handles = Strings(d, "handles"), Adjectives = Strings(d, "adjectives"), Plurals = Strings(d, "plurals"),
                Version = d.TryGetValue("version", out var v) && v.IsNumeric ? v.ToInt32() : 1,
            };
            if (d.TryGetValue("components", out var c) && c.IsBsonDocument) { e.Components = c.AsBsonDocument.DeepClone().AsBsonDocument; }
            if (d.TryGetValue("behaviors", out var bs) && bs.IsBsonArray)
            {
                foreach (BsonValue item in bs.AsBsonArray.Where(x => x.IsBsonDocument))
                {
                    BsonDocument b = item.AsBsonDocument;
                    e.Behaviors.Add(new BehaviorRef(Text(b, "hook"), Text(b, "module"), Text(b, "type"), b.TryGetValue("priority", out var p) && p.IsNumeric ? p.ToInt32() : 0,
                        b.TryGetValue("config", out var cfg) && cfg.IsBsonDocument ? cfg.AsBsonDocument : null,
                        b.TryGetValue("schemaVersion", out var sv) && sv.IsNumeric ? sv.ToInt32() : 1));
                }
            }
            return e;
        }

        private static string Text(BsonDocument d, string name) { return d.TryGetValue(name, out var v) && v.IsString ? v.AsString : null; }

        private static List<string> Strings(BsonDocument d, string name)
        {
            if (!d.TryGetValue(name, out var v) || !v.IsBsonArray) { return new List<string>(); }
            return v.AsBsonArray.Where(x => x.IsString).Select(x => x.AsString).ToList();
        }
    }

    /// <summary>The item templates in MongoDB (collection "item_template"). Not run against a database in the authoring sandbox.</summary>
    public sealed class MongoItemTemplateStore : IItemTemplateStore
    {
        private readonly IMongoCollection<BsonDocument> _collection;

        public MongoItemTemplateStore(IMongoDatabase database)
        {
            _collection = database.GetCollection<BsonDocument>(ItemTemplateDocuments.Collection);
        }

        public IReadOnlyList<ItemTemplateEntry> List(string area)
        {
            FilterDefinition<BsonDocument> filter = string.IsNullOrEmpty(area) ? new BsonDocument() : new BsonDocument("area", area);
            return _collection.Find(filter).Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToList().Select(ItemTemplateDocuments.From).ToList();
        }

        public ItemTemplateEntry Get(string id)
        {
            var d = _collection.Find(new BsonDocument("_id", id)).FirstOrDefault();
            return d == null ? null : ItemTemplateDocuments.From(d);
        }

        public bool Insert(ItemTemplateEntry entry, string updatedBy)
        {
            try { _collection.InsertOne(Stamp(ItemTemplateDocuments.ToDocument(entry), updatedBy)); return true; }
            catch (MongoWriteException ex) when (ex.WriteError != null && ex.WriteError.Category == ServerErrorCategory.DuplicateKey) { return false; }
        }

        public bool Replace(ItemTemplateEntry entry, string updatedBy)
        {
            var d = Stamp(ItemTemplateDocuments.ToDocument(entry), updatedBy);
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
