using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;

namespace MUD.Core
{
    public class Player
    {
        [BsonId(IdGenerator = typeof(StringObjectIdGenerator))] 
        [BsonRepresentation(BsonType.ObjectId)]
        public string _id { get; set; }
        public string PlayerName { get; set; }
        public string Password { get; set; }
        public string Gender { get; set; }
        public string Race { get; set; }
        public string Class { get; set; }
        public Room CurrentLocation { get; set; }
    }
}