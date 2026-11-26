using MUD.Web.Services;

namespace MUD.Web.Pages;

public sealed record NavItem(string Label, string Href, bool ExactMatch = false);

// The pull-out menu: which links it holds and whether it is open (WEB_BLAZOR.md 5.2). Free of the renderer so the rules are testable.
// Log out is not an item: it is a POST form with an antiforgery token, rendered by the shell after the links.
public sealed class NavMenu
{
    // Creators and admins: the editing links first, then what every signed-in player has. Same items and visibility as before the top bar.
    public static IReadOnlyList<NavItem> ItemsFor(bool creatorOrAdmin)
    {
        var items = new List<NavItem>();
        if (creatorOrAdmin)
        {
            items.Add(new NavItem("Map", WebRoutes.Map));
            items.Add(new NavItem("Creator", WebRoutes.Creator, ExactMatch: true));
            items.Add(new NavItem("Players", WebRoutes.Players));
            items.Add(new NavItem("Modules", WebRoutes.Modules));
            items.Add(new NavItem("Terrain", WebRoutes.Terrain));
        }
        items.Add(new NavItem("Character", WebRoutes.Character));
        items.Add(new NavItem("Change password", WebRoutes.Password));
        return items;
    }

    public bool IsOpen { get; private set; }

    // True when the state changed (the shell re-renders and moves focus only then).
    public bool Open() { var changed = !IsOpen; IsOpen = true; return changed; }

    public bool Close() { var changed = IsOpen; IsOpen = false; return changed; }

    public void Toggle() => IsOpen = !IsOpen;
}
