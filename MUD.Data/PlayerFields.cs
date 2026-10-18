namespace MUD.Data
{
    // The one list of player fields the website owns (specs/WEB_EDITING.md sections 4.2 and 9, owner decisions in
    // section 15). The website writes exactly these (plus bookkeeping); the game's Player.Save never writes them, so a
    // stale live object cannot roll a web edit back. Both sides read this list, so they cannot drift.
    //
    // Password, NameKey, PlayerName, Inventory, WornItems, HeldItems, CurrentLocationId and SelectedTerm are NOT web-owned:
    // the website never touches them (CurrentLocationId stays game-written, owner decision Q12).
    public static class PlayerFields
    {
        public const string ShortDescription = "ShortDescription";
        public const string LongDescription = "LongDescription";
        public const string Race = "Race";
        public const string Gender = "Gender";
        public const string Class = "Class";
        public const string Retired = "Retired";

        // Roles are CreatorProperty / AdminProperty elements inside this array. Other elements (none today) belong to the game.
        public const string ExtendedProperties = "ExtendedProperties";

        public const string Rev = "Rev";
        public const string WebUpdatedAt = "WebUpdatedAt";
        public const string Id = "_id";

        // What an edit form may change directly (roles are handled separately, with array operators).
        public static readonly string[] WebEditable = { ShortDescription, LongDescription, Race, Gender, Class, Retired };

        // Fields the game must leave out of its field-level $set on Player.Save.
        public static readonly string[] NotWrittenByGame =
        {
            Id, Rev, WebUpdatedAt, ShortDescription, LongDescription, Race, Gender, Class, Retired, ExtendedProperties
        };
    }
}
