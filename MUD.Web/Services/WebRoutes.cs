namespace MUD.Web.Services;

// Every internal link of the Blazor UI is built from here (WEB_BLAZOR.md 2.2). While the Razor Pages own the real URLs the Blazor
// components live under Prefix; the cutover card empties it. The @page directives are literals (Razor needs that), so a route
// table test pins each one against these constants.
public static class WebRoutes
{
    public const string Prefix = "/next";

    public const string Home = Prefix;
    public const string Login = Prefix + "/login";
    public const string Logout = Prefix + "/logout";
    public const string AccessDenied = Prefix + "/access-denied";
    public const string Character = Prefix + "/character";
    public const string Password = Prefix + "/account/password";

    // Pages that are still Razor Pages (later cards port them). The shell links to the real URLs until then.
    public const string LegacyCreator = "/creator";
    public const string LegacyMap = "/creator/map";
    public const string LegacyPlayers = "/creator/players";
    public const string LegacyModules = "/creator/modules";
    public const string LegacyTerrain = "/creator/terrain";
}
