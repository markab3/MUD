using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;

namespace MUD.Core
{
    // This class is super small right now, but might grow to include doors, or locked/unlocked or whatever.
    public class Exit
    {
        public string Name { get; set; }

        [BsonRepresentation(BsonType.ObjectId)]
        public string Destination_id { get; set; }
    }
}