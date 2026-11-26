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

    public const string Creator = Prefix + "/creator";
    public const string Map = Prefix + "/creator/map";

    public static string MapFor(string area, int? z) =>
        Map + "?area=" + Uri.EscapeDataString(area) + (z.HasValue ? "&z=" + z.Value : string.Empty);

    public static string RoomEdit(string key) => Prefix + "/creator/rooms/" + Uri.EscapeDataString(key);

    public const string Players = Prefix + "/creator/players";
    public const string Modules = Prefix + "/creator/modules";

    public static string PlayerEdit(string nameKey) => Players + "/" + Uri.EscapeDataString(nameKey);

    public const string Terrain = Prefix + "/creator/terrain";
    public const string TerrainEditPage = Terrain + "/edit";

    // kind and id travel in the query string only (an empty id means a new entry).
    public static string TerrainEdit(string kind, string? id = null) =>
        TerrainEditPage + "?kind=" + Uri.EscapeDataString(kind) + (string.IsNullOrEmpty(id) ? string.Empty : "&id=" + Uri.EscapeDataString(id));
}
