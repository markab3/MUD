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
}
