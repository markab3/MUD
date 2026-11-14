using MongoDB.Bson.Serialization.Attributes;

namespace MUD.Core.Components
{
    // The core component "roles" (owner decisions D45, G30): what a living may do beyond the default commands.
    // It replaces the Creator and Admin properties. It is stored as the embedded document
    // "roles": { creator, admin, currentDirectory } on the entity, and the website reads creator and admin from it
    // (MongoAccountStore, MongoPlayerEditStore), so the field names are part of a contract between two processes.
    [BsonIgnoreExtraElements]
    public class Roles
    {
        public const string FieldName = "roles";

        // May use the creator commands.
        [BsonElement("creator")]
        public bool Creator { get; set; }

        // May use the admin commands and promote others. The website treats admin as the highest role.
        [BsonElement("admin")]
        public bool Admin { get; set; }

        // The creator's current directory (the cd command); null until one is set.
        [BsonElement("currentDirectory")]
        public string CurrentDirectory { get; set; }
    }
}
