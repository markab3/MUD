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

// The collapsible sections of the room edit page (WEB_BLAZOR.md 4.1): native details/summary, Basics open, the rest closed.
// The real RoomEdit is rendered with HtmlRenderer over FakeAdminClient; its handlers are invoked on the live instance and the page rendered again.
public class RoomEditSectionsTests
{
    private const string Token = "token-1";

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => default;
    }

    private sealed class TestNav : NavigationManager
    {
        public List<string> Visited { get; } = new();
        public TestNav() { Initialize("http://localhost/", "http://localhost/creator/rooms/t.hall"); }
        protected override void NavigateToCore(string uri, bool forceLoad) { Visited.Add(uri); }
    }

    private sealed class Host : ComponentBase
    {
        [Parameter] public string Key { get; set; } = string.Empty;
        [Parameter] public Action<RoomEdit>? Capture { get; set; }

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
                inner.AddComponentReferenceCapture(2, o => Capture?.Invoke((RoomEdit)o));
                inner.CloseComponent();
            }));
            b.CloseComponent();
        }
    }

    // A rendered page that stays alive: Html() renders the current state again, Call() runs a handler of the page and lets it re-render.
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

        public List<string> Visited => ((TestNav)_provider.GetRequiredService<NavigationManager>()).Visited;
        public NoticeService Notices => _provider.GetRequiredService<NoticeService>();
        public DraftStore Drafts => _provider.GetRequiredService<DraftStore>();

        // What a field's change handler does: set the value on the page's presenter, then re-render.
        public Task Edit<T>(Action<T> change) => _renderer.Dispatcher.InvokeAsync(() =>
        {
            var presenter = (T)typeof(RoomEdit).GetField("_presenter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_edit)!;
            change(presenter);
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_edit, null);
        });

        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => _root());

        // Runs a handler of the page the way an event does: the handler, then the re-render the framework does after a UI event.
        public Task Call(string method) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var m = typeof(RoomEdit).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
            if (m.Invoke(_edit, null) is Task t) { await t; }
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_edit, null);
        });

        public async ValueTask DisposeAsync() { await _renderer.DisposeAsync(); await _provider.DisposeAsync(); }
    }

    private static FakeAdminClient Core() => new FakeAdminClient().AddRoom("t.start").AddRoom("t.hall", "t", 1, 1, 0, ("north", "t.start"));

    private static readonly Regex DetailsTag = new("<details class=\"mud-section mud-collapse[^\"]*\"([^>]*)>\\s*<summary><h2 id=\"([^\"]+)\">([^<]*)</h2></summary>");

    // section heading text -> is the details element open
    private static Dictionary<string, bool> Sections(string html) =>
        DetailsTag.Matches(html).ToDictionary(m => m.Groups[3].Value, m => m.Groups[1].Value.Contains("open"));

    [Fact]
    public async Task Page_BasicsIsOpen_AndEveryOtherSectionIsClosed()
    {
        await using var page = await Page.OpenAsync(Core(), "t.hall");
        var s = Sections(await page.Html());
        Assert.Equal(new[] { "Basics", "Exits", "Features", "Behaviors", "Move room (change its key)", "Delete" }, s.Keys);
        Assert.True(s["Basics"]);
        Assert.All(s.Where(x => x.Key != "Basics"), x => Assert.False(x.Value, x.Key + " must start closed"));
    }

    [Fact]
    public async Task Page_GeneratedRoom_HasAGeneratedSection_ClosedByDefault_AndOthersDoNot()
    {
        var core = Core();
        core.Rooms["t.hall"].Gen = new RoomGen { Map = "meadow", Terrain = "grass" };
        await using var page = await Page.OpenAsync(core, "t.hall");
        var s = Sections(await page.Html());
        Assert.False(s["Generated room"]);
        Assert.Contains("Regen", await page.Html());
        await using var plain = await Page.OpenAsync(Core(), "t.hall");
        Assert.DoesNotContain("Generated room", Sections(await plain.Html()).Keys);
    }

    [Fact]
    public async Task Page_PutsEachHeadingInsideItsSummary_AsAnH2()
    {
        await using var page = await Page.OpenAsync(Core(), "t.hall");
        var html = await page.Html();
        Assert.Equal(6, Regex.Matches(html, "<summary><h2 id=\"re-[a-z]+-h\">[^<]+</h2></summary>").Count);
        // No h2 is left outside a summary.
        Assert.Equal(Regex.Matches(html, "<h2").Count, Regex.Matches(html, "<summary><h2").Count);
        // The body keeps the labelling by the heading.
        Assert.Contains("aria-labelledby=\"re-exits-h\"", html);
    }

    [Fact]
    public async Task Page_OpenIsNeverBoundToAToggledState_SoARenderLeavesTheUsersToggleAlone()
    {
        await using var page = await Page.OpenAsync(Core(), "t.hall");
        var first = await page.Html();
        // A successful save re-renders the page; the open attribute markup is byte-for-byte the same, so the browser keeps its state.
        await page.Call("SaveAsync");
        var second = await page.Html();
        Assert.Equal(Sections(first), Sections(second));
        Assert.Equal(first.Split("<details").Length, second.Split("<details").Length);
        var again = await page.Html();
        Assert.Equal(second, again);
    }

    [Fact]
    public async Task SaveWithAnInvalidFeature_OpensFeatures_AndBasicsStaysOpen_WithTheMessageOutsideAnySection()
    {
        var core = Core();
        core.RoomEditResult = AdminResult.Invalid;
        core.RoomEditErrors.Add(new FieldError { Field = "Features[0].Handles", Message = "Handle is too long." });
        core.Rooms["t.hall"].Features.Add(new RoomFeature { Short = "fountain", Long = "l" });
        await using var page = await Page.OpenAsync(core, "t.hall");
        Assert.False(Sections(await page.Html())["Features"]);
        await page.Call("SaveAsync");
        var html = await page.Html();
        var s = Sections(html);
        Assert.True(s["Features"]);
        Assert.True(s["Basics"]);
        Assert.False(s["Exits"]);
        Assert.False(s["Delete"]);
        // The problem list sits above the form, not inside a details element.
        Assert.True(html.IndexOf("Feature 1: Handle is too long.", StringComparison.Ordinal) < html.IndexOf("<details", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Exits[0].To", "Exits")]
    [InlineData("Exits", "Exits")]
    [InlineData("ConfirmDangling", "Exits")]
    [InlineData("Features", "Features")]
    [InlineData("Features[2].Plurals", "Features")]
    [InlineData("Short", "Basics")]
    [InlineData("Long", "Basics")]
    [InlineData("Area", "Basics")]
    [InlineData("NewLocalKey", "Move room (change its key)")]
    [InlineData("NewArea", "Move room (change its key)")]
    public async Task AnError_OpensTheSectionThatHoldsTheField(string field, string heading)
    {
        var core = Core();
        core.RoomEditResult = AdminResult.Invalid;
        core.RoomEditErrors.Add(new FieldError { Field = field, Message = "Problem here." });
        await using var page = await Page.OpenAsync(core, "t.hall");
        await page.Call(field.StartsWith("New") ? "MoveAsync" : "SaveAsync");
        var s = Sections(await page.Html());
        Assert.True(s[heading], heading);
        Assert.True(s["Basics"]);
    }

    [Fact]
    public async Task AFailedDelete_OpensTheDeleteSection_SoTheRefusalIsInContext()
    {
        var core = Core();
        core.RoomEditResult = AdminResult.Failed;
        await using var page = await Page.OpenAsync(core, "t.hall");
        await page.Call("DeleteAsync");
        var s = Sections(await page.Html());
        Assert.True(s["Delete"]);
        Assert.True(s["Basics"]);
        Assert.False(s["Exits"]);
    }

    [Fact]
    public async Task Save_SendsTheSameData_WhateverTheCollapsedState()
    {
        var core = Core();
        core.Rooms["t.hall"].Features.Add(new RoomFeature { Short = "fountain", Long = "water" });
        await using var page = await Page.OpenAsync(core, "t.hall");
        var html = await page.Html();
        // Collapsed sections still hold their fields in the markup: nothing is removed from the DOM.
        Assert.Contains("id=\"re-exit", html);
        Assert.Contains("id=\"re-f0-short\"", html);
        Assert.False(Sections(html)["Exits"]);
        Assert.False(Sections(html)["Features"]);
        await page.Call("SaveAsync");
        var collapsed = Assert.Single(core.RoomUpdates);
        Assert.Single(collapsed.Exits);
        Assert.Equal("north", collapsed.Exits[0].Name);
        Assert.True(collapsed.FeaturesPresent);
        Assert.Equal("fountain", Assert.Single(collapsed.Features).Short);

        // The same save through the presenter alone (no markup involved, so no open or closed state) sends the identical request.
        var plainCore = Core();
        plainCore.Rooms["t.hall"].Features.Add(new RoomFeature { Short = "fountain", Long = "water" });
        var p = new RoomEditPresenter(plainCore, Token, "t.hall");
        await p.LoadAsync();
        await p.SaveAsync();
        Assert.Equal(Assert.Single(plainCore.RoomUpdates), collapsed);
    }

    [Fact]
    public async Task MoveAndDelete_StillWork_FromTheirSections()
    {
        var core = Core();
        await using var page = await Page.OpenAsync(core, "t.hall");
        await page.Call("DeleteAsync");
        Assert.Equal(new[] { "t.hall" }, core.RoomDeletes);
        Assert.Contains("Delete this room", await page.Html());
    }

    [Fact]
    public async Task Page_RegenAndUpdateButtons_AreInTheGeneratedSection()
    {
        var core = Core();
        core.Rooms["t.hall"].Gen = new RoomGen { Map = "meadow", Terrain = "grass" };
        await using var page = await Page.OpenAsync(core, "t.hall");
        var html = await page.Html();
        var start = html.IndexOf("id=\"re-generated-h\"", StringComparison.Ordinal);
        var end = html.IndexOf("</details>", start, StringComparison.Ordinal);
        var section = html.Substring(start, end - start);
        Assert.Contains(">Regen<", section);
        Assert.Contains(">Update<", section);
        await page.Call("AskRegen");
        Assert.Contains("Confirm regen", await page.Html());
    }

    // ---- Save at the bottom, Back to the room's map level, Save goes to the map ----

    private static readonly Regex BackLink = new("<a class=\"mud-btn mud-btn-block\" id=\"re-back\" href=\"([^\"]*)\"");

    private static string BackHref(string html) => System.Net.WebUtility.HtmlDecode(BackLink.Match(html).Groups[1].Value);

    [Fact]
    public async Task SaveButton_IsTheLastActionOfThePage_AfterDelete_AndBelongsToTheForm()
    {
        await using var page = await Page.OpenAsync(Core(), "t.hall");
        var html = await page.Html();
        var save = html.IndexOf("Save room</button>", StringComparison.Ordinal);
        Assert.True(save > html.IndexOf("Delete this room", StringComparison.Ordinal));
        // The pinned bar is the last thing on the page: no other button follows it, and there is exactly one bar.
        Assert.DoesNotContain("<button", html.Substring(save));
        Assert.Single(Regex.Matches(html, "class=\"mud-pinbar\""));
        Assert.DoesNotContain("mud-sticky-actions", html);
        Assert.Contains("<form id=\"re-form\"", html);
        Assert.Matches("<button type=\"submit\" form=\"re-form\" class=\"mud-btn mud-btn-primary mud-btn-block mud-save\"", html);
        // The Save button is outside the form element; the form holds no other section.
        var formEnd = html.IndexOf("</form>", StringComparison.Ordinal);
        Assert.True(save > formEnd);
        Assert.Single(Regex.Matches(html, "<form"));
        // Back is in the page flow, before the bar (not pinned).
        var back = html.IndexOf("id=\"re-back\"", StringComparison.Ordinal);
        Assert.True(back > html.IndexOf("Delete this room", StringComparison.Ordinal));
        Assert.True(back < html.IndexOf("class=\"mud-pinbar\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaveButton_IsDisabledWhileBusy_ThroughTheBusyFlag()
    {
        await using var page = await Page.OpenAsync(Core(), "t.hall");
        Assert.DoesNotContain("disabled", System.Text.RegularExpressions.Regex.Match(await page.Html(), "<button type=\"submit\"[^>]*>").Value);
        Assert.Contains("disabled=\"@_busy\"", File.ReadAllText(SourcePath()));
    }

    private static string SourcePath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "MUD.sln"))) { dir = Path.GetDirectoryName(dir); }
        return Path.Combine(dir!, "MUD.Web", "Components", "Pages", "RoomEdit.razor");
    }

    [Fact]
    public async Task BackLink_IsFullWidth_AndGoesToTheStoredAreaAndLevel_CenteredOnTheRoom()
    {
        var core = new FakeAdminClient().AddRoom("t.hall", "t", 1, 1, 2);
        await using var page = await Page.OpenAsync(core, "t.hall");
        var html = await page.Html();
        Assert.Equal(WebRoutes.MapFor("t", 2, null, null, "t.hall"), BackHref(html));
        Assert.Contains("mud-btn-block", BackLink.Match(html).Value);
        Assert.Contains(".mud-btn-block { display: flex; width: 100%;", File.ReadAllText(Path.Combine(Path.GetDirectoryName(SourcePath())!, "..", "..", "wwwroot", "css", "mobile.css")));
    }

    [Fact]
    public async Task BackLink_NullZ_MeansLevelZero()
    {
        var core = new FakeAdminClient().AddRoom("t.hall", "t", null, null, null);
        await using var page = await Page.OpenAsync(core, "t.hall");
        Assert.Equal(WebRoutes.MapFor("t", 0, null, null, "t.hall"), BackHref(await page.Html()));
    }

    [Fact]
    public async Task BackLink_RoomWithoutArea_GoesToTheBareMap()
    {
        var core = new FakeAdminClient().AddRoom("t.hall", "", 1, 1, 1);
        await using var page = await Page.OpenAsync(core, "t.hall");
        Assert.Equal(WebRoutes.Map, BackHref(await page.Html()));
    }

    [Fact]
    public async Task BackLink_IgnoresAnUnsavedZEdit()
    {
        var core = new FakeAdminClient().AddRoom("t.hall", "t", 1, 1, 0);
        await using var page = await Page.OpenAsync(core, "t.hall");
        var before = BackHref(await page.Html());
        await page.Edit<RoomEditPresenter>(p => p.Z = 5);
        Assert.Equal(before, BackHref(await page.Html()));
        Assert.Equal(WebRoutes.MapFor("t", 0, null, null, "t.hall"), before);
    }

    [Fact]
    public async Task Save_Ok_GoesToTheMapOfTheSavedLevel_WithTheSavedNotice_AndDropsTheDraft()
    {
        var core = new FakeAdminClient().AddRoom("t.hall", "t", 1, 1, 0);
        await using var page = await Page.OpenAsync(core, "t.hall");
        await page.Edit<RoomEditPresenter>(p => p.Z = 3);
        await page.Call("SaveAsync");
        Assert.Equal(new[] { WebRoutes.MapFor("t", 3, null, null, "t.hall") }, page.Visited);
        Assert.Equal(Messages.RoomSaved, page.Notices.Take());
        Assert.Null(page.Notices.Take());
    }

    [Fact]
    public async Task Save_NothingChanged_AlsoGoesToTheMap_WithThatNotice()
    {
        var core = new FakeAdminClient { RoomEditResult = AdminResult.NoChange }.AddRoom("t.hall", "t", 1, 1, 0);
        await using var page = await Page.OpenAsync(core, "t.hall");
        await page.Call("SaveAsync");
        Assert.Equal(new[] { WebRoutes.MapFor("t", 0, null, null, "t.hall") }, page.Visited);
        Assert.Equal(Messages.RoomNoChange, page.Notices.Take());
    }

    [Fact]
    public async Task Save_Problem_StaysOnThePage_AndOpensTheSection()
    {
        var core = Core();
        core.RoomEditResult = AdminResult.Invalid;
        core.RoomEditErrors.Add(new FieldError { Field = "Exits[0].To", Message = "Bad." });
        await using var page = await Page.OpenAsync(core, "t.hall");
        await page.Call("SaveAsync");
        Assert.Empty(page.Visited);
        Assert.Null(page.Notices.Take());
        Assert.True(Sections(await page.Html())["Exits"]);
    }

    [Fact]
    public async Task OtherActions_KeepTheirOutcome_DeleteGoesToTheBareMap_NothingElseNavigatesOnOk()
    {
        var core = Core();
        core.Rooms["t.hall"].Gen = new RoomGen { Map = "meadow", Terrain = "grass" };
        await using var page = await Page.OpenAsync(core, "t.hall");
        await page.Call("UpdateGeneratedAsync");
        await page.Call("AskRegen");
        await page.Call("ConfirmRegenAsync");
        Assert.Empty(page.Visited);
        await page.Call("DeleteAsync");
        Assert.Equal(new[] { WebRoutes.Map }, page.Visited);
        Assert.Equal(Messages.RoomDeleted, page.Notices.Take());
    }

    // No component may bind an editable text input to an area field: the area of an existing room, rooms, items or terrain is chosen from a select.
    // The one exception is the new area's prefix on the Areas page (a free-text id by nature).
    [Fact]
    public void NoComponent_BindsAnEditableTextInput_ToAnAreaField_ExceptTheNewAreaPrefix()
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(SourcePath())!)!;   // .../Components
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, "<(input|InputText)\\b[^>]*>", RegexOptions.Singleline))
            {
                var tag = m.Value;
                var isArea = Regex.IsMatch(tag, "\\b(id|name)=\"[a-z]+-(new)?area\"|\\bArea\\b|\\.Area\\b|\\.NewArea\\b|datalist|list=\"[^\"]*area", RegexOptions.IgnoreCase);
                var readOnly = Regex.IsMatch(tag, "\\breadonly\\b");
                var isPrefixBox = Path.GetFileName(file) == "AreaList.razor" && tag.Contains("id=\"ar-id\"");
                if (isArea && !readOnly && !isPrefixBox) { offenders.Add(Path.GetFileName(file) + ": " + tag); }
            }
            // The remaining datalists suggest terrain types and hooks; none may offer areas.
            Assert.DoesNotMatch("<datalist[^>]*area", text);
        }
        Assert.Empty(offenders);
    }
}
