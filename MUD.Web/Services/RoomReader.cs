using MongoDB.Bson;
using MongoDB.Driver;

namespace MUD.Web.Services;

public record RoomExitRecord(string Name, string To);

// Definition fields of a room that the map needs. X/Y/Z are null when the room is unplaced.
public record RoomRecord(string Key, string? Area, string Short, int? X, int? Y, int? Z, IReadOnlyList<RoomExitRecord> Exits);

// Read-only view of the game's `room` collection. Does not reference MUD.Core.
public interface IRoomReader
{
    IReadOnlyList<RoomRecord> GetAll();
}

public class MongoRoomReader : IRoomReader
{
    private readonly IMongoCollection<BsonDocument> _rooms;

    public MongoRoomReader(IMongoClient client, string databaseName)
    {
        _rooms = client.GetDatabase(databaseName).GetCollection<BsonDocument>("room");
    }

    public IReadOnlyList<RoomRecord> GetAll()
    {
        // Only the definition fields the map needs; runtime state (Items) is never loaded.
        var docs = _rooms.Find(FilterDefinition<BsonDocument>.Empty)
            .Project(Builders<BsonDocument>.Projection.Include("ShortDescription").Include("Area")
                .Include("X").Include("Y").Include("Z").Include("Exits"))
            .ToList();
        return docs.Select(From).ToList();
    }

    public static RoomRecord From(BsonDocument doc)
    {
        var exits = new List<RoomExitRecord>();
        if (doc.TryGetValue("Exits", out var ex) && ex.IsBsonArray)
        {
            foreach (var e in ex.AsBsonArray)
            {
                if (!e.IsBsonDocument) { continue; }
                var d = e.AsBsonDocument;
                var name = Str(d, "Name");
                var to = Str(d, "DestinationId");
                if (name != null && to != null) { exits.Add(new RoomExitRecord(name, to)); }
            }
        }
        return new RoomRecord(doc["_id"].ToString()!, Str(doc, "Area"), Str(doc, "ShortDescription") ?? string.Empty,
            Int(doc, "X"), Int(doc, "Y"), Int(doc, "Z"), exits);
    }

    private static string? Str(BsonDocument d, string field) =>
        d.TryGetValue(field, out var v) && !v.IsBsonNull ? v.ToString() : null;

    private static int? Int(BsonDocument d, string field) =>
        d.TryGetValue(field, out var v) && v.IsNumeric ? v.ToInt32() : null;
}
