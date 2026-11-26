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

    // Draft restore (WEB_BLAZOR.md 6.4).
    public const string DraftFound = "Unsaved changes from an earlier visit were found on this device. The room may have changed in the meantime; saving them overwrites it (last write wins).";
}
