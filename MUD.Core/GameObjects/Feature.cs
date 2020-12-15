using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;
using MUD.Core.Interfaces;

namespace MUD.Core
{
    /// A feature of a room. Perhaps we can have a library of these and then build rooms from them?
    public class Feature : IExaminable
    {
        [BsonId(IdGenerator = typeof(StringObjectIdGenerator))]
        [BsonRepresentation(BsonType.ObjectId)]
        public string _id { get; set; }

        public string ShortDescription { get; set; }
        
        public string LongDescription { get; set; }

        public string Examine()
        {
            return LongDescription;
        }
    }
}