using MongoDB.Bson.Serialization.Attributes;

namespace MUD.Core.GameObjects
{
    // Stored generator provenance of a room (stored field "gen"; specs/TERRAIN_GENERATORS.md section 5.1).
    // Present only on rooms written from a generated area file. Its presence means "the generator owns this room".
    [BsonIgnoreExtraElements]
    public class GenProvenance
    {
        [BsonElement("map")]
        public string Map { get; set; }

        [BsonElement("terrain")]
        [BsonIgnoreIfNull]
        public string Terrain { get; set; }

        [BsonElement("seed")]
        [BsonIgnoreIfNull]
        public string Seed { get; set; }

        [BsonElement("libVersion")]
        [BsonIgnoreIfNull]
        public int? LibVersion { get; set; }
    }
}
