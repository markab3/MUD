using MongoDB.Bson;
using MongoDB.Driver;

namespace MUD.Web.Services;

// Reads and writes the game's `room` collection for the website editor. Only definition fields are read or written;
// Items, Features and other state are never loaded here and never appear in an update document.
public class MongoRoomEditStore : IRoomEditStore
{
    private static readonly ProjectionDefinition<BsonDocument> Projection = Builders<BsonDocument>.Projection
        .Include("ShortDescription").Include("LongDescription").Include("Area").Include("X").Include("Y").Include("Z")
        .Include("Exits").Include("Rev");

    private readonly IMongoCollection<BsonDocument> _rooms;

    public MongoRoomEditStore(IMongoClient client, string databaseName)
    {
        _rooms = client.GetDatabase(databaseName).GetCollection<BsonDocument>("room");
    }

    public RoomDetail? Find(string key)
    {
        var doc = _rooms.Find(new BsonDocument("_id", key)).Project(Projection).FirstOrDefault();
        return doc == null ? null : From(doc);
    }

    public bool Exists(string key) =>
        _rooms.CountDocuments(new BsonDocument("_id", key), new CountOptions { Limit = 1 }) > 0;

    public StoreUpdateOutcome UpdateIfRev(string key, int expectedRev, BsonDocument set)
    {
        var filter = new BsonDocument("_id", key);
        filter.AddRange(RevFilter(expectedRev));
        var update = new BsonDocument { { "$set", set }, { "$inc", new BsonDocument("Rev", 1) } };
        var result = _rooms.UpdateOne(filter, new BsonDocumentUpdateDefinition<BsonDocument>(update));
        if (result.IsAcknowledged && result.MatchedCount == 1) { return StoreUpdateOutcome.Applied; }
        return Exists(key) ? StoreUpdateOutcome.Conflict : StoreUpdateOutcome.Missing;
    }

    public bool InsertIfAbsent(string key, BsonDocument document)
    {
        var body = (BsonDocument)document.DeepClone();
        body.Remove("_id");
        var result = _rooms.UpdateOne(new BsonDocument("_id", key),
            new BsonDocumentUpdateDefinition<BsonDocument>(new BsonDocument("$setOnInsert", body)),
            new UpdateOptions { IsUpsert = true });
        return result.IsAcknowledged && result.UpsertedId != null;
    }

    public StoreUpdateOutcome DeleteIfRev(string key, int expectedRev)
    {
        var filter = new BsonDocument("_id", key);
        filter.AddRange(RevFilter(expectedRev));
        var result = _rooms.DeleteOne(filter);
        if (result.IsAcknowledged && result.DeletedCount == 1) { return StoreUpdateOutcome.Applied; }
        return Exists(key) ? StoreUpdateOutcome.Conflict : StoreUpdateOutcome.Missing;
    }

    public IReadOnlyList<RoomDetail> FindReferencing(string key)
    {
        var filter = new BsonDocument("Exits", new BsonDocument("$elemMatch", new BsonDocument("DestinationId", key)));
        return _rooms.Find(filter).Project(Projection).ToList().Select(From).ToList();
    }

    // A missing Rev counts as 0.
    public static BsonDocument RevFilter(int expectedRev) =>
        expectedRev == 0
            ? new BsonDocument("$or", new BsonArray
            {
                new BsonDocument("Rev", new BsonDocument("$exists", false)),
                new BsonDocument("Rev", BsonNull.Value),
                new BsonDocument("Rev", 0),
            })
            : new BsonDocument("Rev", expectedRev);

    public static RoomDetail From(BsonDocument doc)
    {
        var exits = new List<RoomExitData>();
        if (doc.TryGetValue("Exits", out var ex) && ex.IsBsonArray)
        {
            foreach (var e in ex.AsBsonArray)
            {
                if (!e.IsBsonDocument) { continue; }
                var name = Str(e.AsBsonDocument, "Name");
                var to = Str(e.AsBsonDocument, "DestinationId");
                if (name != null && to != null) { exits.Add(new RoomExitData(name, to)); }
            }
        }
        return new RoomDetail(doc["_id"].ToString()!, Str(doc, "ShortDescription") ?? string.Empty,
            Str(doc, "LongDescription") ?? string.Empty, Str(doc, "Area"), Int(doc, "X"), Int(doc, "Y"), Int(doc, "Z"),
            exits, Int(doc, "Rev") ?? 0);
    }

    private static string? Str(BsonDocument d, string field) =>
        d.TryGetValue(field, out var v) && !v.IsBsonNull ? v.ToString() : null;

    private static int? Int(BsonDocument d, string field) =>
        d.TryGetValue(field, out var v) && v.IsNumeric ? v.ToInt32() : null;
}

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
