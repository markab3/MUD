using System.Collections.Generic;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MUD.Core.GameObjects
{
    public class Living : GameObject
    {
        public string Gender { get; set; }

        public string Race { get; set; }

        [BsonRepresentation(BsonType.ObjectId)]
        public string CurrentLocationId { get; set; }
        
        public string Class { get; set; }

        public string[] KnownCommands { get; set; }

        public List<string> Inventory { get; set; }
    }
}