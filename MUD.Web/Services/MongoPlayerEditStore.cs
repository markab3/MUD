using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using MUD.Data;

namespace MUD.Web.Services;

// Reads and writes the game's `player` collection for the website editor. Reads use an explicit INCLUSION projection that
// does not name Password, so the password is never loaded into this process by the editor. Writes are a field-level $set
// built by PlayerWriter (allow-list only) plus $inc Rev; nothing here ever replaces a document.
public class MongoPlayerEditStore : IPlayerEditStore
{
    private static readonly ProjectionDefinition<BsonDocument> Projection = Builders<BsonDocument>.Projection
        .Include("PlayerName").Include("NameKey").Include("ShortDescription").Include("LongDescription").Include("Race")
        .Include("Gender").Include("Class").Include("CurrentLocationId").Include("Retired").Include("Rev")
        .Include("ExtendedProperties");

    private readonly IMongoCollection<BsonDocument> _players;

    public MongoPlayerEditStore(IMongoClient client, string databaseName)
    {
        _players = client.GetDatabase(databaseName).GetCollection<BsonDocument>("player");
    }

    public PlayerPage List(string? nameFilter, bool descending, int pageNumber, int pageSize)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        pageNumber = Math.Max(1, pageNumber);
        var filter = Filter(nameFilter);
        var total = (int)_players.CountDocuments(filter);
        var sort = descending ? Builders<BsonDocument>.Sort.Descending("NameKey") : Builders<BsonDocument>.Sort.Ascending("NameKey");
        var docs = _players.Find(filter).Project(Projection).Sort(sort).Skip((pageNumber - 1) * pageSize).Limit(pageSize).ToList();
        return new PlayerPage(docs.Select(Summary).ToList(), total, pageNumber, pageSize);
    }

    // Case-insensitive substring on PlayerName. The text is escaped, so it can never act as a pattern.
    public static FilterDefinition<BsonDocument> Filter(string? nameFilter)
    {
        var text = (nameFilter ?? string.Empty).Trim();
        if (text.Length == 0) { return Builders<BsonDocument>.Filter.Empty; }
        return Builders<BsonDocument>.Filter.Regex("PlayerName", new BsonRegularExpression(Regex.Escape(text), "i"));
    }

    public PlayerDetail? Find(string nameKey)
    {
        if (string.IsNullOrEmpty(nameKey)) { return null; }
        var doc = _players.Find(Builders<BsonDocument>.Filter.Eq("NameKey", nameKey)).Project(Projection).FirstOrDefault();
        return doc == null ? null : Detail(doc);
    }

    public StoreUpdateOutcome UpdateIfRev(string id, int expectedRev, BsonDocument set)
    {
        var filter = new BsonDocument("_id", id);
        filter.AddRange(MongoRoomEditStore.RevFilter(expectedRev));
        var update = new BsonDocument { { "$set", set }, { "$inc", new BsonDocument("Rev", 1) } };
        var result = _players.UpdateOne(filter, new BsonDocumentUpdateDefinition<BsonDocument>(update));
        if (result.IsAcknowledged && result.MatchedCount == 1) { return StoreUpdateOutcome.Applied; }
        return _players.CountDocuments(new BsonDocument("_id", id), new CountOptions { Limit = 1 }) > 0
            ? StoreUpdateOutcome.Conflict : StoreUpdateOutcome.Missing;
    }

    public int CountAdmins()
    {
        // Same role rule as login: any _t entry (scalar or hierarchy array) that names AdminProperty.
        var count = 0;
        foreach (var doc in _players.Find(new BsonDocument("ExtendedProperties._t", "AdminProperty")).Project(Projection).ToList())
        {
            if (MongoAccountStore.RoleFrom(doc) == AccountRole.Admin) { count++; }
        }
        return count;
    }

    public static PlayerSummary Summary(BsonDocument doc) => new(
        Str(doc, "NameKey") ?? string.Empty, Str(doc, "PlayerName") ?? string.Empty, Str(doc, "Race"), Str(doc, "Class"),
        Str(doc, "Gender"), Str(doc, "CurrentLocationId"), MongoAccountStore.RoleFrom(doc), Bool(doc, "Retired"));

    public static PlayerDetail Detail(BsonDocument doc) => new(
        doc["_id"].ToString()!, Str(doc, "NameKey") ?? string.Empty, Str(doc, "PlayerName") ?? string.Empty,
        Str(doc, "ShortDescription"), Str(doc, "LongDescription"), Str(doc, "Race"), Str(doc, "Gender"), Str(doc, "Class"),
        Bool(doc, "Retired"), MongoAccountStore.RoleFrom(doc),
        doc.TryGetValue("Rev", out var rev) && rev.IsNumeric ? rev.ToInt32() : 0,
        doc.TryGetValue("ExtendedProperties", out var props) && props.IsBsonArray ? props.AsBsonArray : new BsonArray());

    private static string? Str(BsonDocument d, string field) =>
        d.TryGetValue(field, out var v) && v.IsString ? v.AsString : null;

    private static bool Bool(BsonDocument d, string field) =>
        d.TryGetValue(field, out var v) && v.IsBoolean && v.AsBoolean;
}
