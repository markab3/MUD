using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MUD.Contracts;
using MUD.Web.Components.Shared;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

// Room key autocomplete (WEB_BLAZOR.md 4.4): matching and ranking, the combobox behaviour, the rendered ARIA markup,
// the exit rows that use it, and the per-circuit room cache.
public class RoomKeyCompleteTests
{
    private static RoomRecord R(string key, string shortText = "", string? area = null) =>
        new(key, area, shortText, null, null, null, Array.Empty<RoomExitRecord>());

    private static readonly RoomRecord[] World =
    {
        R("town.square", "The Town Square", "town"),
        R("town.hall", "A great hall", "town"),
        R("town.well", "By the old well", "town"),
        R("forest.clearing", "A sunny clearing near the town", "forest"),
        R("forest.hallway", "A leafy path", "forest"),
        R("cave.entrance", "A dark mouth", "cave"),
    };

    // ---- RoomKeyMatcher ----

    [Fact]
    public void Match_BlankQuery_ReturnsNothing()
    {
        Assert.Empty(RoomKeyMatcher.Match(World, null));
        Assert.Empty(RoomKeyMatcher.Match(World, ""));
        Assert.Empty(RoomKeyMatcher.Match(World, "   "));
    }

    [Fact]
    public void Match_NullRooms_OrZeroMax_ReturnsNothing()
    {
        Assert.Empty(RoomKeyMatcher.Match(null, "town"));
        Assert.Empty(RoomKeyMatcher.Match(World, "town", 0));
    }

    [Fact]
    public void Match_OneCharacter_Matches()
    {
        Assert.NotEmpty(RoomKeyMatcher.Match(World, "t"));
    }

    [Fact]
    public void Match_IsCaseInsensitive_OnKeyAndShort()
    {
        Assert.Equal("town.square", RoomKeyMatcher.Match(World, "SQUARE").First().Key);
        Assert.Contains(RoomKeyMatcher.Match(World, "OLD WELL"), r => r.Key == "town.well");
    }

    [Fact]
    public void Match_AnyPartOfTheKey_Matches()
    {
        Assert.Equal(new[] { "town.hall", "forest.hallway" }, RoomKeyMatcher.Match(World, "hall").Select(r => r.Key));
    }

    [Fact]
    public void Match_ShortDescriptionOnly_Matches()
    {
        Assert.Equal(new[] { "cave.entrance" }, RoomKeyMatcher.Match(World, "dark").Select(r => r.Key));
    }

    [Fact]
    public void Match_RanksKeyPrefix_ThenKeyContains_ThenShortOnly()
    {
        // "town": key prefix (town.*), none contain it mid-key, forest.clearing only by its short description.
        var keys = RoomKeyMatcher.Match(World, "town").Select(r => r.Key).ToList();
        Assert.Equal(new[] { "town.hall", "town.well", "town.square", "forest.clearing" }, keys);
        // "hall": town.hall is contained mid-key as well; both contain, shorter key first.
        var rooms = new[] { R("a.xhall"), R("hallway.b"), R("zz", "the hall of zz") };
        Assert.Equal(new[] { "hallway.b", "a.xhall", "zz" }, RoomKeyMatcher.Match(rooms, "hall").Select(r => r.Key));
    }

    [Fact]
    public void Match_TiesBreakByKeyLength_ThenOrdinal()
    {
        var rooms = new[] { R("b.bb"), R("b.aa"), R("b.a") };
        Assert.Equal(new[] { "b.a", "b.aa", "b.bb" }, RoomKeyMatcher.Match(rooms, "b.").Select(r => r.Key));
    }

    [Fact]
    public void Match_AtMostMax()
    {
        var many = Enumerable.Range(0, 30).Select(i => R("a.r" + i.ToString("00"))).ToList();
        Assert.Equal(RoomKeyMatcher.MaxResults, RoomKeyMatcher.Match(many, "a.").Count);
        Assert.Equal(3, RoomKeyMatcher.Match(many, "a.", 3).Count);
    }

    [Fact]
    public void Match_SkipsNullAndKeylessRooms()
    {
        var rooms = new RoomRecord?[] { null, R(""), R("a.b") }!;
        Assert.Equal(new[] { "a.b" }, RoomKeyMatcher.Match(rooms, "a").Select(r => r.Key));
    }

    [Fact]
    public void Match_NullShort_DoesNotThrow()
    {
        var room = new RoomRecord("a.b", null, null!, null, null, null, Array.Empty<RoomExitRecord>());
        Assert.Empty(RoomKeyMatcher.Match(new[] { room }, "zzz"));
    }

    [Fact]
    public void Match_WithScope_OnlyThatAreaAndMatchesTheLocalPart()
    {
        // The create form types only the part after "town."; "town" must not match every town room by the prefix.
        Assert.Equal(new[] { "town.hall" }, RoomKeyMatcher.Match(World, "ha", 8, "town.").Select(r => r.Key));
        Assert.Empty(RoomKeyMatcher.Match(World, "town", 8, "cave."));
        Assert.Empty(RoomKeyMatcher.Match(World, "clearing", 8, "town."));
    }

    [Fact]
    public void Exists_ExactOrdinalAfterTrim()
    {
        Assert.True(RoomKeyMatcher.Exists(World, " town.hall "));
        Assert.False(RoomKeyMatcher.Exists(World, "Town.Hall"));
        Assert.False(RoomKeyMatcher.Exists(World, "town"));
        Assert.False(RoomKeyMatcher.Exists(World, ""));
        Assert.False(RoomKeyMatcher.Exists(World, null));
        Assert.False(RoomKeyMatcher.Exists(null, "town.hall"));
    }

    [Fact]
    public void AreaOf_UsesAreaThenKeyPrefix()
    {
        Assert.Equal("town", RoomKeyMatcher.AreaOf(R("x.y", "", "town")));
        Assert.Equal("a.b", RoomKeyMatcher.AreaOf(R("a.b.c")));
        Assert.Equal(string.Empty, RoomKeyMatcher.AreaOf(R("nodot")));
    }

    // ---- RoomKeyCombo ----

    private static RoomKeyCombo Existing()
    {
        var c = new RoomKeyCombo(RoomKeyMode.Existing) { Rooms = World };
        c.Focus();
        return c;
    }

    [Fact]
    public void Combo_EmptyInput_ShowsNothing_UntilOneCharacter()
    {
        var c = Existing();
        Assert.False(c.ShowList);
        c.Type("");
        Assert.False(c.ShowList);
        c.Type("t");
        Assert.True(c.ShowList);
    }

    [Fact]
    public void Combo_Announcement_CountsMatches()
    {
        var c = Existing();
        Assert.Equal(string.Empty, c.Announcement);
        c.Type("cave");
        Assert.Equal("1 matching room", c.Announcement);
        c.Type("town");
        Assert.Equal("4 matching rooms", c.Announcement);
        c.Type("zzz");
        Assert.Equal("No matching rooms", c.Announcement);
        Assert.False(c.ShowList);
    }

    [Fact]
    public void Combo_ArrowKeys_MoveAndWrap()
    {
        var c = Existing();
        c.Type("town");
        Assert.Equal(-1, c.Active);
        c.KeyDown("ArrowDown"); Assert.Equal(0, c.Active);
        c.KeyDown("ArrowDown"); Assert.Equal(1, c.Active);
        c.KeyDown("ArrowUp"); Assert.Equal(0, c.Active);
        c.KeyDown("ArrowUp"); Assert.Equal(c.Results.Count - 1, c.Active);
        c.KeyDown("ArrowDown"); Assert.Equal(0, c.Active);
    }

    [Fact]
    public void Combo_ArrowDown_ReopensAClosedList()
    {
        var c = Existing();
        c.Type("town");
        c.KeyDown("Escape");
        Assert.False(c.ShowList);
        c.KeyDown("ArrowDown");
        Assert.True(c.ShowList);
        Assert.Equal(0, c.Active);
    }

    [Fact]
    public void Combo_ArrowDown_WithEmptyText_DoesNothing()
    {
        var c = Existing();
        c.KeyDown("ArrowDown");
        Assert.False(c.ShowList);
        Assert.Equal(-1, c.Active);
    }

    [Fact]
    public void Combo_Enter_WithHighlightedRow_ChoosesIt()
    {
        var c = Existing();
        c.Type("town");
        c.KeyDown("ArrowDown");
        var press = c.KeyDown("Enter");
        Assert.Equal("town.hall", press.Chosen);
        Assert.Equal("town.hall", c.Text);
        Assert.False(c.ShowList);
        Assert.Null(c.Problem);
    }

    [Fact]
    public void Combo_Enter_WithoutChoosing_KeepsTypedKey()
    {
        var c = Existing();
        c.Type("town.well");
        var press = c.KeyDown("Enter");
        Assert.Null(press.Chosen);
        Assert.Equal("town.well", c.Text);
        Assert.False(c.ShowList);
        Assert.Null(c.Problem);
    }

    [Fact]
    public void Combo_Enter_WithoutChoosing_UnknownKey_ShowsMessageEvenWhileFocused()
    {
        var c = Existing();
        c.Type("town.nope");
        Assert.Null(c.Problem);               // still typing
        c.KeyDown("Enter");
        Assert.Equal("No room has this key.", c.Problem);
        c.Type("town.nope2");                 // typing again clears the early check
        Assert.Null(c.Problem);
    }

    [Fact]
    public void Combo_Escape_Closes_AndKeepsText()
    {
        var c = Existing();
        c.Type("town");
        c.KeyDown("ArrowDown");
        c.KeyDown("Escape");
        Assert.False(c.ShowList);
        Assert.Equal(-1, c.Active);
        Assert.Equal("town", c.Text);
    }

    [Fact]
    public void Combo_OtherKeys_AreIgnored()
    {
        var c = Existing();
        c.Type("town");
        Assert.Null(c.KeyDown("a").Chosen);
        Assert.True(c.ShowList);
    }

    [Fact]
    public void Combo_Choose_ByTap_SetsTheKey_AndOutOfRangeIsIgnored()
    {
        var c = Existing();
        c.Type("cave");
        Assert.Null(c.Choose(5));
        Assert.Null(c.Choose(-1));
        Assert.Equal("cave.entrance", c.Choose(0));
        Assert.Equal("cave.entrance", c.Text);
    }

    [Fact]
    public void Combo_UnknownKey_MessageOnBlur_NotWhileFocused_NotWhenEmpty_NotWhenRoomsUnknown()
    {
        var c = Existing();
        c.Type("town.nope");
        Assert.Null(c.Problem);
        c.Blur();
        Assert.Equal("No room has this key.", c.Problem);
        c.SetFromParent("");
        Assert.Null(c.Problem);
        var unknown = new RoomKeyCombo(RoomKeyMode.Existing);
        unknown.Type("town.nope");
        unknown.Blur();
        Assert.Null(unknown.Problem);
    }

    [Fact]
    public void Combo_ExistingKeyOnBlur_HasNoMessage()
    {
        var c = Existing();
        c.Type("town.hall");
        c.Blur();
        Assert.Null(c.Problem);
    }

    [Fact]
    public void Combo_SetFromParent_ShowsValueWithoutOpening_AndSameValueIsIgnored()
    {
        var c = Existing();
        c.SetFromParent("town.hall");
        Assert.Equal("town.hall", c.Text);
        Assert.False(c.ShowList);
        c.Type("town");
        c.SetFromParent("town");
        Assert.True(c.ShowList);
        c.SetFromParent(null);
        Assert.Equal(string.Empty, c.Text);
    }

    [Fact]
    public void Combo_RoomsArriveWhileOpen_RefreshesTheList()
    {
        var c = new RoomKeyCombo(RoomKeyMode.Existing);
        c.Type("town");
        Assert.False(c.ShowList);
        c.Rooms = World;
        Assert.True(c.ShowList);
    }

    [Fact]
    public void Combo_ActiveBeyondShrunkResults_IsReset()
    {
        var c = Existing();
        c.Type("town");
        c.KeyDown("ArrowUp");
        Assert.Equal(c.Results.Count - 1, c.Active);
        c.Type("cave");
        Assert.Equal(-1, c.Active);
    }

    [Fact]
    public void Combo_NewMode_SuggestsRoomsInTheArea_NeverChooses_WarnsOnDuplicate()
    {
        var c = new RoomKeyCombo(RoomKeyMode.New, "town.") { Rooms = World };
        c.Type("ha");
        Assert.Equal(new[] { "town.hall" }, c.Results.Select(r => r.Key));
        Assert.Null(c.Choose(0));
        Assert.Equal("ha", c.Text);
        c.KeyDown("ArrowDown");
        Assert.Equal(-1, c.Active);
        c.KeyDown("ArrowUp");
        Assert.Equal(-1, c.Active);
        Assert.Null(c.Problem);
        c.Type("hall");
        Assert.True(c.IsDuplicate);
        Assert.Contains("already exists", c.Problem);
        c.Type("newroom");
        Assert.False(c.IsDuplicate);
        Assert.Null(c.Problem);
        c.Type("");
        Assert.False(c.IsDuplicate);
    }

    [Fact]
    public void Combo_NewMode_WithoutScope_MatchesAllRooms()
    {
        var c = new RoomKeyCombo(RoomKeyMode.New) { Rooms = World };
        c.Type("cave");
        Assert.Single(c.Results);
    }

    // ---- RoomDirectory ----

    [Fact]
    public async Task Directory_LoadsOncePerCircuit_UntilInvalidated()
    {
        var core = new FakeAdminClient().AddRoom("t.a").AddRoom("t.b");
        var dir = new RoomDirectory(core);
        var first = await dir.GetAsync("tok");
        core.AddRoom("t.c");
        var second = await dir.GetAsync("tok");
        Assert.Same(first, second);
        Assert.Equal(2, second!.Count);
        dir.Invalidate();
        Assert.Equal(3, (await dir.GetAsync("tok"))!.Count);
        Assert.Equal(2, core.RoomTokens.Count);
    }

    [Fact]
    public async Task Directory_CoreFailure_ReturnsNull_AndIsNotCached()
    {
        var core = new FakeAdminClient().AddRoom("t.a");
        core.Unavailable = true;
        var dir = new RoomDirectory(core);
        Assert.Null(await dir.GetAsync("tok"));
        core.Unavailable = false;
        Assert.Single((await dir.GetAsync("tok"))!);
    }

    // ---- rendered markup ----

    private sealed class Host : ComponentBase
    {
        [Parameter] public RenderFragment? Content { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder b)
        {
            var identity = new ClaimsIdentity(new[] { new Claim(SessionAuthentication.TokenClaim, "tok") }, "test");
            var state = Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
            b.OpenComponent<CascadingValue<Task<AuthenticationState>>>(0);
            b.AddAttribute(1, "Value", state);
            b.AddAttribute(2, "ChildContent", Content);
            b.CloseComponent();
        }
    }

    private static async Task<string> RenderAsync(FakeAdminClient core, RenderFragment content)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAdminClient>(core);
        services.AddScoped<RoomDirectory>();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new Microsoft.AspNetCore.Components.Web.HtmlRenderer(provider, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Content"] = content }));
            return root.ToHtmlString();
        });
    }

    private static FakeAdminClient Core() => new FakeAdminClient().AddRoom("t.start").AddRoom("t.hall", "t", 1, 1, 0, ("north", "t.start"));

    [Fact]
    public async Task Render_ClosedCombobox_HasTheAriaContract()
    {
        var html = await RenderAsync(Core(), b =>
        {
            b.OpenComponent<RoomKeyInput>(0);
            b.AddAttribute(1, "Id", "k");
            b.AddAttribute(2, "Value", "t.start");
            b.CloseComponent();
        });
        Assert.Contains("role=\"combobox\"", html);
        Assert.Contains("aria-expanded=\"false\"", html);
        Assert.Contains("aria-controls=\"k-list\"", html);
        Assert.Contains("aria-autocomplete=\"list\"", html);
        Assert.Contains("aria-haspopup=\"listbox\"", html);
        Assert.Contains("autocomplete=\"off\"", html);
        Assert.Contains("role=\"status\" aria-live=\"polite\"", html);
        Assert.DoesNotContain("role=\"listbox\"", html);
        Assert.DoesNotContain("<datalist", html);
        Assert.DoesNotContain("No room has this key", html);   // known key, and never before the user touched it
    }

    [Fact]
    public async Task Render_ExitRows_UseTheSharedInput_ForEveryTarget()
    {
        var exits = new List<ExitInput> { new() { Dir = "north", To = "t.start" }, new() };
        var html = await RenderAsync(Core(), b =>
        {
            b.OpenComponent<ExitRows>(0);
            b.AddAttribute(1, "Id", "x");
            b.AddAttribute(2, "Exits", exits);
            b.CloseComponent();
        });
        Assert.Equal(2, Regex.Matches(html, "role=\"combobox\"").Count);
        Assert.Contains("id=\"x-to-0\"", html);
        Assert.Contains("id=\"x-to-1\"", html);
        Assert.Contains("value=\"t.start\"", html);
        Assert.Contains("aria-label=\"Exit target room key\"", html);
        Assert.Contains("<label for=\"x-to-0\">Target room key</label>", html);
    }

    [Fact]
    public async Task Render_ExitRow_WithUnknownTarget_ShowsNoMessageUntilTheUserLeavesTheField()
    {
        // A stored dangling exit is not nagged on load: the message comes from blur, Enter or save, not from rendering.
        var exits = new List<ExitInput> { new() { Dir = "north", To = "t.gone" } };
        var html = await RenderAsync(Core(), b =>
        {
            b.OpenComponent<ExitRows>(0);
            b.AddAttribute(1, "Exits", exits);
            b.CloseComponent();
        });
        Assert.Contains("value=\"t.gone\"", html);
    }

    [Fact]
    public async Task Render_NewMode_ShowsThePrefix_BesideTheInput()
    {
        var html = await RenderAsync(Core(), b =>
        {
            b.OpenComponent<RoomKeyInput>(0);
            b.AddAttribute(1, "Id", "cr-key");
            b.AddAttribute(2, "Mode", RoomKeyMode.New);
            b.AddAttribute(3, "Prefix", "t.");
            b.AddAttribute(4, "Value", "hall");
            b.CloseComponent();
        });
        Assert.Contains("id=\"cr-key-prefix\"", html);
        Assert.Contains(">t.</span>", html);
        Assert.Contains("aria-describedby=\"cr-key-prefix cr-key-problem\"", html);
        Assert.Contains("A room with this key already exists.", html);   // t.hall exists
    }

    [Fact]
    public async Task Render_NewMode_FreeKey_HasNoProblem()
    {
        var html = await RenderAsync(Core(), b =>
        {
            b.OpenComponent<RoomKeyInput>(0);
            b.AddAttribute(1, "Id", "cr-key");
            b.AddAttribute(2, "Mode", RoomKeyMode.New);
            b.AddAttribute(3, "Prefix", "t.");
            b.AddAttribute(4, "Value", "brandnew");
            b.CloseComponent();
        });
        Assert.DoesNotContain("already exists", html);
        Assert.DoesNotContain("role=\"alert\"", html);
    }

    // ---- where the input is used / static guards ----

    private static string Source(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "MUD.Web"))) { dir = Path.GetDirectoryName(dir); }
        return File.ReadAllText(Path.Combine(new[] { dir!, "MUD.Web" }.Concat(parts).ToArray()));
    }

    [Fact]
    public void Source_CreateKey_UsesTheSharedInput_InNewMode()
    {
        var creator = Source("Components", "Pages", "Creator.razor");
        Assert.Contains("<RoomKeyInput Id=\"cr-key\" Mode=\"RoomKeyMode.New\"", creator);
        Assert.DoesNotContain("<input id=\"cr-key\"", creator);
    }

    [Fact]
    public void Source_ExitTargetInput_IsNotAPlainInputAnymore()
    {
        var rows = Source("Components", "Shared", "ExitRows.razor");
        Assert.Contains("<RoomKeyInput", rows);
        Assert.DoesNotContain("-to-@index\" value", rows);
    }

    [Fact]
    public void Source_MoveRoomLocalKey_StaysAPlainInput_WithTheAreaPrefixBesideIt()
    {
        var edit = Source("Components", "Pages", "RoomEdit.razor");
        Assert.Contains("<input id=\"re-newlocal\"", edit);
        Assert.Contains("id=\"re-newprefix\"", edit);
        Assert.DoesNotContain("RoomKeyInput Id=\"re-newlocal\"", edit);
    }

    [Fact]
    public void Source_PagesRefreshTheRoomListWhenTheyLoad()
    {
        Assert.Contains("RoomsDirectory.Invalidate()", Source("Components", "Pages", "Creator.razor"));
        Assert.Contains("RoomsDirectory.Invalidate()", Source("Components", "Pages", "RoomEdit.razor"));
    }

    [Fact]
    public void Source_Css_UsesPaletteVariablesOnly_And44pxRows()
    {
        var css = Source("wwwroot", "css", "mobile.css");
        var block = css.Substring(css.IndexOf("/* Room key autocomplete", StringComparison.Ordinal));
        Assert.DoesNotMatch(new Regex("#[0-9a-fA-F]{3,8}\\b"), block);
        Assert.Contains("min-height: var(--mud-hit)", block);
    }
}
