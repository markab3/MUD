namespace MUD.Core.GameObjects
{
    /// A feature of a room (a table, a statue, a pile of rocks). Perhaps we can have a library of these and then build rooms from them?
    ///
    /// A feature has three independent pieces of static text/data:
    ///   - Handles/Adjectives/Plurals (inherited): the nouns a player can target, matched by the GameObject matching methods.
    ///   - RoomLine: appended to the room's long description when the room is examined. May be empty.
    ///   - LongDescription (inherited): the one look text, shown when a player looks at this feature.
    public class Feature : GameObject
    {
        public Feature() { }

        /// Text this feature adds to the room description. Null or empty means it adds nothing.
        public virtual string RoomLine { get; set; }

        /// The library feature template this feature was generated from (specs/TERRAIN_GENERATORS.md 5.2). Null for authored features.
        [MongoDB.Bson.Serialization.Attributes.BsonIgnoreIfNull]
        public string TemplateId { get; set; }

        /// The version of that template at generation time.
        [MongoDB.Bson.Serialization.Attributes.BsonIgnoreIfNull]
        public int? TemplateVersion { get; set; }

        /// The room-description text as seen by the examiner. Base returns RoomLine; subclasses may vary it
        /// per examiner or return null/empty to hide the feature.
        public virtual string GetRoomLine(Living examiner)
        {
            return RoomLine;
        }

        /// The look text as seen by the examiner. Base returns LongDescription; subclasses may vary it per examiner.
        public virtual string GetLongDescription(Living examiner)
        {
            return LongDescription;
        }

        /// Examine with no particular examiner (examiner is null).
        public override string Examine()
        {
            return Examine(null);
        }

        /// Returns GetLongDescription(examiner): a feature has one look text.
        public override string Examine(Living examiner)
        {
            return GetLongDescription(examiner);
        }
    }
}
