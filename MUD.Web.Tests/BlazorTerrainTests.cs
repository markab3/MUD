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
        core.AddArea("a").AddArea("b").AddArea("c");
        core.AddTerrainType("a.forest").AddTerrainType("a.grass").AddTerrainType("a.river").AddTerrainType("b.sand");
        core.Fragments["g1"] = f1; core.Fragments["orphan"] = f2;
        core.FeatureTemplates["t1"] = t1; core.NeighborPhrases["p1"] = p1;
        return core;
    }

    private static async Task<TerrainListPresenter> List(FakeAdminClient core, string? area = null, string? terrain = null, string? tab = null, string? text = null, string? tag = null)
    {
        var list = new TerrainListPresenter(core, Token);
        await list.LoadAsync(area, terrain, tab, text, tag);
        return list;
    }

    private static async Task<TerrainEditPresenter> Edit(FakeAdminClient core, string? kind, string? id, string? area = null, string? terrain = null)
    {
        var edit = new TerrainEditPresenter(core, Token, kind, id, area, terrain);
        await edit.LoadAsync();
        return edit;
    }

    // ---- list ----

    [Fact]
    public async Task List_WithNoArea_ShowsNothingToPick_AndNoCallForEntries()
    {
        var core = Core();
        var page = await List(core);
        Assert.Null(page.Area);
        Assert.Null(page.Terrain);
        Assert.Empty(page.Types);
        Assert.Empty(page.Rows);
        Assert.DoesNotContain("ListFragments", core.LibraryCalls);
        Assert.Equal(new[] { "a", "b", "c" }, page.Areas.Select(a => a.Id));
    }

    [Fact]
    public async Task List_AreaThenTerrain_TheTerrainDropdownHoldsOnlyTheChosenAreasTypes_AndDefaultsToTheFirst()
    {
        var core = Core();
        var a = await List(core, "a");
        Assert.Equal(new[] { "a.forest", "a.grass", "a.river" }, a.Types.Select(t => t.Id));
        Assert.Equal("a.forest", a.Terrain);
        var b = await List(core, "b");
        Assert.Equal(new[] { "b.sand" }, b.Types.Select(t => t.Id));
        Assert.Equal("b.sand", b.Terrain);
        // A terrain of ANOTHER area is not accepted: the first type of the chosen area is used.
        var mixed = await List(core, "b", "a.grass");
        Assert.Equal("b.sand", mixed.Terrain);
        // An area without terrain types: no terrain, no rows.
        var empty = await List(core, "c");
        Assert.Empty(empty.Types);
        Assert.Null(empty.Terrain);
        Assert.Empty(empty.Rows);
        // An unknown area is no area.
        Assert.Null((await List(core, "nope")).Area);
    }

    [Fact]
    public async Task List_ShowsOnlyTheChosenTerrainsEntries_AndACountPerTab()
    {
        var core = Core();
        var forest = await List(core, "a", "a.forest");
        Assert.Equal(new[] { "orphan" }, forest.Rows.Select(r => r.Id));
        Assert.Equal((1, 1, 0), (forest.Fragments.Count, forest.Features.Count, forest.Phrases.Count));
        var grass = await List(core, "a", "a.grass");
        Assert.Equal(new[] { "g1" }, grass.Rows.Select(r => r.Id));
        Assert.Equal((1, 0, 1), (grass.Fragments.Count, grass.Features.Count, grass.Phrases.Count));
        Assert.Empty((await List(core, "a", "a.river")).Rows);
    }

    [Fact]
    public async Task List_FiltersLiveWithinTheChosenTerrain_ByTextAndTag_WithoutACallToCore()
    {
        var core = Core();
        var page = await List(core, "a", "a.grass");
        core.LibraryCalls.Clear();
        page.ApplyFilters("gra", null);
        Assert.Equal(new[] { "g1" }, page.Rows.Select(r => r.Id));
        page.ApplyFilters("zzz", null);
        Assert.Empty(page.Rows);
        page.ApplyFilters(null, "calm");
        Assert.Equal(new[] { "g1" }, page.Rows.Select(r => r.Id));
        page.ApplyFilters(null, "wild");
        Assert.Empty(page.Rows);
        page.ApplyFilters(" ", "");
        Assert.Equal(new[] { "g1" }, page.Rows.Select(r => r.Id));
        Assert.Empty(core.LibraryCalls);
        // The filter never reaches entries of another terrain: "Nothing." is the orphan fragment of a.forest.
        page.ApplyFilters("Nothing", null);
        Assert.Empty(page.Rows);
    }

    [Fact]
    public async Task List_FeatureAndPhraseTabs_ShowTheirKind()
    {
        var features = await List(Core(), "a", "a.forest", "feature");
        Assert.Equal(new[] { "t1" }, features.Rows.Select(r => r.Id));
        Assert.Contains("version 3", features.Rows[0].Details);
        var phrases = await List(Core(), "a", "a.grass", "phrase");
        Assert.Equal(new[] { "p1" }, phrases.Rows.Select(r => r.Id));
        Assert.Contains("A river flows north of here.", phrases.Rows[0].Details);
    }

    [Fact]
    public async Task List_UnknownTab_IsFragments_AndBlankFiltersAreNone()
    {
        var page = await List(Core(), "a", null, "bogus", "  ", "");
        Assert.Equal(TerrainLibrary.Fragment, page.Tab);
        Assert.Null(page.TextFilter);
        Assert.Null(page.TagFilter);
        Assert.Equal("Description fragments", page.TabLabel);
    }

    [Fact]
    public async Task List_CoreRefusedSession_ShowsTheForbiddenMessage()
    {
        var core = Core();
        core.RefuseAreaCalls = true;
        var page = new TerrainListPresenter(core, Token);
        Assert.Equal(TerrainListOutcome.Failed, await page.LoadAsync("a", null, null, null, null));
        Assert.Equal(TerrainLibrary.ForbiddenError, page.Error);
        Assert.Empty(page.Rows);
    }

    [Fact]
    public async Task List_CoreUnavailable_ShowsAFixedMessage()
    {
        var page = new TerrainListPresenter(new FakeAdminClient { Unavailable = true }, Token);
        Assert.Equal(TerrainListOutcome.Failed, await page.LoadAsync(null, null, null, null, null));
        Assert.Equal(TerrainLibrary.UnavailableError, page.Error);
    }

    [Fact]
    public void ListUrl_RoundTrips_TheAreaTerrainTabAndFilters_InTheQueryString()
    {
        Assert.Equal(WebRoutes.Terrain + "?tab=fragment", TerrainListPresenter.ListUrl(null, null, null));
        Assert.Equal(WebRoutes.Terrain + "?area=a&terrain=a.grass&tab=feature&q=a%20b&tag=calm", TerrainListPresenter.ListUrl("a", "a.grass", "feature", " a b ", "calm"));
        Assert.Equal(WebRoutes.Terrain + "?tab=fragment", TerrainListPresenter.ListUrl(null, null, "evil&x=1"));
        // Choosing another area drops the terrain.
        Assert.Equal(WebRoutes.Terrain + "?area=b&tab=phrase", TerrainListPresenter.AreaUrl("b", "phrase"));
        // The address a presenter stands for gives the same presenter back.
        var key = TerrainListPresenter.StateKey("a", "a.grass", "phrase", "x", "calm");
        Assert.Equal(WebRoutes.Terrain + "?area=a&terrain=a.grass&tab=phrase&q=x&tag=calm", key);
    }

    [Fact]
    public async Task List_AddressOfALoadedPage_ReloadsTheSamePage()
    {
        var core = Core();
        var first = await List(core, "a", "a.grass", "phrase", "river", null);
        Assert.Equal(TerrainListPresenter.StateKey("a", "a.grass", "phrase", "river", null), first.CurrentKey);
        var back = await List(core, "a", "a.grass", "phrase", "river", null);
        Assert.Equal((first.Area, first.Terrain, first.Tab, first.TextFilter), (back.Area, back.Terrain, back.Tab, back.TextFilter));
        Assert.Equal(first.Rows, back.Rows);
    }

    // ---- terrain types ----

    [Fact]
    public async Task TypeCreate_SendsAreaDotLocal_WithNameAndDescription_AndSelectsTheNewOne()
    {
        var core = Core();
        var page = await List(core, "a");
        page.BeginNewType();
        page.FormLocal = " rock "; page.FormName = "Rock"; page.FormDescription = "Bare rock.";
        Assert.Equal(TerrainListOutcome.Ok, await page.SaveTypeAsync());
        Assert.Equal("Terrain type created.", page.Notice);
        Assert.Equal("a.rock", page.CreatedId);
        Assert.False(page.TypeFormOpen);
        var stored = core.TerrainTypes["a.rock"];
        Assert.Equal(("a", "Rock", "Bare rock."), (stored.Area, stored.Name, stored.Description));
        Assert.Contains("CreateTerrainType", core.TerrainTypeCalls);
    }

    [Fact]
    public async Task TypeCreate_Invalid_KeepsTheForm_AndShowsCoresFieldMessages()
    {
        var core = Core();
        core.TerrainTypeResult = AdminResult.Invalid;
        core.TerrainTypeErrors.Add(new FieldError { Field = "Name", Message = "Name is required." });
        var page = await List(core, "a");
        page.BeginNewType();
        page.FormLocal = "x";
        Assert.Equal(TerrainListOutcome.Failed, await page.SaveTypeAsync());
        Assert.True(page.TypeFormOpen);
        Assert.Equal(new[] { "Name: Name is required." }, page.Problems);
        Assert.Equal("x", page.FormLocal);
    }

    [Fact]
    public async Task TypeCreate_AlreadyExists_ShowsAMessage()
    {
        var core = Core();
        core.TerrainTypeResult = AdminResult.AlreadyExists;
        var page = await List(core, "a");
        page.BeginNewType(); page.FormLocal = "grass"; page.FormName = "Grass";
        Assert.Equal(TerrainListOutcome.Failed, await page.SaveTypeAsync());
        Assert.Contains("already exists", page.Error);
    }

    [Fact]
    public async Task TypeEdit_WritesTheChosenId_NotTheFormsLocalPart_AndChangesNameAndDescription()
    {
        var core = Core();
        var page = await List(core, "a", "a.grass");
        Assert.True(page.BeginEditType("a.grass"));
        Assert.Equal("grass", page.FormLocal);
        page.FormLocal = "other"; page.FormName = "Meadow"; page.FormDescription = "Soft.";
        Assert.Equal(TerrainListOutcome.Ok, await page.SaveTypeAsync());
        Assert.Equal("Terrain type saved.", page.Notice);
        Assert.Equal("Meadow", core.TerrainTypes["a.grass"].Name);
        Assert.False(core.TerrainTypes.ContainsKey("a.other"));
        Assert.False(page.BeginEditType("a.nope"));
    }

    [Fact]
    public async Task TypeDelete_Ok_RemovesIt()
    {
        var core = Core();
        var page = await List(core, "a");
        Assert.Equal(TerrainListOutcome.Ok, await page.DeleteTypeAsync("a.river"));
        Assert.Equal("Terrain type deleted.", page.Notice);
        Assert.False(core.TerrainTypes.ContainsKey("a.river"));
        Assert.Contains("DeleteTerrainType:a.river", core.TerrainTypeCalls);
    }

    [Fact]
    public async Task TypeDelete_Refused_ShowsCoresMessageVerbatim()
    {
        var core = Core();
        const string text = "This terrain type is still in use by 2 description fragments, 1 feature template and 1 neighbour phrase (as neighbour terrain). Move or delete them first.";
        core.TerrainTypeResult = AdminResult.Refused;
        core.TerrainTypeMessage = text;
        var page = await List(core, "a");
        Assert.Equal(TerrainListOutcome.Failed, await page.DeleteTypeAsync("a.grass"));
        Assert.Equal(text, page.Error);
        Assert.True(core.TerrainTypes.ContainsKey("a.grass"));
    }

    [Fact]
    public async Task TypeDelete_RefusedWithoutAMessage_UsesTheFixedSentence_AndOtherResults()
    {
        var core = Core();
        var page = await List(core, "a");
        core.TerrainTypeResult = AdminResult.Refused;
        await page.DeleteTypeAsync("a.grass");
        Assert.Equal(TerrainLibrary.RefusedError, page.Error);
        core.TerrainTypeResult = AdminResult.Forbidden;
        await page.DeleteTypeAsync("a.grass");
        Assert.Equal(TerrainLibrary.ForbiddenError, page.Error);
        core.TerrainTypeResult = AdminResult.NotFound;
        Assert.Equal(TerrainListOutcome.Ok, await page.DeleteTypeAsync("a.grass"));
        Assert.Equal("That terrain type does not exist.", page.Notice);
    }

    [Fact]
    public async Task TypeCreate_SuggestsLegendNamesOfTheAreasMap_NotYetATerrainType()
    {
        var core = Core();
        core.TerrainColors.Add(new TerrainColor { Map = "a", Terrain = "grass", Color = "#00ff00" });   // already a type
        core.TerrainColors.Add(new TerrainColor { Map = "a", Terrain = "swamp", Color = "#003300" });
        core.TerrainColors.Add(new TerrainColor { Map = "b", Terrain = "dune", Color = "#ffff00" });    // another map
        var page = await List(core, "a");
        Assert.Equal(new[] { "swamp" }, page.Suggestions);
    }

    [Fact]
    public async Task EntryDelete_FromTheChosenTerrain_StillWorks()
    {
        var core = Core();
        var page = await List(core, "a", "a.grass");
        Assert.Equal(TerrainListOutcome.Ok, await page.DeleteAsync("fragment", "g1"));
        Assert.Equal("Deleted.", page.Notice);
        Assert.False(core.Fragments.ContainsKey("g1"));
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
    public async Task Edit_New_Fragment_OffersEveryArea_AndNoTerrainsUntilAnAreaIsChosen()
    {
        var page = await Edit(Core(), "fragment", null);
        Assert.True(page.IsNew);
        Assert.False(page.IsMissing);
        Assert.Equal(new[] { "a", "b", "c" }, page.AreaChoices);
        Assert.Null(page.Area);
        Assert.Empty(page.TerrainChoices);
        Assert.Equal("A new template starts at version 1.", page.VersionHint);
    }

    [Fact]
    public async Task Edit_New_FromAListPage_IsPreFilledWithAreaAndTerrain()
    {
        var page = await Edit(Core(), "fragment", null, "a", "a.grass");
        Assert.Equal(("a", "a.grass"), (page.Area, page.Terrain));
        Assert.Equal(new[] { "a.forest", "a.grass", "a.river" }, page.TerrainChoices.Select(t => t.Id));
        // Only an area: the first terrain type of it. A terrain of another area is ignored.
        var area = await Edit(Core(), "fragment", null, "b", "a.grass");
        Assert.Equal(("b", "b.sand"), (area.Area, area.Terrain));
        // An unknown area pre-fills nothing.
        Assert.Null((await Edit(Core(), "fragment", null, "zzz", "zzz.x")).Area);
        // Existing entries ignore the pre-fill.
        var existing = await Edit(Core(), "fragment", "g1", "b", "b.sand");
        Assert.Equal(("a", "a.grass"), (existing.Area, existing.Terrain));
    }

    [Fact]
    public async Task Edit_ChoosingAnotherArea_MovesToItsFirstTerrain_AndAnEntryCanMoveBetweenAreasAndTerrains()
    {
        var core = Core();
        var page = await Edit(core, "fragment", "g1");
        Assert.Equal(("a", "a.grass"), (page.Area, page.Terrain));
        page.ChooseTerrain("a.river");
        Assert.Equal(TerrainEditOutcome.Ok, await page.SaveAsync());
        Assert.Equal("a.river", core.Fragments["g1"].Terrain);
        page.ChooseArea("b");
        Assert.Equal("b.sand", page.Terrain);
        Assert.Equal(new[] { "b.sand" }, page.TerrainChoices.Select(t => t.Id));
        Assert.Equal(TerrainEditOutcome.Ok, await page.SaveAsync());
        Assert.Equal("b.sand", core.Fragments["g1"].Terrain);
        page.ChooseArea("c");
        Assert.Null(page.Terrain);
        Assert.Equal(TerrainEditOutcome.Invalid, await page.SaveAsync());
        Assert.Equal(new[] { TerrainEditPresenter.TerrainRequired }, page.Problems);
        Assert.Equal("b.sand", core.Fragments["g1"].Terrain);
    }

    [Fact]
    public async Task Edit_Phrase_NeighbourChoices_AreTheSameAreasTypes_AndFollowTheArea()
    {
        var core = Core();
        var page = await Edit(core, "phrase", "p1");
        Assert.Equal(new[] { "a.forest", "a.grass", "a.river" }, page.NeighborChoices.Select(t => t.Id));
        Assert.DoesNotContain(page.NeighborChoices, t => t.Id == "b.sand");
        Assert.False(page.NeighborIsMissing);
        // Moving to another area drops a neighbour that is not in it; saving then needs a new one.
        page.ChooseArea("b");
        Assert.Null(page.NeighborTerrain);
        Assert.Equal(new[] { "b.sand" }, page.NeighborChoices.Select(t => t.Id));
        Assert.Equal(TerrainEditOutcome.Invalid, await page.SaveAsync());
        Assert.Equal(new[] { TerrainEditPresenter.NeighborRequired }, page.Problems);
        page.NeighborTerrain = "b.sand";
        Assert.Equal(TerrainEditOutcome.Ok, await page.SaveAsync());
        Assert.Equal(("b.sand", "b.sand"), (core.NeighborPhrases["p1"].Terrain, core.NeighborPhrases["p1"].NeighborTerrain));
    }

    [Fact]
    public async Task Edit_Phrase_ANeighbourOutsideTheArea_IsNeverSent()
    {
        var core = Core();
        var page = await Edit(core, "phrase", "p1");
        page.NeighborTerrain = "b.sand";
        Assert.True(page.NeighborIsMissing);
        Assert.Equal(TerrainEditOutcome.Invalid, await page.SaveAsync());
        Assert.DoesNotContain("UpdateNeighborPhrase", core.LibraryCalls);
        Assert.Equal("a.river", core.NeighborPhrases["p1"].NeighborTerrain);
    }

    [Fact]
    public async Task Edit_AnEntryWhoseTerrainTypeIsGone_Loads_ShowsAsMissing_AndNeedsAValidChoiceToSave()
    {
        var core = Core();
        var stray = new FragmentRecord { Id = "stray", Slot = "opening", Text = "Gone.", Weight = 1, Terrain = "a.vanished" };
        core.Fragments["stray"] = stray;
        var page = await Edit(core, "fragment", "stray");
        Assert.False(page.IsMissing);
        Assert.Equal("a.vanished", page.Terrain);
        Assert.Equal("a", page.Area);                       // taken from the id
        Assert.True(page.TerrainIsMissing);
        Assert.Equal("(missing: a.vanished)", TerrainEditPresenter.MissingLabel(page.Terrain!));
        Assert.DoesNotContain(page.TerrainChoices, t => t.Id == "a.vanished");
        Assert.Equal(TerrainEditOutcome.Invalid, await page.SaveAsync());
        Assert.Equal(new[] { TerrainEditPresenter.TerrainRequired }, page.Problems);
        Assert.Equal("a.vanished", core.Fragments["stray"].Terrain);   // nothing written
        page.ChooseTerrain("a.grass");
        Assert.False(page.TerrainIsMissing);
        Assert.Equal(TerrainEditOutcome.Ok, await page.SaveAsync());
        Assert.Equal("a.grass", core.Fragments["stray"].Terrain);
    }

    [Fact]
    public async Task Edit_BackUrl_PointsAtTheListOfTheEntrysAreaAndTerrain()
    {
        var page = await Edit(Core(), "phrase", "p1");
        Assert.Equal(WebRoutes.Terrain + "?area=a&terrain=a.grass&tab=phrase", page.BackUrl());
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
        var page = await Edit(core, "fragment", null, "a", "a.grass");
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
        var page = await Edit(core, "phrase", null, "a", "a.grass");
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
        var page = await Edit(core, "phrase", null, "a", "a.grass");
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
        var page = await Edit(core, "fragment", null, "a", "a.grass");
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
        var page = await Edit(core, "fragment", null, "a", "a.grass");
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

    [Fact]
    public void TerrainEdit_HasNoDetailField_TheLongFieldIsTheLookText_AndAnOldDraftWithDetailIsIgnored()
    {
        var src = Read("MUD.Web/Components/Pages/TerrainEdit.razor");
        Assert.DoesNotContain("te-detail", src);
        Assert.DoesNotContain("Detail", src);
        Assert.Contains("Look text (shown when a player looks at it)", src);
        var old = "{\"Short\":\"a rock\",\"Long\":\"Look.\",\"RoomLine\":\"r\",\"Detail\":\"old detail\",\"Weight\":2}";
        var draft = System.Text.Json.JsonSerializer.Deserialize<Components.Pages.TerrainEdit.TerrainDraft>(old);
        Assert.NotNull(draft);
        Assert.Equal("Look.", draft!.Long);
        Assert.DoesNotContain("old detail", System.Text.Json.JsonSerializer.Serialize(draft));
    }

    [Fact]
    public void TheVersionHintNoLongerNamesDetail()
    {
        Assert.DoesNotContain("detail", new TerrainEditPresenter(new FakeAdminClient(), Token, TerrainLibrary.Feature, "x").VersionHint, StringComparison.OrdinalIgnoreCase);
    }

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
        foreach (var (prop, name) in new[] { ("Area", "area"), ("Terrain", "terrain"), ("Tab", "tab"), ("TextFilter", "q"), ("TagFilter", "tag") })
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
