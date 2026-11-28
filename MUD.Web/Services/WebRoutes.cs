namespace MUD.Web.Services;

// Every internal link of the Blazor UI is built from here (WEB_BLAZOR.md 2.2). Until the cutover, while the Razor Pages owned the real URLs, the Blazor
// components lived under Prefix; the cutover emptied it (Prefix is now ""). The @page directives are literals (Razor needs that), so a route
// table test pins each one against these constants.
public static class WebRoutes
{
    public const string Prefix = "";

    public const string Home = "/";
    public const string Login = Prefix + "/login";
    public const string Logout = Prefix + "/logout";
    public const string AccessDenied = Prefix + "/access-denied";
    public const string Character = Prefix + "/character";
    public const string Password = Prefix + "/account/password";

    public const string Creator = Prefix + "/creator";
    public const string Map = Prefix + "/creator/map";

    // gx/gy: the X,Y a level switch started from (the map shows a dashed "No room here" square there if that level has no room at it).
    // center: a room key to centre the map on; created: the page then also says "Created <key>" (after Create room).
    public static string MapFor(string area, int? z, int? gx = null, int? gy = null, string? center = null, bool created = false) =>
        Map + "?area=" + Uri.EscapeDataString(area) + (z.HasValue ? "&z=" + z.Value : string.Empty)
        + (gx.HasValue && gy.HasValue ? "&gx=" + gx.Value + "&gy=" + gy.Value : string.Empty)
        + (string.IsNullOrEmpty(center) ? string.Empty : "&center=" + Uri.EscapeDataString(center))
        + (created ? "&created=1" : string.Empty);

    // Create room, prefilled: area and coordinates only, never an exit. Parts that are null are left out.
    public static string CreatorAt(string? area, int? x, int? y, int? z)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(area)) { parts.Add("area=" + Uri.EscapeDataString(area)); }
        if (x.HasValue) { parts.Add("x=" + x.Value); }
        if (y.HasValue) { parts.Add("y=" + y.Value); }
        if (z.HasValue) { parts.Add("z=" + z.Value); }
        return Creator + (parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts));
    }

    public static string RoomEdit(string key) => Prefix + "/creator/rooms/" + Uri.EscapeDataString(key);

    public const string Players = Prefix + "/creator/players";
    public const string Modules = Prefix + "/creator/modules";
    public const string Areas = Prefix + "/creator/areas";
    public const string ItemTemplates = Prefix + "/creator/items";
    public const string ItemTemplateEditPage = ItemTemplates + "/edit";

    // The edit page: no id = a new template; otherwise the template with that id (query string only).
    // With no id and an area, a new template opens with that area chosen.
    public static string ItemTemplateEdit(string? id = null, string? area = null) =>
        ItemTemplateEditPage + (!string.IsNullOrEmpty(id) ? "?id=" + Uri.EscapeDataString(id) : !string.IsNullOrEmpty(area) ? "?area=" + Uri.EscapeDataString(area) : string.Empty);

    public static string PlayerEdit(string nameKey) => Players + "/" + Uri.EscapeDataString(nameKey);

    public const string Terrain = Prefix + "/creator/terrain";
    public const string TerrainEditPage = Terrain + "/edit";

    // kind and id travel in the query string only (an empty id means a new entry). area and terrain pre-fill the form of a NEW entry
    // (the list page's choice); an existing entry's area and terrain come from the stored entry.
    public static string TerrainEdit(string kind, string? id = null, string? area = null, string? terrain = null) =>
        TerrainEditPage + "?kind=" + Uri.EscapeDataString(kind) + (string.IsNullOrEmpty(id) ? string.Empty : "&id=" + Uri.EscapeDataString(id))
        + (string.IsNullOrEmpty(area) ? string.Empty : "&area=" + Uri.EscapeDataString(area))
        + (string.IsNullOrEmpty(terrain) ? string.Empty : "&terrain=" + Uri.EscapeDataString(terrain));

    // The terrain list page for an area (and optionally one terrain type of it).
    public static string TerrainFor(string area, string? terrain = null) =>
        Terrain + "?area=" + Uri.EscapeDataString(area) + (string.IsNullOrEmpty(terrain) ? string.Empty : "&terrain=" + Uri.EscapeDataString(terrain));

    // The item template list limited to one area.
    public static string ItemTemplatesFor(string area) => ItemTemplates + "?area=" + Uri.EscapeDataString(area);
}
