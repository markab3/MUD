using MUD.Web.Services;

namespace MUD.Web.Pages;

public sealed record NavItem(string Label, string Href, bool ExactMatch = false);

// Id names the heading element (mud-nav-<Id>) that the section's list is labelled by.
public sealed record NavSection(string Id, string Heading, IReadOnlyList<NavItem> Items);

// The pull-out menu: which links it holds and whether it is open (WEB_BLAZOR.md 5.2). Free of the renderer so the rules are testable.
// Log out is not an item: it is a POST form with an antiforgery token, rendered by the shell after the links.
public sealed class NavMenu
{
    // Three sections in this order: Player (every signed-in user), Creator (CreatorOrAdmin), Admin (admins only).
    // A section with no visible items is left out, so the menu never shows an empty heading.
    // The Players link is under Admin only, and the player pages are authorised for AdminOnly.
    public static IReadOnlyList<NavSection> SectionsFor(bool creatorOrAdmin, bool admin)
    {
        var sections = new List<NavSection>
        {
            new("player", "Player", new[]
            {
                new NavItem("Character", WebRoutes.Character),
                new NavItem("Change password", WebRoutes.Password),
            }),
        };
        if (creatorOrAdmin)
        {
            sections.Add(new("creator", "Creator", new[]
            {
                new NavItem("Map", WebRoutes.Map),
                new NavItem("Modules", WebRoutes.Modules),
                new NavItem("Terrain", WebRoutes.Terrain),
                new NavItem("Areas", WebRoutes.Areas),
                new NavItem("Items", WebRoutes.ItemTemplates),
                new NavItem("Create room", WebRoutes.Creator, ExactMatch: true),
            }));
        }
        if (admin)
        {
            sections.Add(new("admin", "Admin", new[] { new NavItem("Players", WebRoutes.Players) }));
        }
        return sections.Where(s => s.Items.Count > 0).ToList();
    }

    public bool IsOpen { get; private set; }

    // True when the state changed (the shell re-renders and moves focus only then).
    public bool Open() { var changed = !IsOpen; IsOpen = true; return changed; }

    public bool Close() { var changed = IsOpen; IsOpen = false; return changed; }

    public void Toggle() => IsOpen = !IsOpen;
}
