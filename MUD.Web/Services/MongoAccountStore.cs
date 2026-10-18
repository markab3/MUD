using MongoDB.Bson;
using MongoDB.Driver;

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
            .Project(Builders<BsonDocument>.Projection.Include("PlayerName").Include("Password").Include("ExtendedProperties").Include("Retired"))
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

    // Role from the BSON discriminators (_t) of ExtendedProperties. The discriminator may be a scalar or a
    // hierarchy array; admin > creator > player.
    public static AccountRole RoleFrom(BsonDocument doc)
    {
        var role = AccountRole.Player;
        if (!doc.TryGetValue("ExtendedProperties", out var props) || !props.IsBsonArray) { return role; }
        foreach (var prop in props.AsBsonArray)
        {
            if (!prop.IsBsonDocument || !prop.AsBsonDocument.TryGetValue("_t", out var t)) { continue; }
            var names = t.IsBsonArray ? t.AsBsonArray.Select(x => x.ToString()) : new[] { t.ToString()! };
            foreach (var n in names)
            {
                var simple = n!.Split('.').Last();
                if (simple == "AdminProperty") { return AccountRole.Admin; }
                if (simple == "CreatorProperty") { role = AccountRole.Creator; }
            }
        }
        return role;
    }
}
