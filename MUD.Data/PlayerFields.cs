namespace MUD.Data
{
    // The player fields an edit through the Admin service may change (specs/WEB_EDITING.md sections 4.2 and 15, specs/ADMIN_API.md).
    // Core is the only writer of the player document and edits are last write wins (D30), so no field is kept away from the
    // game's own save.
    //
    // Password, NameKey, PlayerName, Inventory, WornItems, HeldItems, CurrentLocationId and SelectedTerm are NOT editable
    // there (CurrentLocationId stays game-written, owner decision Q12).
    public static class PlayerFields
    {
        public const string ShortDescription = "ShortDescription";
        public const string LongDescription = "LongDescription";
        public const string Race = "Race";
        public const string Gender = "Gender";
        public const string Class = "Class";
        public const string Retired = "Retired";

        // The roles component (decisions D45, G30): an embedded document { creator, admin, currentDirectory }. The Admin
        // service changes creator and admin; currentDirectory belongs to the game.
        public const string Roles = "roles";
        public const string RolesCreator = "roles.creator";
        public const string RolesAdmin = "roles.admin";

        public const string WebUpdatedAt = "WebUpdatedAt";
        public const string Id = "_id";

        // What an edit form may change directly (roles are handled separately, with array operators).
        public static readonly string[] WebEditable = { ShortDescription, LongDescription, Race, Gender, Class, Retired };
    }
}
