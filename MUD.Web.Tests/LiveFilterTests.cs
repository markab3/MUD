using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MUD.Web.Components.Pages;
using MUD.Web.Pages;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

// Live filters (WEB_BLAZOR.md "Live filters"): the debounce and latest-wins rules in a plain class with a fake clock, the three pages
// applying a settled value without a submit, and the menu order.
public class LiveFilterRunnerTests
{
    // Waits (bounded) for a condition instead of guessing how many scheduler turns a continuation needs.
    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) { await Task.Delay(10); }
    }

    // A clock the test controls: every Delay waits until the test releases it (or its token is cancelled).
    private sealed class FakeClock
    {
        public readonly List<(TimeSpan Delay, TaskCompletionSource Gate)> Waits = new();

        public Task Delay(TimeSpan delay, CancellationToken cancellation)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellation.Register(() => gate.TrySetCanceled(cancellation));
            Waits.Add((delay, gate));
            return gate.Task;
        }

        public void Release(int index) => Waits[index].Gate.TrySetResult();
    }

    private sealed class Source
    {
        public readonly List<(string Text, TaskCompletionSource<Action?> Reply, CancellationToken Token)> Calls = new();
        public readonly List<string> Published = new();

        public Task<Action?> Apply(string text, CancellationToken token)
        {
            var reply = new TaskCompletionSource<Action?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Add((text, reply, token));
            return reply.Task;
        }

        public void Finish(int index) => Calls[index].Reply.SetResult(() => Published.Add(Calls[index].Text));
    }

    [Fact]
    public async Task ASettledValue_IsAppliedOnce_AfterTheQuietPeriod()
    {
        var clock = new FakeClock(); var source = new Source();
        using var runner = new LiveFilterRunner(source.Apply, delayer: clock.Delay);
        var run = runner.Input("zel");
        Assert.Equal(TimeSpan.FromMilliseconds(250), clock.Waits[0].Delay);
        Assert.Empty(source.Calls);              // nothing before the delay ends
        Assert.True(runner.Pending);
        clock.Release(0);
        await Until(() => source.Calls.Count >= 1);
        Assert.Equal("zel", Assert.Single(source.Calls).Text);
        Assert.True(runner.Busy);
        source.Finish(0);
        await run;
        Assert.Equal(new[] { "zel" }, source.Published);
        Assert.False(runner.Busy);
        Assert.False(runner.Pending);
    }

    [Fact]
    public async Task ManyKeystrokes_InsideTheQuietPeriod_MakeOneCall()
    {
        var clock = new FakeClock(); var source = new Source();
        using var runner = new LiveFilterRunner(source.Apply, delayer: clock.Delay);
        var runs = new[] { runner.Input("z"), runner.Input("ze"), runner.Input("zel") };
        clock.Release(0); clock.Release(1); clock.Release(2);   // the first two were cancelled by the later keystrokes
        await Until(() => source.Calls.Count >= 1);
        Assert.Equal("zel", Assert.Single(source.Calls).Text);
        source.Finish(0);
        await Task.WhenAll(runs);
        Assert.Equal(new[] { "zel" }, source.Published);
    }

    [Fact]
    public async Task ARepliesThatArrivesAfterANewerKeystroke_IsDropped_AndTheOlderRequestIsCancelled()
    {
        var clock = new FakeClock(); var source = new Source();
        using var runner = new LiveFilterRunner(source.Apply, delayer: clock.Delay);
        var first = runner.Input("a");
        clock.Release(0);
        await Until(() => source.Calls.Count >= 1);
        Assert.Single(source.Calls);                       // "a" is in flight
        var second = runner.Input("ab");                   // a newer keystroke
        Assert.True(source.Calls[0].Token.IsCancellationRequested);
        clock.Release(1);
        await Until(() => source.Calls.Count >= 2);
        Assert.Equal(2, source.Calls.Count);
        source.Finish(1);                                  // the newer reply comes first
        await second;
        source.Finish(0);                                  // the older one limps in late
        await first;
        Assert.Equal(new[] { "ab" }, source.Published);    // never "a"
        Assert.False(runner.Busy);
    }

    [Fact]
    public async Task OnlyOneRequestIsLive_ANewValueCancelsTheOldOne_SoNothingPilesUp()
    {
        var clock = new FakeClock(); var source = new Source();
        using var runner = new LiveFilterRunner(source.Apply, delayer: clock.Delay);
        var runs = new List<Task>();
        for (var i = 0; i < 5; i++)
        {
            runs.Add(runner.Input(new string('x', i + 1)));
            clock.Release(i);
            await Until(() => source.Calls.Count >= i + 1);
        }
        Assert.Equal(5, source.Calls.Count);
        Assert.Equal(4, source.Calls.Count(c => c.Token.IsCancellationRequested));
        Assert.False(source.Calls[4].Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Immediate_SkipsTheQuietPeriod_AndStillWinsOverAnEarlierKeystroke()
    {
        var clock = new FakeClock(); var source = new Source();
        using var runner = new LiveFilterRunner(source.Apply, delayer: clock.Delay);
        var typed = runner.Input("abc");
        var cleared = runner.Immediate(string.Empty);
        await Until(() => source.Calls.Count >= 1);
        Assert.Empty(clock.Waits.Skip(1));                 // Immediate waited for nothing
        Assert.Equal(string.Empty, Assert.Single(source.Calls).Text);
        source.Finish(0);
        await Task.WhenAll(typed, cleared);
        Assert.Equal(new[] { string.Empty }, source.Published);
    }

    [Fact]
    public async Task AFailedApply_PublishesNothing_AndClearsBusy()
    {
        var clock = new FakeClock();
        using var runner = new LiveFilterRunner((_, _) => throw new InvalidOperationException("boom"), delayer: clock.Delay);
        var run = runner.Input("x");
        clock.Release(0);
        await run;
        Assert.False(runner.Busy);
        Assert.False(runner.Pending);
    }

    [Fact]
    public async Task Cancel_DropsAPendingValue()
    {
        var clock = new FakeClock(); var source = new Source();
        using var runner = new LiveFilterRunner(source.Apply, delayer: clock.Delay);
        var run = runner.Input("x");
        runner.Cancel();
        await run;
        Assert.Empty(source.Calls);
        Assert.False(runner.Pending);
    }

    [Fact]
    public async Task ThePublishStep_RunsThroughTheMarshal()
    {
        var clock = new FakeClock(); var source = new Source(); var marshalled = 0;
        using var runner = new LiveFilterRunner(source.Apply, delayer: clock.Delay, marshal: work => { marshalled++; return work(); });
        var run = runner.Input("x");
        clock.Release(0);
        await Until(() => source.Calls.Count >= 1);
        source.Finish(0);
        await run;
        Assert.Equal(1, marshalled);
    }

    [Theory]
    [InlineData(0, "0 players")]
    [InlineData(1, "1 player")]
    [InlineData(12, "12 players")]
    public void Count_SaysTheNumberAndPluralises(int n, string expected) => Assert.Equal(expected, LiveFilterRunner.Count(n, "player"));

    [Fact]
    public void Clean_TrimsAndCutsToTheLimit()
    {
        Assert.Equal("ab", LiveFilterRunner.Clean("  ab  ", 64));
        Assert.Equal(string.Empty, LiveFilterRunner.Clean(null, 64));
        Assert.Equal(64, LiveFilterRunner.Clean(new string('x', 200), 64).Length);
    }

    [Fact]
    public void AnEchoOfAnAddressThePageWrote_IsSkippedOnce_AnyOtherNavigationReloadsAndForgets()
    {
        var echoes = new LiveUrlEchoes();
        echoes.Expect("a"); echoes.Expect("b");
        Assert.True(echoes.Consume("a"));
        Assert.False(echoes.Consume("a"));      // only once
        Assert.False(echoes.Consume("c"));      // a real navigation clears what was pending
        Assert.False(echoes.Consume("b"));
    }
}

public class LiveFilterPageTests
{
    private const string Token = "token-1";

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => default;
    }

    private sealed class TestNav : NavigationManager
    {
        public static readonly List<(string Uri, bool Replace)> Navigations = new();
        public TestNav() { Initialize("http://localhost/", "http://localhost/creator/players"); }
        protected override void NavigateToCore(string uri, NavigationOptions options) { Navigations.Add((uri, options.ReplaceHistoryEntry)); }
    }

    private sealed class Host<T> : ComponentBase where T : IComponent
    {
        [Parameter] public Action<T>? Capture { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder b)
        {
            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "admin"), new Claim(SessionAuthentication.TokenClaim, Token) }, "test");
            var state = Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
            b.OpenComponent<CascadingValue<Task<AuthenticationState>>>(0);
            b.AddAttribute(1, "Value", state);
            b.AddAttribute(2, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<T>(0);
                inner.AddComponentReferenceCapture(1, o => Capture?.Invoke((T)o));
                inner.CloseComponent();
            }));
            b.CloseComponent();
        }
    }

    private sealed class Page<T> : IAsyncDisposable where T : ComponentBase
    {
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private readonly Func<string> _root;
        public readonly T Component;

        private Page(ServiceProvider provider, HtmlRenderer renderer, Func<string> root, T component) { _provider = provider; _renderer = renderer; _root = root; Component = component; }

        public static async Task<Page<T>> OpenAsync(FakeAdminClient core, Dictionary<string, object?>? query = null)
        {
            TestNav.Navigations.Clear();
            var services = new ServiceCollection();
            services.AddSingleton<IAdminClient>(core);
            services.AddSingleton<NavigationManager, TestNav>();
            services.AddSingleton<NoticeService>();
            services.AddSingleton<IJSRuntime>(new NoJs());
            services.AddLogging();
            var provider = services.BuildServiceProvider();
            var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
            T? captured = null;
            var root = await renderer.Dispatcher.InvokeAsync(async () =>
                await renderer.RenderComponentAsync<Host<T>>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Capture"] = (Action<T>)(c => captured = c) })));
            var page = new Page<T>(provider, renderer, () => root.ToHtmlString(), captured!);
            await page.SetQuery(query);
            return page;
        }

        // Sets the query-string properties as the router would, then runs the page's parameter hook (what a navigation does).
        public async Task SetQuery(Dictionary<string, object?>? query)
        {
            if (query == null) { await Invoke("OnParametersSetAsync"); return; }
            await _renderer.Dispatcher.InvokeAsync(() => { foreach (var kv in query) { typeof(T).GetProperty(kv.Key)!.SetValue(Component, kv.Value); } });
            await Invoke("OnParametersSetAsync");
        }

        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => _root());

        public Task Invoke(string method, params object?[] args) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var m = typeof(T).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
            if (m.Invoke(Component, args) is Task t) { await t; }
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Component, null);
        });

        // What the live filter does when a typed value settles.
        public Task Settle(string text, string method) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var m = typeof(T).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
            var publish = await (Task<Action?>)m.Invoke(Component, new object?[] { text, CancellationToken.None })!;
            publish?.Invoke();
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Component, null);
        });

        public async ValueTask DisposeAsync() { await _renderer.DisposeAsync(); await _provider.DisposeAsync(); }
    }

    private static FakeAdminClient Players(int n = 3)
    {
        var core = new FakeAdminClient();
        for (var i = 0; i < n; i++) { core.AddPlayer("Pat" + i.ToString("D3")); }
        core.AddPlayer("Zelda");
        return core;
    }

    // ---- players (server side)

    [Fact]
    public async Task Players_TypingAppliesTheFilter_WithoutASubmit_AndWritesTheAddressInPlace()
    {
        var core = Players();
        await using var page = await Page<PlayerList>.OpenAsync(core);
        var before = core.PlayerListCalls.Count;
        await page.Settle("zel", "ApplyFilterAsync");
        Assert.Equal(before + 1, core.PlayerListCalls.Count);          // one call for the settled value
        Assert.Equal(("zel", false, 1), (core.PlayerListCalls.Last().Filter, core.PlayerListCalls.Last().Descending, core.PlayerListCalls.Last().Page));
        var html = await page.Html();
        Assert.Contains("Zelda", html);
        Assert.DoesNotContain("Pat000", html);
        Assert.Contains("1 player", html);
        var nav = Assert.Single(TestNav.Navigations);
        Assert.True(nav.Replace);                                       // no history entry per keystroke
        Assert.Equal("/creator/players?q=zel&sort=asc&page=1", nav.Uri);
    }

    [Fact]
    public async Task Players_TheAddressTheFilterWrote_ComesBackWithoutASecondCall()
    {
        var core = Players();
        await using var page = await Page<PlayerList>.OpenAsync(core);
        await page.Settle("zel", "ApplyFilterAsync");
        var calls = core.PlayerListCalls.Count;
        await page.SetQuery(new() { ["Filter"] = "zel", ["Sort"] = "asc", ["PageNumber"] = 1 });   // the replace navigation arriving
        Assert.Equal(calls, core.PlayerListCalls.Count);
        await page.SetQuery(new() { ["Filter"] = "pat", ["Sort"] = "asc", ["PageNumber"] = 1 });   // a real navigation reloads
        Assert.Equal(calls + 1, core.PlayerListCalls.Count);
    }

    [Fact]
    public async Task Players_DeepLink_PrefillsTheBox_AndAppliesIt_KeepingSortAndPage()
    {
        var core = Players(120);
        await using var page = await Page<PlayerList>.OpenAsync(core, new() { ["Filter"] = "Pat", ["Sort"] = "desc", ["PageNumber"] = 2 });
        var call = core.PlayerListCalls.Last();
        Assert.Equal(("Pat", true, 2), (call.Filter, call.Descending, call.Page));
        var html = await page.Html();
        Assert.Contains("id=\"pl-q\"", html);
        Assert.Contains("value=\"Pat\"", html);
        Assert.Contains("Page 2 of 3", html);
        Assert.Contains("q=Pat&amp;sort=desc&amp;page=3", html);      // paging links keep the text and the sort
    }

    [Fact]
    public async Task Players_ChangingTheText_StartsAgainFromPageOne_AndKeepsTheSortOrder()
    {
        var core = Players(120);
        await using var page = await Page<PlayerList>.OpenAsync(core, new() { ["Filter"] = "Pat", ["Sort"] = "desc", ["PageNumber"] = 3 });
        await page.Settle("Pat0", "ApplyFilterAsync");
        var call = core.PlayerListCalls.Last();
        Assert.Equal(("Pat0", true, 1), (call.Filter, call.Descending, call.Page));
        Assert.Equal("/creator/players?q=Pat0&sort=desc&page=1", TestNav.Navigations.Last().Uri);
    }

    [Fact]
    public async Task Players_ACoreError_KeepsTheOldList_AndShowsTheFixedMessage()
    {
        var core = Players();
        await using var page = await Page<PlayerList>.OpenAsync(core);
        core.Unavailable = true;
        await page.Settle("zel", "ApplyFilterAsync");
        var html = await page.Html();
        Assert.Contains(Messages.Unavailable, html);
        Assert.Contains("Pat000", html);                   // the old list is still there
        Assert.Contains("4 players", html);
        Assert.Empty(TestNav.Navigations);                 // the address was not rewritten for a failed value
        core.Unavailable = false;
        await page.Settle("zel", "ApplyFilterAsync");      // and the next good value clears the message
        html = await page.Html();
        Assert.DoesNotContain(Messages.Unavailable, html);
        Assert.DoesNotContain("Pat000", html);
        Assert.Contains("1 player", html);
    }

    [Fact]
    public async Task Players_TheBoxIsALiveFilter_NoFilterButton_NoForm_ACountAndAClearButton()
    {
        await using var page = await Page<PlayerList>.OpenAsync(Players());
        var html = await page.Html();
        Assert.DoesNotContain("<form", html);
        Assert.DoesNotContain("type=\"submit\"", html);
        Assert.DoesNotContain(">Filter<", html);
        Assert.Contains("aria-live=\"polite\"", html);
        Assert.Contains("4 players", html);
        Assert.Contains("aria-label=\"Clear name contains\"", html);
        Assert.Contains("maxlength=\"64\"", html);
    }

    // ---- item templates (client side)

    [Fact]
    public async Task Items_TypingNarrowsTheLoadedList_WithNoCallToCore()
    {
        var core = new FakeAdminClient().AddArea("a", "Ay land").AddArea("b", "Bee land").AddItemTemplate("a.sword", "a rusty sword").AddItemTemplate("b.honey", "a pot of honey");
        await using var page = await Page<ItemTemplateList>.OpenAsync(core);
        var html0 = await page.Html();
        Assert.Contains("2 item templates", html0);
        var calls = core.ItemTemplateCalls.Count;
        await page.Settle("honey", "ApplyFilterAsync");
        var html = await page.Html();
        Assert.Contains("a pot of honey", html);
        Assert.DoesNotContain("a rusty sword", html);
        Assert.Contains("1 item template", html);
        Assert.Equal(calls, core.ItemTemplateCalls.Count);     // no rpc
        Assert.DoesNotContain("<form", html);
        Assert.DoesNotContain("type=\"submit\"", html);
        await page.Settle(string.Empty, "ApplyFilterAsync");    // clearing shows everything again
        Assert.Contains("a rusty sword", await page.Html());
    }

    // ---- terrain (client side)

    [Fact]
    public async Task Terrain_TypingNarrowsTheLoadedRows_WithNoCallToCore_AndWritesTheAddressInPlace()
    {
        var core = new FakeAdminClient();
        core.Fragments["g1"] = new MUD.Contracts.FragmentRecord { Id = "g1", Slot = "opening", Text = "Grass.", Weight = 1, Terrain = "a.forest" };
        core.Fragments["g2"] = new MUD.Contracts.FragmentRecord { Id = "g2", Slot = "opening", Text = "Trees.", Weight = 1, Terrain = "a.forest" };
        core.Fragments["g3"] = new MUD.Contracts.FragmentRecord { Id = "g3", Slot = "opening", Text = "Sand.", Weight = 1, Terrain = "a.grass" };
        core.AddArea("a").AddTerrainType("a.grass").AddTerrainType("a.forest");
        await using var page = await Page<TerrainList>.OpenAsync(core, new() { ["Area"] = "a", ["Terrain"] = "a.forest" });
        Assert.Contains("2 entries", await page.Html());      // only the chosen terrain's entries
        core.LibraryCalls.Clear();
        await page.Settle("trees", "ApplyTextAsync");
        var html = await page.Html();
        Assert.Contains(">g2<", html);
        Assert.DoesNotContain(">g1<", html);
        Assert.DoesNotContain(">g3<", html);
        Assert.Contains("1 entry", html);
        Assert.Empty(core.LibraryCalls);
        var nav = Assert.Single(TestNav.Navigations);
        Assert.True(nav.Replace);
        Assert.Equal("/creator/terrain?area=a&terrain=a.forest&tab=fragment&q=trees", nav.Uri);
        await page.Settle("calm", "ApplyTagAsync");
        Assert.Equal("/creator/terrain?area=a&terrain=a.forest&tab=fragment&q=trees&tag=calm", TestNav.Navigations.Last().Uri);
        Assert.Contains("0 entries", await page.Html());
    }

    [Fact]
    public async Task Terrain_DeepLink_PrefillsBothBoxes()
    {
        var core = new FakeAdminClient();
        core.AddArea("a").AddTerrainType("a.grass");
        core.Fragments["g1"] = new MUD.Contracts.FragmentRecord { Id = "g1", Slot = "opening", Text = "Grass.", Weight = 1, Terrain = "a.grass" };
        await using var page = await Page<TerrainList>.OpenAsync(core, new() { ["Area"] = "a", ["Terrain"] = "a.grass", ["TextFilter"] = "gra", ["TagFilter"] = "calm" });
        var html = await page.Html();
        Assert.Matches("id=\"tl-text\"[^>]*value=\"gra\"", html);
        Assert.Matches("id=\"tl-tag\"[^>]*value=\"calm\"", html);
    }

    // ---- no submit-only filter remains

    private static string Read(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MUD.sln"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    [Theory]
    [InlineData("Components/Pages/PlayerList.razor", "pl-q")]
    [InlineData("Components/Pages/ItemTemplateList.razor", "it-filter")]
    [InlineData("Components/Pages/TerrainList.razor", "tl-text")]
    [InlineData("Components/Pages/TerrainList.razor", "tl-tag")]
    public void EveryListFilter_IsALiveFilter_NotAFormWithASubmitButton(string file, string id)
    {
        var src = Read("MUD.Web/" + file);
        Assert.Contains("<LiveFilter Id=\"" + id + "\"", src);
        Assert.DoesNotContain("<form", src);
        Assert.DoesNotContain("type=\"submit\"", src);
        Assert.DoesNotContain("onsubmit", src);
    }

    [Fact]
    public void NoComponentHasASearchBoxOrFilterButton_OutsideTheLiveFilter()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "MUD.sln"))) { root = root.Parent; }
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root!.FullName, "MUD.Web", "Components"), "*.razor", SearchOption.AllDirectories))
        {
            var src = File.ReadAllText(file);
            var name = Path.GetFileName(file);
            Assert.False(Regex.IsMatch(src, "type=\"search\""), name + " has a search box outside LiveFilter");
            Assert.False(Regex.IsMatch(src, @">\s*Filter\s*</button>"), name + " has a Filter submit button");
        }
    }

    [Fact]
    public void TheLiveFilter_UsesTheRunner_KeepsItsValueStable_AndHasNoFormOrSubmit()
    {
        var src = Read("MUD.Web/Components/Shared/LiveFilter.razor");
        Assert.Contains("new LiveFilterRunner(", src);
        Assert.Contains("@oninput=\"OnInput\"", src);
        Assert.Contains("aria-live=\"polite\"", src);
        Assert.DoesNotContain("<form", src);
        Assert.DoesNotContain("type=\"submit\"", src);
        Assert.Equal(250, LiveFilterRunner.DefaultDelayMs);
        var css = Read("MUD.Web/wwwroot/css/mobile.css");
        Assert.Contains(".mud-liverow input { flex: 1 1 auto; min-width: 0; }", css);   // 44px comes from the shared input rule, no sideways scroll
    }
}
