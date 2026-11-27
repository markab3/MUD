using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.RenderTree;
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

// The read-only Behaviors section of the room edit page (WEB_BLAZOR.md 4.1). The real RoomEdit page is rendered to HTML with the
// framework's HtmlRenderer over FakeAdminClient (no browser, no database); the section is never part of the form that is saved.
public class RoomBehaviorsPageTests
{
    private const string Token = "token-1";

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => default;
    }

    private sealed class Host : ComponentBase
    {
        [Parameter] public string Key { get; set; } = string.Empty;

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder b)
        {
            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "creator"), new Claim(SessionAuthentication.TokenClaim, Token) }, "test");
            var state = Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
            b.OpenComponent<CascadingValue<Task<AuthenticationState>>>(0);
            b.AddAttribute(1, "Value", state);
            b.AddAttribute(2, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<RoomEdit>(0);
                inner.AddAttribute(1, "Key", Key);
                inner.CloseComponent();
            }));
            b.CloseComponent();
        }
    }

    private static async Task<string> RenderAsync(FakeAdminClient core, string key)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAdminClient>(core);
        services.AddSingleton<NavigationManager, TestNav>();
        services.AddSingleton<NoticeService>();
        services.AddSingleton<IJSRuntime>(new NoJs());
        services.AddSingleton<DraftStore>();
        services.AddLogging();
        using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Key"] = key }));
            return output.ToHtmlString();
        });
    }

    private sealed class TestNav : NavigationManager
    {
        public TestNav() { Initialize("http://localhost/", "http://localhost/creator/rooms/t.hall"); }
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    private static FakeAdminClient Core()
    {
        var core = new FakeAdminClient().AddRoom("t.hall", "t", 1, 1, 0);
        var room = core.Rooms["t.hall"];
        room.Behaviors.Add(new RoomBehavior { Hook = "room.entered", Module = "zeta", Type = "Welcome", Priority = 5, ConfigJson = "{\"greeting\":\"hi <b>there</b>\",\"n\":[1,2]}" });
        room.Behaviors.Add(new RoomBehavior { Hook = "command.entered", Module = "alpha", Type = "A.Very.Long.Namespace.QualifiedTypeNameThatKeepsGoing", Priority = -1, ConfigJson = "{}" });
        return core;
    }

    private static string Section(string html)
    {
        var start = html.IndexOf("id=\"re-behaviors-h\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "Behaviors section missing");
        var end = html.IndexOf("</section>", start, StringComparison.Ordinal);
        return html.Substring(start, end - start);
    }

    [Fact]
    public async Task Page_ListsBehaviorsInStoredOrder_WithHookModuleTypeAndPriority()
    {
        var section = Section(await RenderAsync(Core(), "t.hall"));
        var first = section.IndexOf("room.entered", StringComparison.Ordinal);
        var second = section.IndexOf("command.entered", StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first, "stored order, not sorted");
        Assert.Contains("zeta", section);
        Assert.Contains("Welcome", section);
        Assert.Contains("<dd>5</dd>", section);
        Assert.Contains("<dd>-1</dd>", section);
        Assert.Contains("A.Very.Long.Namespace.QualifiedTypeNameThatKeepsGoing", section);
        Assert.DoesNotContain("No behaviors attached.", section);
    }

    [Fact]
    public async Task Page_ShowsTheConfigPrettyPrinted_InACollapsedBlock_AndEncoded()
    {
        var section = Section(await RenderAsync(Core(), "t.hall"));
        Assert.Contains("<details class=\"mud-config\">", section);
        Assert.DoesNotContain("<details class=\"mud-config\" open", section);
        Assert.Contains("<summary>Config</summary>", section);
        // The renderer HTML-encodes quotes and line feeds; the markup in the value is never live.
        Assert.Contains("&quot;greeting&quot;: &quot;hi &lt;b&gt;there&lt;/b&gt;&quot;", section);
        Assert.DoesNotContain("<b>there</b>", section);
        Assert.Contains("{&#xA;  &quot;greeting&quot;", section);
    }

    [Fact]
    public async Task Page_WithNoBehaviors_ShowsTheEmptyState_AndNoSearchSubsection()
    {
        var core = new FakeAdminClient().AddRoom("t.hall");
        var section = Section(await RenderAsync(core, "t.hall"));
        Assert.Contains("No behaviors attached.", section);
        Assert.DoesNotContain("<h3>Search</h3>", section);
        Assert.DoesNotContain("mud-behavior\"", section);
    }

    [Fact]
    public async Task Page_ShowsSearchEntries_IdTargetAndRefreshTime()
    {
        var core = Core();
        core.Rooms["t.hall"].Searches.Add(new RoomSearchEntry { Id = "table", Target = "oak table", RefreshMs = 90000, RewardShort = "a coin" });
        var section = Section(await RenderAsync(core, "t.hall"));
        Assert.Contains("<h3>Search</h3>", section);
        Assert.Contains("<dd>table</dd>", section);
        Assert.Contains("<dd>oak table</dd>", section);
        Assert.Contains("90000 ms", section);
        Assert.Contains("a coin", section);
    }

    private static async Task<string> SearchSection(params RoomSearchEntry[] entries)
    {
        var core = new FakeAdminClient().AddRoom("t.hall", "t", 1, 1, 0);
        core.Rooms["t.hall"].Searches.AddRange(entries);
        return Section(await RenderAsync(core, "t.hall"));
    }

    [Fact]
    public async Task Page_RendersAnItemRewardEntry_WithSummaryAndReadableCooldown()
    {
        var section = await SearchSection(new RoomSearchEntry { Id = "table", Target = "oak table", RefreshMs = 600000, RewardShort = "a coin" });
        Assert.Contains("Typing `search oak table` finds a coin (again after 10 minutes).", section);
        Assert.Contains("<dd title=\"600000 ms\">10 minutes <span class=\"mud-muted\">(600000 ms)</span></dd>", section);
        Assert.Contains("<dt>Reward</dt><dd>a coin</dd>", section);
        Assert.DoesNotContain("Discovery", section);
    }

    [Fact]
    public async Task Page_RendersADiscoveryOnlyEntry_WithNoRewardRow()
    {
        var section = await SearchSection(new RoomSearchEntry { Id = "floor", Target = "floor", RefreshMs = 600000, Discovery = new RoomSearchDiscovery { Id = "hidden_button", Found = "You press <i>it</i>.", Again = "Nothing new." } });
        Assert.Contains("Typing `search floor` records the discovery &#x27;hidden_button&#x27; for the player (once per player).", section);
        Assert.DoesNotContain("Reward", section);
        Assert.Contains("<dt>Discovery</dt><dd>hidden_button</dd>", section);
        Assert.Contains("You press &lt;i&gt;it&lt;/i&gt;.", section);
        Assert.Contains("<dt>Already found</dt><dd>Nothing new.</dd>", section);
    }

    [Fact]
    public async Task Page_RendersAnEntryWithBothRewardAndDiscovery_AndDefaultTextForEmptyMessages()
    {
        var section = await SearchSection(new RoomSearchEntry { Id = "chest", Target = "chest", RefreshMs = 90000, RewardShort = "a key", Discovery = new RoomSearchDiscovery { Id = "chest_seen" } });
        Assert.Contains("finds a key (again after 1 minute 30 seconds) and records the discovery", section);
        Assert.Contains("<dt>Reward</dt><dd>a key</dd>", section);
        Assert.Contains("<dt>Discovery</dt><dd>chest_seen</dd>", section);
        Assert.Contains("<dt>Found message</dt><dd>(default text)</dd>", section);
    }

    [Fact]
    public async Task Page_WithNoSearchEntries_ShowsNoSearchSubsection()
    {
        Assert.DoesNotContain("<h3>Search</h3>", await SearchSection());
    }

    [Theory]
    [InlineData(0, "0 ms")]
    [InlineData(5, "5 ms")]
    [InlineData(1000, "1 second")]
    [InlineData(600000, "10 minutes")]
    [InlineData(3600000, "1 hour")]
    [InlineData(5400000, "1 hour 30 minutes")]
    [InlineData(90061001, "1 day 1 hour 1 minute 1 second 1 ms")]
    public void Duration_IsReadable(int ms, string expected) => Assert.Equal(expected, SearchRow.Duration(ms));

    [Fact]
    public async Task Save_KeepsDiscoveryEntries_AndSendsNothingAboutThem()
    {
        var core = Core();
        core.Rooms["t.hall"].Searches.Add(new RoomSearchEntry { Id = "floor", Target = "floor", RefreshMs = 600000, Discovery = new RoomSearchDiscovery { Id = "hidden_button", Found = "f", Again = "a" } });
        var before = core.Rooms["t.hall"].Clone();
        var p = new RoomEditPresenter(core, Token, "t.hall");
        await p.LoadAsync();
        p.Short = "edited";
        Assert.Equal(RoomEditOutcome.Ok, await p.SaveAsync());
        Assert.Equal(before.Searches, core.Rooms["t.hall"].Searches);
        Assert.Equal("hidden_button", p.Searches.Single().Discovery!.Id);
        Assert.Contains("hidden_button", Section(await RenderAsync(core, "t.hall")));
    }

    [Fact]
    public async Task Page_StillRenders_WhenCoreSendsNoBehaviorsOrSearches_AsAnOlderCoreDoes()
    {
        // An older core leaves fields 11 and 12 unset: the lists are empty, nothing else changes.
        var core = new FakeAdminClient().AddRoom("t.hall", "t", 1, 1, 0);
        var html = await RenderAsync(core, "t.hall");
        Assert.Contains("Edit room", html);
        Assert.Contains("Save room", html);
        Assert.Contains("No behaviors attached.", Section(html));
    }

    [Fact]
    public async Task Page_HelpLine_NamesTheCreatorCommandAndTheAreaFile_AndHasNoControls()
    {
        var section = Section(await RenderAsync(Core(), "t.hall"));
        Assert.Contains("<code>behavior</code>", section);
        Assert.Contains("area file", section);
        Assert.DoesNotContain("<input", section);
        Assert.DoesNotContain("<button", section);
        Assert.DoesNotContain("<textarea", section);
        // The section sits outside the saved form.
        var html = await RenderAsync(Core(), "t.hall");
        Assert.True(html.IndexOf("</form>", StringComparison.Ordinal) < html.IndexOf("id=\"re-behaviors-h\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Save_LeavesBehaviorsAndSearchesAsTheyWere_AndSendsNothingAboutThem()
    {
        var core = Core();
        core.Rooms["t.hall"].Searches.Add(new RoomSearchEntry { Id = "table", Target = "oak table", RefreshMs = 90000 });
        var before = core.Rooms["t.hall"].Clone();
        var p = new RoomEditPresenter(core, Token, "t.hall");
        await p.LoadAsync();
        p.Short = "edited";
        Assert.Equal(RoomEditOutcome.Ok, await p.SaveAsync());

        var sent = Assert.Single(core.RoomUpdates);
        // The request type has no field for them at all, so a save cannot send them.
        Assert.DoesNotContain(typeof(RoomEditRequest).GetProperties(), x => x.Name.Contains("Behavior") || x.Name.Contains("Search"));
        Assert.Equal("t.hall", sent.Key);
        Assert.Equal(before.Behaviors, core.Rooms["t.hall"].Behaviors);
        Assert.Equal(before.Searches, core.Rooms["t.hall"].Searches);
        Assert.Equal(new[] { "room.entered", "command.entered" }, p.Behaviors.Select(b => b.Hook).ToArray());
        Assert.Single(p.Searches);

        // And the page, rendered after the save, still lists them.
        var section = Section(await RenderAsync(core, "t.hall"));
        Assert.Contains("room.entered", section);
        Assert.Contains("oak table", section);
    }

    [Fact]
    public async Task Move_RegenAndDelete_DoNotSendBehaviors()
    {
        var core = Core();
        var p = new RoomEditPresenter(core, Token, "t.hall");
        await p.LoadAsync();
        await p.UpdateAsync();
        await p.RegenAsync();
        Assert.Equal(2, core.Rooms["t.hall"].Behaviors.Count);
        Assert.Equal(2, p.Behaviors.Count);
    }

    [Theory]
    [InlineData("{\"a\":1}", "{\n  \"a\": 1\n}")]
    [InlineData("", "{}")]
    [InlineData("not json", "not json")]
    public void PrettyJson_Indents_AndFallsBackWithoutThrowing(string input, string expected) =>
        Assert.Equal(expected.Replace("\n", Environment.NewLine), RoomEditPresenter.PrettyJson(input));

    [Fact]
    public void TheStylesheet_StacksTheCards_UsesThePalette_AndKeepsTheSummaryTouchSized()
    {
        var dir = AppContext.BaseDirectory;
        string? css = null;
        for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "MUD.Web", "wwwroot", "css", "mobile.css");
            if (File.Exists(f)) { css = File.ReadAllText(f); break; }
        }
        Assert.NotNull(css);
        var block = css!.Substring(css.IndexOf("/* Room behaviors", StringComparison.Ordinal));
        Assert.DoesNotContain("<table", block);
        Assert.Contains("min-height: var(--mud-hit)", block);
        Assert.Contains("overflow: auto", block);
        Assert.DoesNotMatch(@"#[0-9A-Fa-f]{3,8}\b", block);
    }
}
