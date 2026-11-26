namespace MUD.Web.Services;

// User-visible sentences of the Blazor UI, moved verbatim from the Razor page models (WEB_BLAZOR.md 4.1). A pin test compares
// them with the page-model constants while both UIs exist.
public static class Messages
{
    public const string LoginGeneric = "Invalid name or password.";
    public const string LoginRetired = "This character has been retired and can no longer be played.";
    public const string Unavailable = "The game is not reachable right now. Please try again later.";

    public const string PasswordWrongCurrent = "Your password could not be changed. Check your current password and try again.";
    public const string PasswordMismatch = "The new passwords did not match.";
    public const string PasswordSame = "The new password must be different from the current one.";
    public const string PasswordSaveFailed = "Your password could not be saved. Please try again later.";
    public const string PasswordChanged = "Your password has been changed.";

    public const string AccessDeniedTitle = "Access denied";
    public const string AccessDeniedText = "Your character does not have permission to view this page.";

    public const string NeedsJavaScript = "This site needs JavaScript. Please enable it and reload the page.";
    public const string Reconnecting = "Connection lost, reconnecting...";
    public const string ReconnectFailed = "Could not reconnect.";
    public const string CircuitLost = "This page's session was lost. Reload to continue.";
    public const string UnexpectedError = "Something went wrong. Reload the page.";

    // Room pages (creator, room edit, map): moved verbatim from CreatorModel / RoomEditModel.
    public const string RoomKeyExists = "A room with that key already exists.";
    public const string RoomCreateFailed = "The room could not be created. Please try again later.";
    public const string RoomCreateForbidden = "You are not allowed to create rooms.";
    public const string RoomNotFound = "That room no longer exists.";
    public const string RoomSaveFailed = "The room could not be saved. Please try again later.";
    public const string RoomAuditFailed = "The change could not be recorded, so nothing was saved. Please try again later.";
    public const string RoomForbidden = "You are not allowed to edit rooms.";
    public const string RoomSaved = "Saved. The running game has the change now.";
    public const string RoomNoChange = "Nothing was changed.";
    public const string RoomMoved = "Room moved.";
    public const string RoomDeleted = "Room deleted. Anything that was inside has been moved.";
    public const string RegenWarning = "Regen replaces the description of this room, ALL features (including ones you added by hand) and properties with new generator output. Your hand edits to them are lost. Exits and occupants stay. Continue?";
    public const string RoomNotGenerated = "This room was not made by a generator, so it cannot be regenerated or updated.";
    public const string LibraryUnavailable = "The terrain library is not available right now. Nothing was changed.";
    public const string RegenFailed = "The room could not be regenerated. Please try again later.";
    public const string UpdateFailed = "The room could not be updated. Please try again later.";

    // Player pages (list, edit): moved verbatim from PlayerListModel / PlayerEditModel.
    public const string PlayerListFailed = "The player list could not be loaded. Please try again later.";
    public const string PlayerListForbidden = "You are not allowed to list players.";
    public const string PlayerNotFound = "That player does not exist.";
    public const string PlayerSaveFailed = "The player could not be saved. Please try again later.";
    public const string PlayerAuditFailed = "The change could not be recorded, so nothing was saved. Please try again later.";
    public const string PlayerForbidden = "You are not allowed to make that change to this player.";
    public const string PlayerLastAdmin = "The last Admin cannot lose the Admin role.";
    public const string PlayerSaved = "Saved. The running game has the change now.";
    public const string PlayerNoChange = "Nothing was changed.";
    public const string PlayerCannotEdit = "You cannot edit this player: you may only edit players whose role is lower than yours.";
    // New in the Blazor UI (WEB_BLAZOR.md 6.4 draft restore, 4.1 ConfirmDialog for destructive actions).
    public const string PlayerDraftFound = "Unsaved changes from an earlier visit were found on this device. The player may have changed in the meantime; saving them overwrites it (last write wins).";
    public const string RetirePlayerConfirm = "Retire this character? It can no longer log in until an Admin un-retires it.";

    // Modules page: moved verbatim from ModulesModel.
    public const string ModuleForbidden = "You are not allowed to do that.";
    public const string ModuleFailed = "The module request could not be completed. Please try again later.";
    public const string ModuleListFailed = "The module list could not be loaded. Please try again later.";
    public const string ModuleRestartHint = "Restart starts a new instance before it stops the old one.";
    public const string ModuleNothingChanged = "Nothing was changed.";
    public const string ModuleNotFound = "That module does not exist.";
    public const string ModuleCoreRefused = "Core refused the request.";
    // New: confirm dialog texts for the destructive module actions.
    public const string StopModuleConfirm = "Stop this module? Anything that depends on it stops working until it is started again.";
    public const string RestartModuleConfirm = "Restart this module? It is replaced by a new instance.";

    // Terrain library pages. The error sentences stay in TerrainLibrary (shared with the Razor pages until the cutover card).
    // New in the Blazor UI: confirm text for deleting an entry, and the draft restore text of the terrain editor.
    public const string TerrainDeleteConfirm = "Delete this library entry? Generators stop using it at once.";
    public const string TerrainDraftFound = "Unsaved changes from an earlier visit were found on this device. The entry may have changed in the meantime; saving them overwrites it (last write wins).";

    // Draft restore (WEB_BLAZOR.md 6.4).
    public const string DraftFound = "Unsaved changes from an earlier visit were found on this device. The room may have changed in the meantime; saving them overwrites it (last write wins).";
}
