using MongoDB.Bson;
using MongoDB.Driver;

namespace MUD.Web.Services;

// Collection `audit`: append-only from the website's point of view (insert, plus flipping Applied on rows it just wrote).
// One row per changed field (specs/WEB_EDITING.md section 7). Values come only from allow-listed room fields.
public class MongoAuditLog : IAuditLog
{
    private readonly IMongoCollection<BsonDocument> _audit;

    public MongoAuditLog(IMongoClient client, string databaseName)
    {
        _audit = client.GetDatabase(databaseName).GetCollection<BsonDocument>("audit");
    }

    public void Insert(IReadOnlyList<AuditEntry> entries)
    {
        if (entries.Count == 0) { return; }
        _audit.InsertMany(entries.Select(ToDocument));
    }

    public void SetOutcome(IReadOnlyList<string> ids, bool applied)
    {
        if (ids.Count == 0) { return; }
        var objectIds = new BsonArray(ids.Select(i => (BsonValue)ObjectId.Parse(i)));
        var update = applied
            ? new BsonDocument("$set", new BsonDocument("Applied", true))
            : new BsonDocument("$set", new BsonDocument { { "Applied", false }, { "Rejected", true } });
        _audit.UpdateMany(new BsonDocument("_id", new BsonDocument("$in", objectIds)),
            new BsonDocumentUpdateDefinition<BsonDocument>(update));
    }

    public static BsonDocument ToDocument(AuditEntry e) => new()
    {
        { "_id", ObjectId.Parse(e.Id) },
        { "At", e.At },
        { "ActorId", e.ActorId },
        { "ActorName", e.ActorName },
        { "ActorRole", e.ActorRole },
        { "Entity", e.Entity },
        { "EntityId", e.EntityId },
        { "EntityName", e.EntityName },
        { "Field", e.Field },
        { "Old", e.Old },
        { "New", e.New },
        { "RevBefore", e.RevBefore },
        { "RevAfter", e.RevAfter },
        { "Source", "web" },
        { "Applied", false },
    };
}
