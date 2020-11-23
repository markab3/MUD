using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MUD.Data;

namespace MUD.Core.Entities
{
    public class LivingEntity : Entity
    {
        public string ShortDescription { get; set; }

        public string LongDescription { get; set; }

        public string Gender { get; set; }

        public string Race { get; set; }

        [BsonRepresentation(BsonType.ObjectId)]
        public string CurrentLocation_id { get; set; }
    }
}