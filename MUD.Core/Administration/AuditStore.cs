using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MUD.Core.Administration
{
    /// <summary>One row of the per-field audit trail (specs/WEB_EDITING.md section 7, owner decisions D30, D34). One row per changed field.</summary>
    public sealed class AuditRecord
    {
        public string Id { get; set; }
        public DateTime At { get; set; }
        public string ActorId { get; set; }
        public string ActorName { get; set; }
        public string ActorRole { get; set; }
        public string Entity { get; set; }
        public string EntityId { get; set; }
        public string EntityName { get; set; }
        public string Field { get; set; }
        public BsonValue Old { get; set; }
        public BsonValue New { get; set; }
        public string Source { get; set; }
        public bool Applied { get; set; }
        public bool Rejected { get; set; }
    }

    public sealed class AuditPage
    {
        public IReadOnlyList<AuditRecord> Items { get; set; }
        public int Total { get; set; }
    }

    /// <summary>
    /// Where the audit rows live. Core is the only process that writes them (D34: web and creator edits are audited; module changes are not).
    /// Audit-first: rows are inserted with Applied false before the edit, and marked applied or rejected afterwards.
    /// </summary>
    public interface IAuditStore
    {
        /// <summary>Appends the rows. Throws when they cannot be written; the edit is then refused.</summary>
        void Insert(IReadOnlyList<AuditRecord> rows);

        /// <summary>Marks rows written by Insert as applied, or as rejected when the edit they announced did not happen.</summary>
        void SetOutcome(IReadOnlyList<string> ids, bool applied);

        /// <summary>Newest first. entity and entityId are optional filters (null or empty matches everything). Page is 1-based.</summary>
        AuditPage Query(string entity, string entityId, int page, int pageSize);
    }

    /// <summary>The audit trail in the database collection "audit".</summary>
    public sealed class MongoAuditStore : IAuditStore
    {
        public const string CollectionName = "audit";

        private readonly IMongoCollection<BsonDocument> _audit;

        public MongoAuditStore(IMongoDatabase database)
        {
            _audit = database.GetCollection<BsonDocument>(CollectionName);
        }

        public void Insert(IReadOnlyList<AuditRecord> rows)
        {
            if (rows == null || rows.Count == 0) { return; }
            _audit.InsertMany(rows.Select(ToDocument));
        }

        public void SetOutcome(IReadOnlyList<string> ids, bool applied)
        {
            if (ids == null || ids.Count == 0) { return; }
            var objectIds = new BsonArray(ids.Select(i => (BsonValue)ObjectId.Parse(i)));
            var update = applied
                ? new BsonDocument("$set", new BsonDocument("Applied", true))
                : new BsonDocument("$set", new BsonDocument { { "Applied", false }, { "Rejected", true } });
            _audit.UpdateMany(new BsonDocument("_id", new BsonDocument("$in", objectIds)), new BsonDocumentUpdateDefinition<BsonDocument>(update));
        }

        public AuditPage Query(string entity, string entityId, int page, int pageSize)
        {
            var filter = new BsonDocument();
            if (!string.IsNullOrEmpty(entity)) { filter["Entity"] = entity; }
            if (!string.IsNullOrEmpty(entityId)) { filter["EntityId"] = entityId; }
            int total = (int)_audit.CountDocuments(filter);
            List<BsonDocument> documents = _audit.Find(filter)
                .Sort(Builders<BsonDocument>.Sort.Descending("At").Descending("_id"))
                .Skip((page - 1) * pageSize).Limit(pageSize).ToList();
            return new AuditPage { Items = documents.Select(FromDocument).ToList(), Total = total };
        }

        public static BsonDocument ToDocument(AuditRecord row)
        {
            return new BsonDocument
            {
                { "_id", ObjectId.Parse(row.Id) },
                { "At", row.At },
                { "ActorId", row.ActorId ?? string.Empty },
                { "ActorName", row.ActorName ?? string.Empty },
                { "ActorRole", row.ActorRole ?? string.Empty },
                { "Entity", row.Entity },
                { "EntityId", row.EntityId },
                { "EntityName", row.EntityName ?? string.Empty },
                { "Field", row.Field },
                { "Old", row.Old ?? BsonNull.Value },
                { "New", row.New ?? BsonNull.Value },
                { "Source", row.Source ?? "web" },
                { "Applied", false },
            };
        }

        // Rows written by the earlier website code also carry RevBefore and RevAfter; they are not read (D30).
        public static AuditRecord FromDocument(BsonDocument document)
        {
            return new AuditRecord
            {
                Id = document["_id"].ToString(),
                At = document.GetValue("At", BsonNull.Value).IsValidDateTime ? document["At"].ToUniversalTime() : DateTime.MinValue,
                ActorId = Text(document, "ActorId"),
                ActorName = Text(document, "ActorName"),
                ActorRole = Text(document, "ActorRole"),
                Entity = Text(document, "Entity"),
                EntityId = Text(document, "EntityId"),
                EntityName = Text(document, "EntityName"),
                Field = Text(document, "Field"),
                Old = document.GetValue("Old", BsonNull.Value),
                New = document.GetValue("New", BsonNull.Value),
                Source = Text(document, "Source"),
                Applied = Flag(document, "Applied"),
                Rejected = Flag(document, "Rejected"),
            };
        }

        private static string Text(BsonDocument document, string name)
        {
            BsonValue value;
            return document.TryGetValue(name, out value) && !value.IsBsonNull ? value.ToString() : string.Empty;
        }

        private static bool Flag(BsonDocument document, string name)
        {
            BsonValue value;
            return document.TryGetValue(name, out value) && value.IsBoolean && value.AsBoolean;
        }
    }
}
