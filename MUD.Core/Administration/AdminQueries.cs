using System.Collections.Generic;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MUD.Core.Administration
{
    /// <summary>
    /// The read-only listings the Admin service needs that the entity repositories do not offer (they find by id or by a typed
    /// expression and load live objects into their cache; a listing must not). Every method reads stored documents and never touches
    /// the live objects, so it is safe from any thread. Player documents are read through an inclusion projection that does not name
    /// the password, so the password is never loaded by a listing.
    /// </summary>
    public interface IAdminQueries
    {
        /// <summary>Stored player documents (name key, name, race, class, gender, location, retired, roles), sorted by name key. page is 1-based.</summary>
        List<BsonDocument> ListPlayers(string nameFilter, bool descending, int page, int pageSize, out int total);

        /// <summary>One stored player document (same fields as the listing, plus the descriptions), or null.</summary>
        BsonDocument FindPlayer(string nameKey);

        /// <summary>How many players hold the admin flag in their roles component.</summary>
        int CountAdmins();

        /// <summary>Every stored room, definition fields only (never Items).</summary>
        List<BsonDocument> ListRooms();

        /// <summary>Ids of the rooms that have an exit leading to the given room key.</summary>
        List<string> RoomIdsWithExitTo(string key);
    }

    /// <summary>Reads the "player" and "room" collections directly. This is core code: core is the only process that talks to the database.</summary>
    public sealed class MongoAdminQueries : IAdminQueries
    {
        private static readonly ProjectionDefinition<BsonDocument> PlayerProjection = Builders<BsonDocument>.Projection
            .Include("PlayerName").Include("NameKey").Include("ShortDescription").Include("LongDescription").Include("Race").Include("Gender").Include("Class")
            .Include("CurrentLocationId").Include("Retired").Include(MUD.Data.PlayerFields.Roles);

        private static readonly ProjectionDefinition<BsonDocument> RoomProjection = Builders<BsonDocument>.Projection
            .Include("ShortDescription").Include("LongDescription").Include("Area").Include("X").Include("Y").Include("Z").Include("Exits");

        private readonly IMongoCollection<BsonDocument> _players;
        private readonly IMongoCollection<BsonDocument> _rooms;

        public MongoAdminQueries(IMongoDatabase database)
        {
            _players = database.GetCollection<BsonDocument>("player");
            _rooms = database.GetCollection<BsonDocument>("room");
        }

        public List<BsonDocument> ListPlayers(string nameFilter, bool descending, int page, int pageSize, out int total)
        {
            FilterDefinition<BsonDocument> filter = Filter(nameFilter);
            total = (int)_players.CountDocuments(filter);
            SortDefinition<BsonDocument> sort = descending ? Builders<BsonDocument>.Sort.Descending("NameKey") : Builders<BsonDocument>.Sort.Ascending("NameKey");
            return _players.Find(filter).Project(PlayerProjection).Sort(sort).Skip((page - 1) * pageSize).Limit(pageSize).ToList();
        }

        // Case-insensitive substring on PlayerName. The text is escaped, so it can never act as a pattern.
        public static FilterDefinition<BsonDocument> Filter(string nameFilter)
        {
            string text = (nameFilter ?? string.Empty).Trim();
            if (text.Length == 0) { return Builders<BsonDocument>.Filter.Empty; }
            return Builders<BsonDocument>.Filter.Regex("PlayerName", new BsonRegularExpression(Regex.Escape(text), "i"));
        }

        public BsonDocument FindPlayer(string nameKey)
        {
            if (string.IsNullOrEmpty(nameKey)) { return null; }
            return _players.Find(new BsonDocument("NameKey", nameKey)).Project(PlayerProjection).FirstOrDefault();
        }

        public int CountAdmins()
        {
            return (int)_players.CountDocuments(new BsonDocument(MUD.Data.PlayerFields.RolesAdmin, true));
        }

        public List<BsonDocument> ListRooms()
        {
            return _rooms.Find(FilterDefinition<BsonDocument>.Empty).Project(RoomProjection).ToList();
        }

        public List<string> RoomIdsWithExitTo(string key)
        {
            var filter = new BsonDocument("Exits", new BsonDocument("$elemMatch", new BsonDocument("DestinationId", key)));
            var ids = new List<string>();
            foreach (BsonDocument document in _rooms.Find(filter).Project(new BsonDocument("_id", 1)).ToList())
            {
                ids.Add(document["_id"].ToString());
            }
            return ids;
        }
    }
}
