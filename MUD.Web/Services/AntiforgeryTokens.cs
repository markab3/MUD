namespace MUD.Web.Services;

// The request token of the current page, handed from the static App shell to the interactive layout so the Log out form
// (a real browser POST, WEB_BLAZOR.md 2.1) can carry it. Server-side render only; it is the same token the antiforgery cookie validates.
public sealed record AntiforgeryTokens(string FormFieldName, string RequestToken);
