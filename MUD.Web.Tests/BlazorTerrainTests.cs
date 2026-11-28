using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using MUD.Contracts;
using MUD.Web.Pages;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

// The terrain library pages of the Blazor UI (WEB_BLAZOR.md 4.1): TerrainPagesTests ported from the Razor page models to the presenters.
// Same arrangement over FakeAdminClient, same sentences and calls. HTTP status codes become TerrainEditMissing values (WEB_BLAZOR.md 7.3).
// The Razor pages and their tests stay until the cutover card. LibraryClientTests (gRPC client and fake) are unchanged.
public class TerrainPresenterTests
{
    private const string Token = "token-1";

    private static FakeAdminClient Core()
    {
        var core = new FakeAdminClient();
        var f1 = new FragmentRecord { Id = "g1", Slot = "opening", Text = "Grass.", Weight = 2 };
        f1.Terrain = "a.grass"; f1.Tags.Add("calm");
        var f2 = new FragmentRecord { Id = "orphan", Slot = "opening", Text = "Nothing.", Weight = 1, Terrain = "a.forest" };
        var t1 = new FeatureTemplateRecord { Id = "t1", Short = "a rock", Long = "A rock.", Weight = 1, Chance = 0.5, Version = 3 };
        t1.Terrain = "a.forest";
        var p1 = new NeighborPhraseRecord { Id = "p1", NeighborTerrain = "a.river", Text = "A river flows {dir} of here.", Weight = 1 };
        p1.Terrain = "a.grass";
        core.AddTerrainType("a.forest").AddTerrainType("a.grass").AddTerrainType("a.river");
        core.Fragments["g1"] = f1; core.Fragments["orphan"] = f2;
        core.FeatureTemplates["t1"] = t1; core.NeighborPhrases["p1"] = p1;
        return core;
    }

    private static async Task<TerrainListPresenter> List(FakeAdminClient core, string? tab = null, string? terrain = null, string? tag = null)
    {
        var list = new TerrainListPresenter(core, Token);
        await list.LoadAsync(tab, terrain, tag);
        return list;
    }

    private static async Task<TerrainEditPresenter> Edit(FakeAdminClient core, string? kind, string? id)
    {
        var edit = new TerrainEditPresenter(core, Token, kind, id);
        await edit.LoadAsync();
        return edit;
    }

    // ---- list ----

    [Fact]
    public async Task List_ShowsFragmentsByDefault_AndKnownTerrains()
    {
        var core = Core();
        var page = await List(core);
        Assert.Equal(new[] { "g1", "orphan" }, page.Rows.Select(r => r.Id));
        Assert.Equal(new[] { "a.forest", "a.grass", "a.river" }, page.KnownTerrains);
        Assert.Equal(new[] { Token }, core.LibraryTokens.Distinct());
    }

    [Fact]
    public async Task List_UnassignedTabIsGone_AnUnknownTabIsTheFragmentsList_AndRowsCarryTheirTerrain()
    {
        // The earlier "unassigned" tab (empty terrains list) was retired with the empty-list rule: every entity has exactly one terrain.
        var page = await List(Core(), "unassigned");
        Assert.Equal(TerrainLibrary.Fragment, page.Tab);
        Assert.Equal(new[] { ("g1", "a.grass"), ("orphan", "a.forest") }, page.Rows.Select(r => (r.Id, r.Terrain)));
    }

    [Fact]
    public async Task List_FiltersByTerrainAndTag()
    {
        Assert.Equal(new[] { "g1" }, (await List(Core(), "fragment", "a.grass")).Rows.Select(r => r.Id));
        Assert.Equal(new[] { "g1" }, (await List(Core(), "fragment", null, "calm")).Rows.Select(r => r.Id));
        Assert.Equal(new[] { "orphan" }, (await List(Core(), "fragment", "a.forest")).Rows.Select(r => r.Id));
        Assert.Empty((await List(Core(), "fragment", "a.river")).Rows);
    }

    [Fact]
    public async Task List_FeatureAndPhraseTabs_ShowTheirKind()
    {
        var features = await List(Core(), "feature");
        Assert.Equal(new[] { "t1" }, features.Rows.Select(r => r.Id));
        Assert.Contains("version 3", features.Rows[0].Details);
        var phrases = await List(Core(), "phrase");
        Assert.Equal(new[] { "p1" }, phrases.Rows.Select(r => r.Id));
        Assert.Contains("A river flows north of here.", phrases.Rows[0].Details);
    }

    [Fact]
    public async Task List_UnknownTab_IsFragments_AndBlankFiltersAreNone()
    {
        var page = await List(Core(), "bogus", "  ", "");
        Assert.Equal(TerrainLibrary.Fragment, page.Tab);
        Assert.Null(page.TerrainFilter);
        Assert.Null(page.TagFilter);
        Assert.Equal("Description fragments", page.TabLabel);
    }

    [Fact]
    public async Task List_CoreRefusedSession_ShowsTheForbiddenMessage()
    {
        var core = Core();
        core.RefuseLibraryCalls = true;
        var page = new TerrainListPresenter(core, Token);
        Assert.Equal(TerrainListOutcome.Failed, await page.LoadAsync(null, null, null));
        Assert.Equal(TerrainLibrary.ForbiddenError, page.Error);
        Assert.Empty(page.Rows);
    }

    [Fact]
    public async Task List_CoreUnavailable_ShowsAFixedMessage()
    {
        var page = new TerrainListPresenter(new FakeAdminClient { Unavailable = true }, Token);
        Assert.Equal(TerrainListOutcome.Failed, await page.LoadAsync(null, null, null));
        Assert.Equal(TerrainLibrary.UnavailableError, page.Error);
    }

    [Fact]
    public void ListUrl_KeepsTheTabAndFilters_InTheQueryString()
    {
        Assert.Equal(WebRoutes.Terrain + "?tab=fragment", TerrainListPresenter.ListUrl(null, null, null));
        Assert.Equal(WebRoutes.Terrain + "?tab=feature&terrain=a%20b&tag=calm", TerrainListPresenter.ListUrl("feature", " a b ", "calm"));
        Assert.Equal(WebRoutes.Terrain + "?tab=fragment", TerrainListPresenter.ListUrl("evil&x=1", null, null));
    }

    [Theory]
    [InlineData("fragment", "g1")]
    [InlineData("feature", "t1")]
    [InlineData("phrase", "p1")]
    public async Task Delete_Ok_SaysDeleted_AndRemoves(string kind, string id)
    {
        var core = Core();
        var page = new TerrainListPresenter(core, Token);
        Assert.Equal(TerrainListOutcome.Ok, await page.DeleteAsync(kind, id));
        Assert.Equal("Deleted.", page.Notice);
        Assert.False(core.Fragments.ContainsKey(id) || core.FeatureTemplates.ContainsKey(id) || core.NeighborPhrases.ContainsKey(id));
        Assert.Equal(new[] { Token }, core.LibraryTokens.Distinct());
    }

    [Theory]
    [InlineData(AdminResult.NotFound, "That entry does not exist.")]
    [InlineData(AdminResult.Refused, TerrainLibrary.RefusedError)]
    [InlineData(AdminResult.AuditFailed, TerrainLibrary.AuditFailedError)]
    [InlineData(AdminResult.Forbidden, TerrainLibrary.ForbiddenError)]
    [InlineData(AdminResult.Failed, TerrainLibrary.FailedError)]
    public async Task Delete_ResultBranches_ShowFixedNotice(AdminResult result, string expected)
    {
        var core = Core();
        core.LibraryResult = result;
        var page = new TerrainListPresenter(core, Token);
        Assert.Equal(TerrainListOutcome.Ok, await page.DeleteAsync("fragment", "g1"));
        Assert.Equal(expected, page.Notice);
        Assert.True(core.Fragments.ContainsKey("g1"));
    }

    [Fact]
    public async Task Delete_NoChange_ShowsCoresMessage_OrTheFixedOne()
    {
        var core = Core();
        core.LibraryResult = AdminResult.NoChange;
        var page = new TerrainListPresenter(core, Token);
        await page.DeleteAsync("fragment", "g1");
        Assert.Equal("Nothing was changed.", page.Notice);
        core.LibraryMessage = "Already gone.";
        await page.DeleteAsync("fragment", "g1");
        Assert.Equal("Already gone.", page.Notice);
    }

    [Fact]
    public async Task Delete_UnknownKind_IsRefusedWithoutACall()
    {
        var core = Core();
        var page = new TerrainListPresenter(core, Token);
        Assert.Equal(TerrainListOutcome.Failed, await page.DeleteAsync("bogus", "g1"));
        Assert.Equal(TerrainLibrary.FailedError, page.Error);
        Assert.Empty(core.LibraryCalls);
    }

    [Fact]
    public async Task Delete_CoreRefusedSession_ShowsTheForbiddenMessage()
    {
        var core = Core();
        core.RefuseLibraryCalls = true;
        var page = new TerrainListPresenter(core, Token);
        Assert.Equal(TerrainListOutcome.Failed, await page.DeleteAsync("fragment", "g1"));
        Assert.Equal(TerrainLibrary.ForbiddenError, page.Error);
    }

    // ---- edit: load ----

    [Fact]
    public async Task Edit_New_Fragment_ListsTheTerrainTypesOfEveryArea()
    {
        var page = await Edit(Core(), "fragment", null);
        Assert.True(page.IsNew);
        Assert.False(page.IsMissing);
        Assert.Equal(new[] { "a.forest", "a.grass", "a.river" }, page.TerrainChoices);
        Assert.Equal("A new template starts at version 1.", page.VersionHint);
    }

    [Fact]
    public async Task Edit_Existing_LoadsFields_AndItsTerrain()
    {
        var page = await Edit(Core(), "feature", "t1");
        Assert.False(page.IsNew);
        Assert.Equal("a rock", page.Short);
        Assert.Equal("a.forest", page.Terrain);
        Assert.Equal(3, page.Version);
        Assert.Contains("from 3 to 4", page.VersionHint);
    }

    [Fact]
    public async Task Edit_Existing_FragmentLoadsItsTerrain_AndAMoveIsSentAsTheNewTerrain()
    {
        var core = Core();
        var page = await Edit(core, "fragment", "orphan");
        Assert.Equal("a.forest", page.Terrain);
        page.Terrain = "a.grass";
        Assert.Equal(TerrainEditOutcome.Ok, await page.SaveAsync());
        Assert.Equal("a.grass", core.Fragments["orphan"].Terrain);
    }

    [Fact]
    public async Task Edit_UnknownKindOrId_IsMissing()
    {
        var page = await Edit(Core(), "bogus", null);
        Assert.True(page.IsMissing);
        Assert.Equal(TerrainEditMissing.NotFound, page.Missing);
        Assert.Equal("That library entry does not exist.", page.Error);
        page = await Edit(Core(), "phrase", "nope");
        Assert.Equal(TerrainEditMissing.NotFound, page.Missing);
    }

    [Fact]
    public async Task Edit_CoreUnavailableOrRefused_ShowsFixedMessages()
    {
        var down = await Edit(new FakeAdminClient { Unavailable = true }, "fragment", "g1");
        Assert.Equal(TerrainEditMissing.Unavailable, down.Missing);
        Assert.Equal(TerrainLibrary.UnavailableError, down.Error);
        var core = Core();
        core.RefuseLibraryCalls = true;
        var refused = await Edit(core, "fragment", "g1");
        Assert.Equal(TerrainEditMissing.Forbidden, refused.Missing);
        Assert.Equal(TerrainLibrary.ForbiddenError, refused.Error);
    }

    // ---- edit: save ----

    [Fact]
    public async Task Save_NewFragment_SendsParsedLists_AndSaysSaved()
    {
        var core = Core();
        var page = await Edit(core, "fragment", null);
        page.NewId = " new1 "; page.Slot = "opening"; page.Text = "Text."; page.Weight = 4; page.Tags = "a, b  c";
        page.Terrain = " a.grass ";
        Assert.Equal(TerrainEditOutcome.Ok, await page.SaveAsync());
        Assert.Equal("Saved.", page.Notice);
        var saved = core.Fragments["new1"];
        Assert.Equal("a.grass", saved.Terrain);
        Assert.Equal(new[] { "a", "b", "c" }, saved.Tags);
        Assert.Equal(4, saved.Weight);
        Assert.Contains("CreateFragment", core.LibraryCalls);
    }

    [Fact]
    public async Task Save_NewPhrase_SendsTheTerrainAndTheNeighbourAsTypedIds()
    {
        var core = Core();
        var page = await Edit(core, "phrase", null);
        page.NewId = "x"; page.Terrain = "a.grass"; page.NeighborTerrain = "a.river"; page.Text = "{dir}";
        await page.SaveAsync();
        Assert.Equal(("a.grass", "a.river"), (core.NeighborPhrases["x"].Terrain, core.NeighborPhrases["x"].NeighborTerrain));
    }

    [Fact]
    public async Task Save_Edit_UsesTheAddressId_NotTheFormId_AndUpdates()
    {
        var core = Core();
        var page = await Edit(core, "feature", "t1");
        page.NewId = "other"; page.Short = "a big rock"; page.Long = "A rock."; page.Chance = 0.5; page.Weight = 1; page.Terrain = "a.forest";
        await page.SaveAsync();
        Assert.Contains("UpdateFeatureTemplate", core.LibraryCalls);
        Assert.False(core.FeatureTemplates.ContainsKey("other"));
        Assert.Equal("a big rock", core.FeatureTemplates["t1"].Short);
        Assert.Equal(4, core.FeatureTemplates["t1"].Version);
    }

    [Fact]
    public async Task Save_Invalid_ShowsCoreFieldErrors_AndKeepsValues()
    {
        var core = Core();
        core.LibraryResult = AdminResult.Invalid;
        var page = await Edit(core, "phrase", null);
        page.NewId = "x"; page.Text = "no placeholder"; page.NeighborTerrain = "a.river";
        Assert.Equal(TerrainEditOutcome.Invalid, await page.SaveAsync());
        Assert.Equal("no placeholder", page.Text);
        Assert.Equal("a.river", page.NeighborTerrain);
        Assert.False(page.IsMissing);
    }

    [Fact]
    public void ErrorText_PrefixesTheField()
    {
        Assert.Equal("text: required", TerrainLibrary.ErrorText(new FieldError { Field = "text", Message = "required" }));
        Assert.Equal("required", TerrainLibrary.ErrorText(new FieldError { Message = "required" }));
    }

    [Theory]
    [InlineData(AdminResult.AlreadyExists, "An entry with that id already exists.")]
    [InlineData(AdminResult.Refused, TerrainLibrary.RefusedError)]
    [InlineData(AdminResult.AuditFailed, TerrainLibrary.AuditFailedError)]
    [InlineData(AdminResult.Forbidden, TerrainLibrary.ForbiddenError)]
    [InlineData(AdminResult.Failed, TerrainLibrary.FailedError)]
    public async Task Save_ResultBranches_ShowFixedError(AdminResult result, string expected)
    {
        var core = Core();
        core.LibraryResult = result;
        var page = await Edit(core, "fragment", null);
        page.NewId = "x"; page.Slot = "s"; page.Text = "t";
        Assert.Equal(TerrainEditOutcome.Failed, await page.SaveAsync());
        Assert.Equal(expected, page.Error);
        Assert.False(page.IsMissing);
    }

    [Fact]
    public async Task Save_NoChange_SaysSo()
    {
        var core = Core();
        core.LibraryResult = AdminResult.NoChange;
        var page = await Edit(core, "fragment", "g1");
        Assert.Equal(TerrainEditOutcome.NoChange, await page.SaveAsync());
        Assert.Equal("Nothing was changed.", page.Notice);
    }

    [Fact]
    public async Task Save_NotFound_IsMissing()
    {
        var core = Core();
        var page = await Edit(core, "fragment", "g1");
        core.LibraryResult = AdminResult.NotFound;
        Assert.Equal(TerrainEditOutcome.Missing, await page.SaveAsync());
        Assert.Equal(TerrainEditMissing.NotFound, page.Missing);
    }

    [Fact]
    public async Task Save_CoreRefusedSession_ShowsTheForbiddenMessage()
    {
        var core = Core();
        var page = await Edit(core, "fragment", null);
        core.RefuseLibraryCalls = true;
        page.NewId = "x";
        Assert.Equal(TerrainEditOutcome.Missing, await page.SaveAsync());
        Assert.Equal(TerrainEditMissing.Forbidden, page.Missing);
        Assert.Equal(TerrainLibrary.ForbiddenError, page.Error);
    }

    [Fact]
    public async Task Save_UnknownKind_NeverReachesCore()
    {
        var core = Core();
        var page = new TerrainEditPresenter(core, Token, "bogus", null) { NewId = "x" };
        Assert.Equal(TerrainEditOutcome.Missing, await page.SaveAsync());
        Assert.Empty(core.LibraryCalls);
    }

    [Fact]
    public async Task Preview_UsesTheSavedValues()
    {
        var phrase = await Edit(Core(), "phrase", "p1");
        Assert.True(phrase.HasPreview);
        Assert.Equal("A river flows north of here.", phrase.PreviewText);
        var feature = await Edit(Core(), "feature", "t1");
        Assert.Equal("a rock", feature.PreviewText);
        feature.RoomLine = "A rock sits here.";
        Assert.Equal("A rock sits here.", feature.PreviewText);
        Assert.False((await Edit(Core(), "fragment", "g1")).HasPreview);
    }

    [Fact]
    public void Split_And_Preview_Helpers()
    {
        Assert.Equal(new[] { "a", "b" }, TerrainLibrary.Split(" a,,b a "));
        Assert.Equal("A river flows north of here.", TerrainLibrary.PhrasePreview("A river flows {dir} of here."));
    }

    // ---- shape ----

    private static Type Component(string name) =>
        typeof(IAdminClient).Assembly.GetTypes().Single(t => t.FullName == "MUD.Web.Components.Pages." + name);

    private static string Read(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MUD.sln"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    [Theory]
    [InlineData("TerrainList", "/creator/terrain")]
    [InlineData("TerrainEdit", "/creator/terrain/edit")]
    public void ThePages_RequireTheCreatorOrAdminPolicy_OnTheirRoutes(string name, string route)
    {
        var type = Component(name);
        Assert.Equal(WebPolicies.CreatorOrAdmin, type.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal(route, type.GetCustomAttribute<RouteAttribute>()!.Template);
    }

    [Fact]
    public void RouteConstants_MatchThePageRoutes()
    {
        Assert.Equal(WebRoutes.Terrain, Component("TerrainList").GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal(WebRoutes.TerrainEditPage, Component("TerrainEdit").GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal(WebRoutes.Prefix + "/creator/terrain/edit?kind=fragment&id=a%20b", WebRoutes.TerrainEdit("fragment", "a b"));
        Assert.Equal(WebRoutes.Prefix + "/creator/terrain/edit?kind=phrase", WebRoutes.TerrainEdit("phrase"));
    }

    [Fact]
    public void TheKindAndId_ComeOnlyFromTheAddress_AndThePresenterCannotChangeThem()
    {
        // Replaces the Razor "exact bound properties" test: no settable Kind or original id, and no Version setter.
        var presenter = typeof(TerrainEditPresenter);
        Assert.Null(presenter.GetProperty("Kind")!.SetMethod);
        Assert.Null(presenter.GetProperty("OriginalId")!.SetMethod);
        Assert.False(presenter.GetProperty("Version")!.SetMethod?.IsPublic ?? false);
        var page = Component("TerrainEdit");
        foreach (var name in new[] { "Kind", "Id" })
        {
            Assert.NotNull(page.GetProperty(name)!.GetCustomAttribute<SupplyParameterFromQueryAttribute>());
            Assert.Null(page.GetProperty(name)!.GetCustomAttribute<ParameterAttribute>());
        }
        Assert.Equal("kind", page.GetProperty("Kind")!.GetCustomAttribute<SupplyParameterFromQueryAttribute>()!.Name);
        Assert.Equal("id", page.GetProperty("Id")!.GetCustomAttribute<SupplyParameterFromQueryAttribute>()!.Name);
    }

    [Fact]
    public void TheListQuery_IsTheOnlyStateOfThePage()
    {
        var type = Component("TerrainList");
        foreach (var (prop, name) in new[] { ("Tab", "tab"), ("TerrainFilter", "terrain"), ("TagFilter", "tag") })
        {
            Assert.Equal(name, type.GetProperty(prop)!.GetCustomAttribute<SupplyParameterFromQueryAttribute>()!.Name);
        }
        Assert.Null(typeof(TerrainListPresenter).GetProperty("Tab")!.SetMethod?.IsPublic == true ? "settable" : null);
    }

    [Fact]
    public void Sources_NeverReferencePassword_NeverUseRawHtml_NeverTouchTheDatabase()
    {
        foreach (var f in new[] { "Components/Pages/TerrainList.razor", "Components/Pages/TerrainEdit.razor", "Pages/TerrainListPresenter.cs", "Pages/TerrainEditPresenter.cs" })
        {
            var text = Read("MUD.Web/" + f);
            Assert.DoesNotContain("Password", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("MarkupString", text);
            Assert.DoesNotContain("Mongo", text);
            Assert.DoesNotContain("<script", text);
            Assert.DoesNotContain("http://", text);
            Assert.DoesNotContain("https://", text);
        }
    }

    [Fact]
    public void TheList_DeleteAsksFirst_AndListsAreTablesWideAndStackedCardsNarrow()
    {
        var src = Read("MUD.Web/Components/Pages/TerrainList.razor");
        Assert.Contains("<ConfirmDialog", src);
        Assert.Contains("Ask(r)", src);
        // No button deletes directly: the only DeleteAsync call is the confirm handler's.
        Assert.DoesNotContain("@onclick=\"() => ConfirmDeleteAsync", src);
        Assert.DoesNotContain("@onclick=\"() => p.DeleteAsync", src);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(src, @"\.DeleteAsync\("));
        Assert.Contains("mud-table-wrap", src);
        Assert.Contains("mud-cards", src);
        // Tabs: links wide, one select on a phone.
        Assert.Contains("mud-tabs", src);
        Assert.Contains("mud-tabselect", src);
        var css = Read("MUD.Web/wwwroot/css/mobile.css");
        Assert.Matches(@"\.mud-tabs \{ display: none;", css);
        Assert.Matches(@"@media \(min-width: 600px\)\s*\{\s*\.mud-tabs \{ display: flex; \}\s*\.mud-tabselect \{ display: none; \}", css);
    }

    [Fact]
    public void TheEditor_KeepsADraft_AndOffersASingleTerrainTypeDropdown_WithNoEmptyListWarning()
    {
        var src = Read("MUD.Web/Components/Pages/TerrainEdit.razor");
        Assert.Contains("Drafts.SaveAsync", src);
        Assert.Contains("Messages.TerrainDraftFound", src);
        Assert.DoesNotContain("Unused", src);               // the empty-list warning is retired: every entry has exactly one terrain
        Assert.DoesNotContain("ExtraTerrains", src);
        Assert.Contains("id=\"te-terrain\"", src);        // one terrain-type dropdown fed from ListTerrainTypes
        Assert.Contains("inputmode=\"numeric\"", src);
        Assert.Contains("mud-sticky-actions", src);
    }

    [Fact]
    public void TheNav_LinksToTheBlazorTerrainPage_UnderTheCreatorPolicy()
    {
        var nav = Read("MUD.Web/Pages/NavMenu.cs");
        var creator = nav.IndexOf("if (creatorOrAdmin)", StringComparison.Ordinal);
        Assert.True(creator >= 0);
        Assert.True(nav.IndexOf("WebRoutes.Terrain", StringComparison.Ordinal) > creator);
        Assert.DoesNotContain("LegacyTerrain", nav);
    }

    [Fact]
    public void TheNewSentences_AreFixedStrings()
    {
        Assert.False(string.IsNullOrWhiteSpace(Messages.TerrainDeleteConfirm));
        Assert.Contains("last write wins", Messages.TerrainDraftFound);
    }
}
