namespace MUD.Core.GameObjects
{
    /// A feature of a room (a table, a statue, a pile of rocks). Perhaps we can have a library of these and then build rooms from them?
    ///
    /// A feature has three independent pieces of static text/data:
    ///   - Handles/Adjectives/Plurals (inherited): the nouns a player can target, matched by the GameObject matching methods.
    ///   - RoomLine: appended to the room's long description when the room is examined. May be empty.
    ///   - DetailDescription: shown when a player looks at this feature.
    public class Feature : GameObject
    {
        public Feature() { }

        /// Text this feature adds to the room description. Null or empty means it adds nothing.
        public virtual string RoomLine { get; set; }

        /// Text shown when the feature itself is looked at.
        public virtual string DetailDescription { get; set; }

        /// The room-description text as seen by the examiner. Base returns RoomLine; subclasses may vary it
        /// per examiner or return null/empty to hide the feature.
        public virtual string GetRoomLine(Living examiner)
        {
            return RoomLine;
        }

        /// The look text as seen by the examiner. Base returns DetailDescription; subclasses may vary it per examiner.
        public virtual string GetDetailDescription(Living examiner)
        {
            return DetailDescription;
        }

        /// Examine with no particular examiner (examiner is null).
        public override string Examine()
        {
            return Examine(null);
        }

        /// Returns GetDetailDescription(examiner). If that is empty (e.g. features created from area files, which
        /// only carry a LongDescription), falls back to LongDescription so they stay lookable.
        public override string Examine(Living examiner)
        {
            var detail = GetDetailDescription(examiner);
            return string.IsNullOrEmpty(detail) ? LongDescription : detail;
        }
    }
}
