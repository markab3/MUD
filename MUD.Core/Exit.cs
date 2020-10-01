using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;
using MUD.Core.Commands;

namespace MUD.Core
{
    public class Exit
    {
        [BsonId(IdGenerator = typeof(StringObjectIdGenerator))]
        [BsonRepresentation(BsonType.ObjectId)]
        public string _id { get; set; }

        public string Name { get; set; }

        [BsonRepresentation(BsonType.ObjectId)]
        public string Destination_id { get; set; }

        [BsonIgnore]
        public Room Destination
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Destination_id))
                {
                    return null;
                }
                return World.Instance.Rooms.FirstOrDefault(r => r._id == Destination_id);
            }
        }
    }
}