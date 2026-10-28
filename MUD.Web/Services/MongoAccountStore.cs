using MongoDB.Bson;
using MongoDB.Driver;
using MUD.Data;

namespace MUD.Web.Services;

// View of the game's `player` collection (reads, plus the single-field password update). Does not reference MUD.Core.
public class MongoAccountStore : IAccountStore
{
    private readonly IMongoCollection<BsonDocument> _players;

    public MongoAccountStore(IMongoClient client, string databaseName)
    {
        _players = client.GetDatabase(databaseName).GetCollection<BsonDocument>("player");
    }

    public StoredAccount? FindByNameKey(string nameKey)
    {
        var doc = _players.Find(Builders<BsonDocument>.Filter.Eq("NameKey", nameKey))
            .Project(Builders<BsonDocument>.Projection.Include("PlayerName").Include("Password").Include(PlayerFields.Roles).Include("Retired"))
            .FirstOrDefault();
        if (doc == null) { return null; }
        var name = doc.GetValue("PlayerName", BsonNull.Value);
        if (!name.IsString) { return null; }
        var password = doc.GetValue("Password", BsonNull.Value);
        return new StoredAccount(name.AsString, password.IsString ? password.AsString : null, RoleFrom(doc), doc.GetValue("_id", BsonNull.Value).IsBsonNull ? null : doc["_id"].ToString(),
            doc.GetValue("Retired", BsonNull.Value) is { IsBoolean: true } r && r.AsBoolean);
    }

    // $set of Password only: never replaces the document, so nothing else the game wrote is touched.
    public bool UpdatePassword(string nameKey, string newPassword)
    {
        if (string.IsNullOrEmpty(nameKey)) { return false; }
        try
        {
            var result = _players.UpdateOne(
                Builders<BsonDocument>.Filter.Eq("NameKey", nameKey),
                Builders<BsonDocument>.Update.Set("Password", newPassword));
            return result.IsAcknowledged && result.MatchedCount == 1;
        }
        catch (MongoException)
        {
            return false;
        }
    }

    // Role from the core "roles" component the game stores on the player: { creator: bool, admin: bool, currentDirectory }
    // (owner decisions D45, G30). Admin > creator > player. The old ExtendedProperties shape is not read (D1).
    public static AccountRole RoleFrom(BsonDocument doc)
    {
        if (!doc.TryGetValue(PlayerFields.Roles, out var roles) || !roles.IsBsonDocument) { return AccountRole.Player; }
        if (Flag(roles.AsBsonDocument, "admin")) { return AccountRole.Admin; }
        if (Flag(roles.AsBsonDocument, "creator")) { return AccountRole.Creator; }
        return AccountRole.Player;
    }

    private static bool Flag(BsonDocument roles, string name) =>
        roles.TryGetValue(name, out var value) && value.IsBoolean && value.AsBoolean;
}
