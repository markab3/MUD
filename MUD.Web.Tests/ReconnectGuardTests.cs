using System.Text.RegularExpressions;
using Xunit;

namespace MUD.Web.Tests;

// WEB_BLAZOR.md 6.2: while the circuit is reconnecting, Save, Delete and the other mutating buttons are disabled. One shared helper
// (window.mudReconnectGuard in app.js) disables every button marked class "mud-save" or attribute "data-mutating". There is no browser or
// bUnit here, so these guards pin the markers and the helper's wiring in source.
public class ReconnectGuardTests
{
    private static string Read(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MUD.sln"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static IEnumerable<string> Buttons(string src) =>
        Regex.Matches(src, "<button\\b[^>]*>[^<]*", RegexOptions.Singleline).Select(m => m.Value);

    private static bool Marked(string button) => button.Contains("mud-save") || button.Contains("data-mutating");

    // Buttons that only change local form state or the draft: not mutating, so they need no marker.
    private static readonly string[] LocalOnly =
    {
        "Restore unsaved changes", "Discard", "Cancel", "Move up", "Move down", "Remove", "Add feature", "Add exit", "Link Adjacent Rooms", "Refresh", "Retry", "Filter",
    };

    [Theory]
    [InlineData("Components/Pages/RoomEdit.razor")]
    [InlineData("Components/Pages/PlayerEdit.razor")]
    [InlineData("Components/Pages/TerrainEdit.razor")]
    [InlineData("Components/Pages/Modules.razor")]
    [InlineData("Components/Pages/Password.razor")]
    [InlineData("Components/Pages/Creator.razor")]
    [InlineData("Components/Pages/TerrainList.razor")]
    [InlineData("Components/Shared/ConfirmDialog.razor")]
    [InlineData("Components/Shared/PinnedSave.razor")]
    public void EveryMutatingButtonCarriesTheReconnectMarker(string file)
    {
        var buttons = Buttons(Read("MUD.Web/" + file)).ToList();
        Assert.NotEmpty(buttons);
        foreach (var b in buttons)
        {
            var local = LocalOnly.Any(t => b.Contains(">" + t) || b.EndsWith(t, StringComparison.Ordinal));
            Assert.True(local || Marked(b), file + ": " + b);
        }
    }

    [Fact]
    public void TheNamedButtonsOfTheSpecAreMarked()
    {
        var room = Read("MUD.Web/Components/Pages/RoomEdit.razor");
        foreach (var label in new[] { ">Delete this room", ">Move room", ">Update", ">Regen" })
            Assert.Contains(Buttons(room), b => b.EndsWith(label, StringComparison.Ordinal) && Marked(b));
        Assert.Contains("Text=\"Save player\"", Read("MUD.Web/Components/Pages/PlayerEdit.razor"));
        // The Save of every edit page is the shared pinned bar, whose button carries the marker.
        Assert.Contains(Buttons(Read("MUD.Web/Components/Shared/PinnedSave.razor")), b => b.EndsWith(">@Text") && Marked(b));
        Assert.Contains("<PinnedSave", Read("MUD.Web/Components/Pages/TerrainEdit.razor"));
        Assert.Contains(Buttons(Read("MUD.Web/Components/Pages/Password.razor")), b => b.EndsWith(">Change password") && Marked(b));
        var modules = Buttons(Read("MUD.Web/Components/Pages/Modules.razor"));
        foreach (var label in new[] { ">Stop", ">Start", ">Restart" })
            Assert.Contains(modules, b => b.EndsWith(label, StringComparison.Ordinal) && Marked(b));
        Assert.Contains(Buttons(Read("MUD.Web/Components/Pages/TerrainList.razor")), b => b.EndsWith(">Delete") && Marked(b));
    }

    [Fact]
    public void TheHelperIsOneSharedScript_NotPerPageCopies()
    {
        var js = Read("MUD.Web/wwwroot/js/app.js");
        Assert.Contains("window.mudReconnectGuard", js);
        Assert.Contains(".mud-save, [data-mutating]", js);
        Assert.Contains("components-reconnect-show", js);
        Assert.Contains("components-reconnect-failed", js);
        Assert.Contains("components-reconnect-rejected", js);
        Assert.Contains("data-reconnect-disabled", js); // only re-enables what it disabled itself
        Assert.Contains("app.js", Read("MUD.Web/Components/App.razor"));
        foreach (var f in new[] { "RoomEdit", "PlayerEdit", "TerrainEdit", "Modules", "Password" })
            Assert.DoesNotContain("mudReconnectGuard", Read("MUD.Web/Components/Pages/" + f + ".razor"));
    }

    [Fact]
    public void NoStaleNextPrefixInTheLogoutEndpointComment()
    {
        Assert.DoesNotContain("/next", Read("MUD.Web/Services/LogoutEndpoint.cs"));
    }
}
