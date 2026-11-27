using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MUD.Contracts;
using MUD.Web.Components.Pages;
using MUD.Web.Pages;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

// "Link Adjacent Rooms" on the room edit page: the pure rule, and the page behaviour (the click only changes the form; Save sends it).
public class LinkAdjacentTests
{
    private static RoomRecord R(string key, int? x, int? y, int? z = 0, string? area = "t") =>
        new(key, area, "short " + key, x, y, z, Array.Empty<RoomExitRecord>());

    private static AdjacentPlan Plan(RoomRecord[] rooms, string[]? have = null, int x = 5, int y = 5, int? z = 0, string? area = "t") =>
        AdjacentRoomLinker.Plan("t.me", area, x, y, z, rooms, have ?? Array.Empty<string>());

    private static string[] Added(AdjacentPlan p) => p.Add.Select(e => e.Name + ">" + e.To).ToArray();

    // ---- the pure rule ----

    [Fact]
    public void Plan_AllEightCompassNeighbours_NorthIsPlusY_EastIsPlusX()
    {
        var rooms = new[]
        {
            R("t.n", 5, 6), R("t.ne", 6, 6), R("t.e", 6, 5), R("t.se", 6, 4),
            R("t.s", 5, 4), R("t.sw", 4, 4), R("t.w", 4, 5), R("t.nw", 4, 6),
        };
        Assert.Equal(new[] { "north>t.n", "northeast>t.ne", "east>t.e", "southeast>t.se", "south>t.s", "southwest>t.sw", "west>t.w", "northwest>t.nw" }, Added(Plan(rooms)));
    }

    [Fact]
    public void Plan_UpAndDown_AreIgnored_EvenWithRoomsAboveAndBelow()
    {
        var rooms = new[] { R("t.above", 5, 5, 1), R("t.below", 5, 5, -1), R("t.diagup", 6, 5, 1), R("t.e", 6, 5, 0) };
        var plan = Plan(rooms);
        Assert.Equal(new[] { "east>t.e" }, Added(plan));
        Assert.DoesNotContain(plan.Add, e => e.Name is "up" or "down");
    }

    [Fact]
    public void Plan_OnlyTheSameZ_AndNullZIsZero()
    {
        var rooms = new[] { R("t.nullz", 5, 6, null), R("t.other", 6, 5, 2) };
        Assert.Equal(new[] { "north>t.nullz" }, Added(Plan(rooms, z: 0)));
        Assert.Equal(new[] { "north>t.nullz" }, Added(Plan(rooms, z: null)));
        Assert.Equal(new[] { "east>t.other" }, Added(Plan(rooms, z: 2)));
    }

    [Fact]
    public void Plan_SkipsDirectionsAlreadyInTheForm_IncludingAbbreviationsAndCase_AndNeverOverwrites()
    {
        var rooms = new[] { R("t.n", 5, 6), R("t.e", 6, 5), R("t.s", 5, 4) };
        Assert.Equal(new[] { "south>t.s" }, Added(Plan(rooms, new[] { "north", " E " })));
        Assert.Equal(new[] { "east>t.e", "south>t.s" }, Added(Plan(rooms, new[] { "N", null!, "" })));
        // A direction that exists but points somewhere else is still "present".
        Assert.Empty(Plan(rooms, new[] { "north", "east", "south" }).Add);
    }

    [Fact]
    public void Plan_OtherAreas_Unplaced_AndFarRooms_AreIgnored_AndSelfToo()
    {
        var rooms = new[]
        {
            R("u.n", 5, 6, 0, "u"), R("t.unplaced", null, null), R("t.halfplaced", 5, null), R("t.far", 7, 5), R("t.me", 6, 5),
            R("t.noarea", 5, 4, 0, null),
        };
        Assert.Empty(Plan(rooms).Add);
    }

    [Fact]
    public void Plan_NullAreaMatchesNullArea()
    {
        Assert.Equal(new[] { "south>t.noarea" }, Added(Plan(new[] { R("t.noarea", 5, 4, 0, null) }, area: null)));
    }

    [Fact]
    public void Plan_DuplicateCoordinates_AddNothingForThatDirection_AndNameTheRooms()
    {
        var rooms = new[] { R("t.b", 5, 6), R("t.a", 5, 6), R("t.e", 6, 5) };
        var plan = Plan(rooms);
        Assert.Equal(new[] { "east>t.e" }, Added(plan));
        var c = Assert.Single(plan.Conflicts);
        Assert.Equal("north", c.Direction);
        Assert.Equal(new[] { "t.a", "t.b" }, c.Keys);
    }

    [Fact]
    public void Plan_UnplacedRoom_HasNoNeighbours()
    {
        var plan = AdjacentRoomLinker.Plan("t.me", "t", null, 5, 0, new[] { R("t.n", 5, 6) }, Array.Empty<string>());
        Assert.Empty(plan.Add);
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void Plan_NeverTouchesItsInputs()
    {
        var rooms = new[] { new RoomRecord("t.n", "t", "s", 5, 6, 0, new[] { new RoomExitRecord("south", "t.elsewhere") }) };
        Plan(rooms);
        Assert.Equal(new[] { new RoomExitRecord("south", "t.elsewhere") }, rooms[0].Exits);
    }

    // ---- presenter: only the form changes ----

    [Fact]
    public async Task Presenter_LinkAdjacent_AddsToTheFormOnly_AndNeverCallsCore()
    {
        var core = new FakeAdminClient().AddRoom("t.me", "t", 5, 5, 0).AddRoom("t.n", "t", 5, 6, 0);
        var presenter = new RoomEditPresenter(core, "token-1", "t.me");
        await presenter.LoadAsync();
        var calls = core.RoomTokens.Count;
        var result = presenter.LinkAdjacent(core.Rooms.Values.Select(RoomRecord.From).ToList());
        Assert.Equal(calls, core.RoomTokens.Count);
        Assert.Empty(core.RoomUpdates);
        Assert.Equal("Added 1 exit to the form: north -> t.n. Save to apply.", result.Status);
        Assert.Equal(("north", "t.n"), (presenter.Exits.Single().Dir, presenter.Exits.Single().To));
        Assert.Empty(core.Rooms["t.n"].Exits);
    }

    [Fact]
    public async Task Presenter_LinkAdjacent_NothingToAdd_SaysSo()
    {
        var core = new FakeAdminClient().AddRoom("t.me", "t", 5, 5, 0);
        var presenter = new RoomEditPresenter(core, "token-1", "t.me");
        await presenter.LoadAsync();
        Assert.Equal("No adjacent rooms to link.", presenter.LinkAdjacent(core.Rooms.Values.Select(RoomRecord.From).ToList()).Status);
    }

    [Fact]
    public async Task Presenter_LinkAdjacent_StopsAtTheFormLimit_AndSaysSo()
    {
        var core = new FakeAdminClient().AddRoom("t.me", "t", 5, 5, 0).AddRoom("t.n", "t", 5, 6, 0).AddRoom("t.e", "t", 6, 5, 0);
        var presenter = new RoomEditPresenter(core, "token-1", "t.me");
        await presenter.LoadAsync();
        presenter.Exits = Enumerable.Range(0, RoomEditPresenter.MaxFormExits - 1).Select(i => new ExitInput { Dir = "door" + i, To = "t.x" }).ToList();
        var result = presenter.LinkAdjacent(core.Rooms.Values.Select(RoomRecord.From).ToList());
        Assert.Equal(RoomEditPresenter.MaxFormExits, presenter.Exits.Count);
        Assert.Equal(1, result.NotAddedForLimit);
        Assert.Contains("1 more not added", result.Status);
    }

    // ---- the page ----

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => default;
    }

    private sealed class TestNav : NavigationManager
    {
        public TestNav() { Initialize("http://localhost/", "http://localhost/creator/rooms/t.me"); }
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    private sealed class Host : ComponentBase
    {
        [Parameter] public string Key { get; set; } = string.Empty;
        [Parameter] public Action<RoomEdit>? Capture { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder b)
        {
            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "creator"), new Claim(SessionAuthentication.TokenClaim, "token-1") }, "test");
            var state = Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
            b.OpenComponent<CascadingValue<Task<AuthenticationState>>>(0);
            b.AddAttribute(1, "Value", state);
            b.AddAttribute(2, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<RoomEdit>(0);
                inner.AddAttribute(1, "Key", Key);
                inner.AddComponentReferenceCapture(2, o => Capture?.Invoke((RoomEdit)o));
                inner.CloseComponent();
            }));
            b.CloseComponent();
        }
    }

    private sealed class Page : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private readonly Func<string> _root;
        private readonly RoomEdit _edit;

        private Page(ServiceProvider provider, HtmlRenderer renderer, Func<string> root, RoomEdit edit) { _provider = provider; _renderer = renderer; _root = root; _edit = edit; }

        public static async Task<Page> OpenAsync(FakeAdminClient core, string key)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IAdminClient>(core);
            services.AddSingleton<NavigationManager, TestNav>();
            services.AddSingleton<NoticeService>();
            services.AddSingleton<IJSRuntime>(new NoJs());
            services.AddSingleton<DraftStore>();
            services.AddScoped<RoomDirectory>();
            services.AddLogging();
            var provider = services.BuildServiceProvider();
            var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
            RoomEdit? edit = null;
            var root = await renderer.Dispatcher.InvokeAsync(async () =>
                await renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Key"] = key, ["Capture"] = (Action<RoomEdit>)(e => edit = e) })));
            return new Page(provider, renderer, () => root.ToHtmlString(), edit!);
        }

        // Forgets the cached room list, so the next use asks core again.
        public void ForgetRoomList() => _provider.GetRequiredService<RoomDirectory>().Invalidate();

        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => _root());

        public Task Call(string method) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var m = typeof(RoomEdit).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
            if (m.Invoke(_edit, null) is Task t) { await t; }
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_edit, null);
        });

        public async ValueTask DisposeAsync() { await _renderer.DisposeAsync(); await _provider.DisposeAsync(); }
    }

    // t.me at (5,5,0) with an exit north to t.old; neighbours: t.n (north, already linked), t.e (east), t.s (south), and one above.
    private static FakeAdminClient World() => new FakeAdminClient()
        .AddRoom("t.me", "t", 5, 5, 0, ("north", "t.old"))
        .AddRoom("t.n", "t", 5, 6, 0)
        .AddRoom("t.e", "t", 6, 5, 0)
        .AddRoom("t.s", "t", 5, 4, 0)
        .AddRoom("t.up", "t", 5, 5, 1);

    private static int Writes(FakeAdminClient c) => c.RoomUpdates.Count + c.RoomCreates.Count + c.RoomDeletes.Count;

    [Fact]
    public async Task Page_ShowsTheButton_InTheExitsSection_WithAnAriaLiveStatus()
    {
        await using var page = await Page.OpenAsync(World(), "t.me");
        var html = await page.Html();
        var exits = html.Substring(html.IndexOf("id=\"re-exits-h\"", StringComparison.Ordinal));
        exits = exits.Substring(0, exits.IndexOf("</details>", StringComparison.Ordinal));
        Assert.Contains(">Link Adjacent Rooms</button>", exits);
        Assert.Contains("aria-live=\"polite\"", exits);
    }

    [Fact]
    public async Task Click_OnlyChangesTheForm_NothingIsSent_AndNeighboursAreUntouched()
    {
        var core = World();
        await using var page = await Page.OpenAsync(core, "t.me");
        await page.Call("LinkAdjacentAsync");
        var html = await page.Html();
        Assert.Equal(0, Writes(core));
        Assert.Contains("Added 2 exits to the form: east -&gt; t.e, south -&gt; t.s. Save to apply.", html);
        Assert.Equal(2, Regex.Matches(html, "New, not saved yet").Count);
        Assert.DoesNotContain("t.up", html.Substring(html.IndexOf("Added 2 exits", StringComparison.Ordinal)).Split("</details>")[0]);
        Assert.Empty(core.Rooms["t.e"].Exits);
        Assert.Empty(core.Rooms["t.s"].Exits);
        Assert.Single(core.Rooms["t.me"].Exits);
    }

    [Fact]
    public async Task Click_ReadsTheRoomListAtMostOnce_ThenASecondClickMakesNoCallAtAll()
    {
        var core = World();
        await using var page = await Page.OpenAsync(core, "t.me");
        var before = core.RoomTokens.Count;
        await page.Call("LinkAdjacentAsync");
        var afterFirst = core.RoomTokens.Count;
        Assert.True(afterFirst - before <= 1);
        await page.Call("LinkAdjacentAsync");
        Assert.Equal(afterFirst, core.RoomTokens.Count);
        Assert.Equal(0, Writes(core));
    }

    [Fact]
    public async Task SecondClick_AddsNothing_ReportsNoAdjacentRooms_AndTheButtonStaysEnabled()
    {
        var core = World();
        await using var page = await Page.OpenAsync(core, "t.me");
        await page.Call("LinkAdjacentAsync");
        await page.Call("LinkAdjacentAsync");
        var html = await page.Html();
        Assert.Contains("No adjacent rooms to link.", html);
        Assert.DoesNotContain("Added 2 exits", html);
        Assert.Equal(3, Regex.Matches(html, "id=\"re-exit-dir-\\d\"").Count);
        Assert.DoesNotMatch("id=\"re-link-adjacent\"[^>]*disabled", html);
    }

    [Fact]
    public async Task Save_AfterTheClick_SendsExactlyThePreviousExitsPlusTheAddedOnes_ThenTheMarkIsGone()
    {
        var core = World();
        await using var page = await Page.OpenAsync(core, "t.me");
        await page.Call("LinkAdjacentAsync");
        Assert.Empty(core.RoomUpdates);
        await page.Call("SaveAsync");
        var sent = Assert.Single(core.RoomUpdates);
        Assert.Equal(new[] { "north>t.old", "east>t.e", "south>t.s" }, sent.Exits.Select(e => e.Name + ">" + e.To).ToArray());
        Assert.DoesNotContain("New, not saved yet", await page.Html());
    }

    [Fact]
    public async Task Click_SkipsADirectionTheUserJustAddedButHasNotSaved()
    {
        var core = World();
        await using var page = await Page.OpenAsync(core, "t.me");
        var presenter = (RoomEditPresenter)typeof(RoomEdit).GetField("_presenter", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(GetEdit(page))!;
        presenter.Exits.Add(new ExitInput { Dir = "east", To = "t.elsewhere" });
        await page.Call("LinkAdjacentAsync");
        await page.Call("SaveAsync");
        Assert.Equal(new[] { "north>t.old", "east>t.elsewhere", "south>t.s" }, core.RoomUpdates.Single().Exits.Select(e => e.Name + ">" + e.To).ToArray());
    }

    [Fact]
    public async Task Click_WithDuplicateCoordinates_ShowsAWarningNamingTheRooms_AndAddsNothingThere()
    {
        var core = World().AddRoom("t.e2", "t", 6, 5, 0);
        await using var page = await Page.OpenAsync(core, "t.me");
        await page.Call("LinkAdjacentAsync");
        var html = await page.Html();
        Assert.Contains("Not linked east: these rooms share the same coordinates: t.e, t.e2.", html);
        Assert.Contains("Added 1 exit to the form: south -&gt; t.s. Save to apply.", html);
    }

    [Fact]
    public async Task Click_WhenCoreCannotListRooms_AddsNothing_AndSaysSo()
    {
        var core = World();
        await using var page = await Page.OpenAsync(core, "t.me");
        page.ForgetRoomList();
        core.RefuseRoomCalls = true;
        await page.Call("LinkAdjacentAsync");
        var html = await page.Html();
        Assert.Contains(Messages.LinkAdjacentUnavailable, html);
        Assert.DoesNotContain("New, not saved yet", html);
    }

    private static RoomEdit GetEdit(Page page) =>
        (RoomEdit)typeof(Page).GetField("_edit", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
}
