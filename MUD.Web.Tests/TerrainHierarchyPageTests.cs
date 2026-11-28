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
using MUD.Contracts;
using MUD.Web.Components.Pages;
using MUD.Web.Pages;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

// The terrain pages by area and terrain type, and the areas page as a hub: rendered pages over the fake client (the repo has no browser
// driver in the unit tests), plus the grep that no trace of the retired terrains multi-select remains.
public class TerrainHierarchyPageTests
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
        public TestNav() { Initialize("http://localhost/", "http://localhost/creator/terrain"); }
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
            services.AddSingleton<DraftStore>();
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

        public async Task SetQuery(Dictionary<string, object?>? query)
        {
            if (query != null)
            {
                await _renderer.Dispatcher.InvokeAsync(() => { foreach (var kv in query) { typeof(T).GetProperty(kv.Key)!.SetValue(Component, kv.Value); } });
            }
            await Invoke("OnParametersSetAsync");
        }

        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => _root());

        public Task Invoke(string method, params object?[] args) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var m = typeof(T).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
            if (m.Invoke(Component, args) is Task t) { await t; }
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Component, null);
        });

        public Task Refresh() => _renderer.Dispatcher.InvokeAsync(() => typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Component, null));

        public TPresenter Presenter<TPresenter>() => (TPresenter)typeof(T).GetField("_presenter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Component)!;

        public async ValueTask DisposeAsync() { await _renderer.DisposeAsync(); await _provider.DisposeAsync(); }
    }

    private static FakeAdminClient Core()
    {
        var core = new FakeAdminClient();
        core.AddArea("a", "Alpha", "First.", 4).AddArea("b", "Beta", "", 0).AddArea("c", "Gamma");
        core.AddTerrainType("a.forest", "Forest").AddTerrainType("a.grass", "Grass").AddTerrainType("b.sand", "Sand");
        core.Fragments["g1"] = new FragmentRecord { Id = "g1", Slot = "opening", Text = "Green.", Weight = 1, Terrain = "a.grass", Tags = { "calm" } };
        core.Fragments["g2"] = new FragmentRecord { Id = "g2", Slot = "opening", Text = "Trees.", Weight = 1, Terrain = "a.forest" };
        core.FeatureTemplates["t1"] = new FeatureTemplateRecord { Id = "t1", Short = "a rock", Long = "A rock.", Weight = 1, Chance = 1, Version = 1, Terrain = "a.grass" };
        core.NeighborPhrases["p1"] = new NeighborPhraseRecord { Id = "p1", NeighborTerrain = "a.forest", Text = "Trees {dir}.", Weight = 1, Terrain = "a.grass" };
        return core;
    }

    private static Dictionary<string, object?> Q(string? area = null, string? terrain = null, string? tab = null, string? text = null, string? tag = null) =>
        new() { ["Area"] = area, ["Terrain"] = terrain, ["Tab"] = tab, ["TextFilter"] = text, ["TagFilter"] = tag };

    private static string Select(string html, string id)
    {
        var m = Regex.Match(html, "<select id=\"" + id + "\".*?</select>", RegexOptions.Singleline);
        Assert.True(m.Success, "no select " + id);
        return m.Value;
    }

    private static List<string> OptionValues(string select) => Regex.Matches(select, "<option value(?:=\"([^\"]*)\")?").Select(m => m.Groups[1].Value).ToList();

    // ---- the list page

    [Fact]
    public async Task List_WithNoArea_ShowsTheAreaDropdownOnly()
    {
        await using var page = await Page<TerrainList>.OpenAsync(Core(), Q());
        var html = await page.Html();
        Assert.Equal(new[] { "", "a", "b", "c" }, OptionValues(Select(html, "tl-area")));
        Assert.DoesNotContain("id=\"tl-terrain\"", html);
        Assert.DoesNotContain("tl-tab", html);
        Assert.DoesNotContain("id=\"tl-text\"", html);
        Assert.Contains("Choose an area", html);
    }

    [Fact]
    public async Task List_TheTerrainDropdownSitsBeneathTheAreaDropdown_AndHoldsOnlyTheAreasTypes()
    {
        await using var page = await Page<TerrainList>.OpenAsync(Core(), Q("a", "a.grass"));
        var html = await page.Html();
        Assert.True(html.IndexOf("id=\"tl-area\"", StringComparison.Ordinal) < html.IndexOf("id=\"tl-terrain\"", StringComparison.Ordinal));
        Assert.Equal(new[] { "a.forest", "a.grass" }, OptionValues(Select(html, "tl-terrain")));
        Assert.Matches("<option value=\"a.grass\" selected", html);
        Assert.Contains("<select id=\"tl-area\"", html);    // native selects
    }

    [Fact]
    public async Task List_ChoosingAnArea_NavigatesToItWithoutATerrain_KeepingTheTab_AndChoosingATerrainKeepsTheArea()
    {
        await using var page = await Page<TerrainList>.OpenAsync(Core(), Q("a", "a.grass", "phrase"));
        await page.Invoke("ChooseArea", "b");
        Assert.Equal("/creator/terrain?area=b&tab=phrase", TestNav.Navigations.Last().Uri);
        await page.Invoke("ChooseTerrain", "a.forest");
        Assert.Equal("/creator/terrain?area=a&terrain=a.forest&tab=phrase", TestNav.Navigations.Last().Uri);
        await page.Invoke("ChooseTab", "feature");
        Assert.Equal("/creator/terrain?area=a&terrain=a.grass&tab=feature", TestNav.Navigations.Last().Uri);
    }

    [Fact]
    public async Task List_BackAndForth_AnAddressGivesTheSamePageBack()
    {
        var core = Core();
        await using var page = await Page<TerrainList>.OpenAsync(core, Q("a", "a.grass", "feature"));
        var first = await page.Html();
        await page.SetQuery(Q("b", null, "feature"));                 // another area: first terrain type of it
        Assert.Contains("<option value=\"b.sand\" selected", await page.Html());
        await page.SetQuery(Q("a", "a.grass", "feature"));            // the back button
        Assert.Equal(first, await page.Html());
    }

    [Fact]
    public async Task List_AreaWithoutTerrainTypes_SaysSo_AndOffersTheCreateForm()
    {
        await using var page = await Page<TerrainList>.OpenAsync(Core(), Q("c"));
        var html = await page.Html();
        Assert.Contains("No terrain types yet", html);
        Assert.DoesNotContain("id=\"tl-terrain\"", html);
        Assert.DoesNotContain("tl-tab", html);
    }

    [Fact]
    public async Task List_TheCreateForm_Opens_AndSavingCreatesAreaDotLocal_AndSelectsIt()
    {
        var core = Core();
        await using var page = await Page<TerrainList>.OpenAsync(core, Q("c"));
        var p = page.Presenter<TerrainListPresenter>();
        p.BeginNewType();
        p.FormLocal = "moor"; p.FormName = "Moor"; p.FormDescription = "Wet.";
        await page.Refresh();
        var html = await page.Html();
        Assert.Contains("id=\"tt-local\"", html);
        Assert.Contains("id=\"tt-name\"", html);
        Assert.Contains("id=\"tt-desc\"", html);
        await page.Invoke("SaveTypeAsync");
        html = await page.Html();
        Assert.Contains("Terrain type created.", html);
        Assert.True(core.TerrainTypes.ContainsKey("c.moor"));
        Assert.Equal("/creator/terrain?area=c&terrain=c.moor&tab=fragment", TestNav.Navigations.Last().Uri);
        Assert.Contains("<option value=\"c.moor\" selected", html);
        Assert.DoesNotContain("No terrain types yet", html);
    }

    [Fact]
    public async Task List_DeletingATerrainType_AsksFirst_ThenShowsCoresRefusalVerbatim()
    {
        var core = Core();
        const string refusal = "This terrain type is still in use by 1 description fragment. Move or delete it first.";
        await using var page = await Page<TerrainList>.OpenAsync(core, Q("a", "a.grass"));
        await page.Invoke("AskType");
        var html = await page.Html();
        Assert.Contains("role=\"alertdialog\"", html);
        Assert.Contains("(a.grass)", html);
        Assert.DoesNotContain(core.TerrainTypeCalls, c => c.StartsWith("DeleteTerrainType"));
        core.TerrainTypeResult = AdminResult.Refused;
        core.TerrainTypeMessage = refusal;
        await page.Invoke("ConfirmDeleteTypeAsync");
        html = await page.Html();
        Assert.Contains(refusal, html);
        Assert.True(core.TerrainTypes.ContainsKey("a.grass"));
        Assert.DoesNotContain("role=\"alertdialog\"", html);
        // Now it works.
        core.TerrainTypeResult = AdminResult.Ok;
        await page.Invoke("AskType");
        await page.Invoke("ConfirmDeleteTypeAsync");
        Assert.False(core.TerrainTypes.ContainsKey("a.grass"));
        Assert.Contains("Terrain type deleted.", await page.Html());
    }

    [Fact]
    public async Task List_TabsShowACountEach_AndOnlyTheChosenTerrainsEntries()
    {
        await using var page = await Page<TerrainList>.OpenAsync(Core(), Q("a", "a.grass"));
        var html = await page.Html();
        Assert.Contains("Fragments (1)", html);
        Assert.Contains("Feature templates (1)", html);
        Assert.Contains("Neighbour phrases (1)", html);
        Assert.Contains(">g1<", html);
        Assert.DoesNotContain(">g2<", html);       // a.forest's fragment
        var forest = await Page<TerrainList>.OpenAsync(Core(), Q("a", "a.forest"));
        var fh = await forest.Html();
        Assert.Contains("Fragments (1)", fh);
        Assert.Contains("Feature templates (0)", fh);
        await using var _ = forest;
    }

    [Fact]
    public async Task List_EmptyTabOfATerrain_SaysSo_InBothLayouts()
    {
        await using var page = await Page<TerrainList>.OpenAsync(Core(), Q("a", "a.forest", "feature"));
        var html = await page.Html();
        Assert.Equal(2, Regex.Matches(html, "No feature templates for this terrain type\\.").Count);
    }

    [Fact]
    public async Task List_NewEntryLink_CarriesAreaTerrainAndTab()
    {
        await using var page = await Page<TerrainList>.OpenAsync(Core(), Q("a", "a.grass", "phrase"));
        var html = await page.Html();
        Assert.Contains("href=\"/creator/terrain/edit?kind=phrase&amp;area=a&amp;terrain=a.grass\"", html);
    }

    [Fact]
    public async Task List_HasNoFilterButton_NoFreeTextTerrainFilter_AndNoFormForFilters()
    {
        await using var page = await Page<TerrainList>.OpenAsync(Core(), Q("a", "a.grass"));
        var html = await page.Html();
        Assert.Contains("id=\"tl-text\"", html);
        Assert.Contains("id=\"tl-tag\"", html);
        Assert.DoesNotContain("id=\"tl-terrain\" type", html);
        Assert.DoesNotContain(">Filter<", html);
        Assert.DoesNotContain("<form", html);
    }

    // ---- the edit page

    [Fact]
    public async Task Edit_ShowsAreaThenTerrainDropdowns_PreSelectedFromTheListPage_AndNoMultiSelectOrWarning()
    {
        var core = Core();
        await using var page = await Page<TerrainEdit>.OpenAsync(core, new() { ["Kind"] = "fragment", ["PrefillArea"] = "a", ["PrefillTerrain"] = "a.grass" });
        var html = await page.Html();
        Assert.True(html.IndexOf("id=\"te-area\"", StringComparison.Ordinal) < html.IndexOf("id=\"te-terrain\"", StringComparison.Ordinal));
        Assert.Contains("<option value=\"a\" selected", html);
        Assert.Contains("<option value=\"a.grass\" selected", html);
        Assert.Equal(new[] { "", "a.forest", "a.grass" }, OptionValues(Select(html, "te-terrain")));
        Assert.DoesNotContain("multiple", html);
        Assert.DoesNotContain("Unused", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mud-badge", html);
        Assert.Contains("href=\"/creator/terrain?area=a&amp;terrain=a.grass&amp;tab=fragment\"", html);   // back to the same list
    }

    [Fact]
    public async Task Edit_Phrase_NeighbourIsADropdownOfTheSameAreasTypes_AndFollowsTheTerrainsArea()
    {
        await using var page = await Page<TerrainEdit>.OpenAsync(Core(), new() { ["Kind"] = "phrase", ["Id"] = "p1" });
        var html = await page.Html();
        Assert.Equal(new[] { "", "a.forest", "a.grass" }, OptionValues(Select(html, "te-neighbor")));
        Assert.DoesNotContain("b.sand", Select(html, "te-neighbor"));
        await page.Invoke("Edit", (Action)(() => page.Presenter<TerrainEditPresenter>().ChooseArea("b")));
        html = await page.Html();
        Assert.Equal(new[] { "", "b.sand" }, OptionValues(Select(html, "te-neighbor")));
        Assert.Equal(new[] { "", "b.sand" }, OptionValues(Select(html, "te-terrain")));
    }

    [Fact]
    public async Task Edit_AnEntryWithAMissingTerrainType_RendersAndShowsMissing_ThenSavingNeedsAChoice()
    {
        var core = Core();
        core.Fragments["stray"] = new FragmentRecord { Id = "stray", Slot = "opening", Text = "Gone.", Weight = 1, Terrain = "a.vanished" };
        await using var page = await Page<TerrainEdit>.OpenAsync(core, new() { ["Kind"] = "fragment", ["Id"] = "stray" });
        var html = await page.Html();
        Assert.Contains("(missing: a.vanished)", html);
        Assert.Contains("<option value=\"a\" selected", html);
        await page.Invoke("SaveAsync");
        html = await page.Html();
        Assert.Contains(TerrainEditPresenter.TerrainRequired, html);
        Assert.Equal("a.vanished", core.Fragments["stray"].Terrain);
        await page.Invoke("Edit", (Action)(() => page.Presenter<TerrainEditPresenter>().ChooseTerrain("a.forest")));
        await page.Invoke("SaveAsync");
        Assert.Equal("a.forest", core.Fragments["stray"].Terrain);
        Assert.DoesNotContain("(missing:", await page.Html());
    }

    // ---- the areas hub

    [Fact]
    public async Task Areas_EachAreaShowsItsCountsAndLinks_AsTableAndCards()
    {
        var core = Core();
        core.AddItemTemplate("a.sword", "a sword").AddItemTemplate("a.shield", "a shield").AddItemTemplate("b.lamp", "a lamp");
        await using var page = await Page<AreaList>.OpenAsync(core);
        var html = await page.Html();
        // Alpha: 4 rooms, 2 terrain types, 2 item templates; shown in the table and the cards.
        Assert.Equal(2, Regex.Matches(html, "href=\"/creator/map\\?area=a\">4 rooms</a>").Count);
        Assert.Equal(2, Regex.Matches(html, "href=\"/creator/terrain\\?area=a\">2 terrain types</a>").Count);
        Assert.Equal(2, Regex.Matches(html, "href=\"/creator/items\\?area=a\">2 item templates</a>").Count);
        Assert.Equal(2, Regex.Matches(html, "href=\"/creator/terrain\\?area=b\">1 terrain type</a>").Count);
        Assert.Equal(2, Regex.Matches(html, "href=\"/creator/items\\?area=b\">1 item template</a>").Count);
        Assert.Equal(2, Regex.Matches(html, "href=\"/creator/items\\?area=c\">0 item templates</a>").Count);
        Assert.Equal(2, Regex.Matches(html, "href=\"/creator/map\\?area=c\">0 rooms</a>").Count);
        Assert.Contains("Alpha", html);
        Assert.Contains("First.", html);
        Assert.DoesNotContain("NPC", html);       // NPC templates do not exist yet: no section
    }

    [Fact]
    public async Task Areas_ACountThatCouldNotBeLoaded_ShowsAQuestionMark_NotAWrongZero()
    {
        var core = Core();
        core.RefuseItemTemplateCalls = true;
        await using var page = await Page<AreaList>.OpenAsync(core);
        var html = await page.Html();
        Assert.Contains("? item templates</a>", html);
        Assert.Contains("2 terrain types</a>", html);
    }

    [Fact]
    public void Areas_LinkHelpers_PointAtTheRightPages()
    {
        Assert.Equal("/creator/map?area=a%20b", AreaListPresenter.RoomsUrl("a b"));
        Assert.Equal("/creator/terrain?area=a", AreaListPresenter.TerrainTypesUrl("a"));
        Assert.Equal("/creator/items?area=a", AreaListPresenter.ItemTemplatesUrl("a"));
    }

    // ---- the items page understands ?area=

    [Fact]
    public async Task Items_AreaQuery_ListsOnlyThatAreasGroup_WithALinkBackToAll()
    {
        var core = Core();
        core.AddItemTemplate("a.sword", "a sword").AddItemTemplate("b.lamp", "a lamp");
        await using var page = await Page<ItemTemplateList>.OpenAsync(core, new() { ["Area"] = "b" });
        await page.Invoke("OnInitializedAsync");     // the router sets the query before the first load; the harness sets it after
        var html = await page.Html();
        Assert.Contains("data-area=\"b\"", html);
        Assert.DoesNotContain("data-area=\"a\"", html);
        Assert.Contains("a lamp", html);
        Assert.DoesNotContain("a sword", html);
        Assert.Contains("Show all areas", html);
        Assert.Contains("1 item template", html);
        Assert.Contains("href=\"/creator/items/edit?area=b\"", html);
        await using var all = await Page<ItemTemplateList>.OpenAsync(core);
        var allHtml = await all.Html();
        Assert.Contains("data-area=\"a\"", allHtml);
        Assert.Contains("data-area=\"b\"", allHtml);
        Assert.DoesNotContain("Show all areas", allHtml);
    }

    // ---- nothing of the retired concept remains

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MUD.sln"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void NoReferenceRemains_ToTheRetiredTermsMultiSelect_ItsWarning_OrTheFreeTextTerrainFilter()
    {
        var web = Path.Combine(Root(), "MUD.Web");
        foreach (var file in Directory.EnumerateFiles(web, "*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".razor") || f.EndsWith(".cs") || f.EndsWith(".css")) && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
        {
            var text = File.ReadAllText(file);
            var name = Path.GetRelativePath(web, file);
            Assert.False(Regex.IsMatch(text, @"\bExtraTerrains\b|\bmud-terrain-list\b|\bKnownTerrains\b|\bTerrainFilter\b"), name + " mentions the retired terrains list / terrain text filter");
            if (name.Contains("Terrain"))
            {
                Assert.DoesNotContain("multiple", text);
                Assert.DoesNotContain("Unused", text);
                Assert.False(Regex.IsMatch(text, @"empty\s*=\s*unused|empty list", RegexOptions.IgnoreCase), name + " still explains the empty-list rule");
                Assert.False(Regex.IsMatch(text, @"\bTerrains\b"), name + " still has a Terrains property");
            }
        }
    }

    [Fact]
    public void Layout_DropdownsAreNativeSelects_StackedWith44pxTargets_AndOnlyPaletteVariablesAreUsed()
    {
        var css = File.ReadAllText(Path.Combine(Root(), "MUD.Web", "wwwroot", "css", "mobile.css"));
        Assert.Contains(".mud-scope { display: flex; flex-direction: column;", css);
        Assert.Contains(".mud-scope .mud-field select { min-height: var(--mud-hit);", css);
        Assert.Matches(@"\.mud-arealinks \{ display: flex; flex-direction: column;", css);
        // The rules this card added carry no raw colour.
        foreach (var line in css.Split('\n').Where(l => l.StartsWith(".mud-scope") || l.StartsWith(".mud-arealinks")))
        {
            Assert.DoesNotMatch(@"#[0-9a-fA-F]{3,6}\b|\brgb\(", line);
        }
        var list = File.ReadAllText(Path.Combine(Root(), "MUD.Web", "Components", "Pages", "TerrainList.razor"));
        var edit = File.ReadAllText(Path.Combine(Root(), "MUD.Web", "Components", "Pages", "TerrainEdit.razor"));
        Assert.Contains("<select id=\"tl-area\"", list);
        Assert.Contains("<select id=\"tl-terrain\"", list);
        Assert.Contains("<select id=\"te-area\"", edit);
        Assert.Contains("<select id=\"te-terrain\"", edit);
        Assert.Contains("<select id=\"te-neighbor\"", edit);
        Assert.Contains("mud-cards", list);       // entries become stacked cards under 600px
        Assert.DoesNotContain("style=", list + edit);
    }
}
