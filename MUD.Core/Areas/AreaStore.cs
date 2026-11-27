using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Driver;
using MUD.Data;

namespace MUD.Core.Areas
{
    // An area record: the organisational entity behind a room's key prefix. The id is the prefix and never changes.
    public sealed class AreaEntry
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
    }

    // Persistence seam for the area records (collection "area"). Core is the only process that uses it. Methods may throw when the
    // database is unreachable; callers decide how to report that.
    public interface IAreaStore
    {
        // Ordered by id (ordinal).
        IReadOnlyList<AreaEntry> List();
        AreaEntry Get(string id);
        // False when a record with this id already exists (nothing is written, an existing record is never overwritten).
        bool Insert(AreaEntry entry, string updatedBy);
        // False when no record with this id exists.
        bool Replace(AreaEntry entry, string updatedBy);
        // False when no record with this id exists.
        bool Delete(string id);
    }

    public static class AreaDocuments
    {
        public const string Collection = "area";

        public static BsonDocument ToDocument(AreaEntry e)
        {
            return new BsonDocument { { "_id", e.Id }, { "name", e.Name ?? string.Empty }, { "description", e.Description ?? string.Empty } };
        }

        public static AreaEntry From(BsonDocument d)
        {
            return new AreaEntry { Id = Text(d, "_id"), Name = Text(d, "name") ?? string.Empty, Description = Text(d, "description") ?? string.Empty };
        }

        private static string Text(BsonDocument d, string name) { return d.TryGetValue(name, out var v) && v.IsString ? v.AsString : null; }
    }

    /// <summary>The area records in MongoDB (collection "area"). Not run against a database in the authoring sandbox.</summary>
    public sealed class MongoAreaStore : IAreaStore
    {
        private readonly IMongoCollection<BsonDocument> _collection;

        public MongoAreaStore(IMongoDatabase database)
        {
            _collection = database.GetCollection<BsonDocument>(AreaDocuments.Collection);
        }

        public IReadOnlyList<AreaEntry> List()
        {
            return _collection.Find(new BsonDocument()).Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToList().Select(AreaDocuments.From).ToList();
        }

        public AreaEntry Get(string id)
        {
            var d = _collection.Find(new BsonDocument("_id", id)).FirstOrDefault();
            return d == null ? null : AreaDocuments.From(d);
        }

        public bool Insert(AreaEntry entry, string updatedBy)
        {
            try { _collection.InsertOne(Stamp(AreaDocuments.ToDocument(entry), updatedBy)); return true; }
            catch (MongoWriteException ex) when (ex.WriteError != null && ex.WriteError.Category == ServerErrorCategory.DuplicateKey) { return false; }
        }

        public bool Replace(AreaEntry entry, string updatedBy)
        {
            var d = Stamp(AreaDocuments.ToDocument(entry), updatedBy);
            return _collection.ReplaceOne(new BsonDocument("_id", d["_id"]), d).MatchedCount > 0;
        }

        public bool Delete(string id)
        {
            return _collection.DeleteOne(new BsonDocument("_id", id)).DeletedCount > 0;
        }

        private static BsonDocument Stamp(BsonDocument d, string updatedBy)
        {
            d["updatedAt"] = DateTime.UtcNow;
            d["updatedBy"] = updatedBy ?? string.Empty;
            return d;
        }
    }

    // Startup consistency (specs/ADMIN_API.md "Areas"): every distinct Room.Area gets a record when it has none. Idempotent, never overwrites.
    public static class AreaBackfill
    {
        public sealed class Result
        {
            public int Created;
            public int Skipped;     // prefixes that already had a record
            public int Invalid;     // room areas that are not a valid prefix (legacy data); no record is made for them
        }

        public static Result Run(IAreaStore store, IEnumerable<string> roomAreas, string updatedBy = "startup")
        {
            var result = new Result();
            foreach (string area in (roomAreas ?? new string[0]).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal))
            {
                if (RoomRules.CheckAreaPrefix(area) != null) { result.Invalid++; continue; }
                if (store.Insert(new AreaEntry { Id = area, Name = AreaRules.ReadableName(area), Description = string.Empty }, updatedBy)) { result.Created++; }
                else { result.Skipped++; }
            }
            return result;
        }
    }
}
