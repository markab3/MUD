using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Authorization;
using MUD.Contracts;
using MUD.Web.Pages;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

// The terrain library pages over a fake of core's Admin service. Page models are built WITHOUT TempData (as in the room page tests), so the
// GET handlers must read it null-safely; the redirecting handlers that write TempData get a throw-away dictionary.
public class TerrainPagesTests
{
    private static DefaultHttpContext Http()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "Someone"), new Claim(ClaimTypes.Role, "Creator") }, "test"));
        var http = new DefaultHttpContext { User = user };
        http.Request.Headers.Cookie = SessionAuthentication.CookieName + "=token-1";
        return http;
    }

    private static TerrainListModel List(FakeAdminClient core) =>
        new(core) { PageContext = new PageContext { HttpContext = Http() } };

    private static TerrainEditModel Edit(FakeAdminClient core) =>
        new(core) { PageContext = new PageContext { HttpContext = Http() } };

    private static FakeAdminClient Core()
    {
        var core = new FakeAdminClient();
        var f1 = new FragmentRecord { Id = "g1", Slot = "opening", Text = "Grass.", Weight = 2 };
        f1.Terrains.Add("grass"); f1.Tags.Add("calm");
        var f2 = new FragmentRecord { Id = "orphan", Slot = "opening", Text = "Nothing.", Weight = 1 };
        var t1 = new FeatureTemplateRecord { Id = "t1", Short = "a rock", Long = "A rock.", Weight = 1, Chance = 0.5, Version = 3 };
        t1.Terrains.Add("forest");
        var p1 = new NeighborPhraseRecord { Id = "p1", NeighborTerrain = "river", Text = "A river flows {dir} of here.", Weight = 1 };
        p1.Terrains.Add("grass");
        core.Fragments["g1"] = f1; core.Fragments["orphan"] = f2;
        core.FeatureTemplates["t1"] = t1; core.NeighborPhrases["p1"] = p1;
        return core;
    }

    // ---- list ----

    [Fact]
    public async Task List_WithoutTempData_ShowsFragmentsByDefault_AndKnownTerrains()
    {
        var core = Core();
        var page = List(core);
        Assert.IsType<PageResult>(await page.OnGetAsync(null, null, null));
        Assert.Equal(new[] { "g1", "orphan" }, page.Rows.Select(r => r.Id));
        Assert.Equal(new[] { "forest", "grass", "river" }, page.KnownTerrains);
        Assert.Equal(new[] { "token-1" }, core.LibraryTokens.Distinct());
        Assert.Equal(1, page.UnassignedCount);
    }

    [Fact]
    public async Task List_UnassignedTab_ShowsOnlyEntitiesWithEmptyTerrains()
    {
        var page = List(Core());
        await page.OnGetAsync("unassigned", null, null);
        Assert.Equal(new[] { "orphan" }, page.Rows.Select(r => r.Id));
        Assert.Equal(1, page.UnassignedCount);
        Assert.Empty(page.Rows[0].Terrains);
    }

    [Fact]
    public async Task List_FiltersByTerrainAndTag()
    {
        var page = List(Core());
        await page.OnGetAsync("fragment", "grass", null);
        Assert.Equal(new[] { "g1" }, page.Rows.Select(r => r.Id));
        page = List(Core());
        await page.OnGetAsync("fragment", null, "calm");
        Assert.Equal(new[] { "g1" }, page.Rows.Select(r => r.Id));
        page = List(Core());
        await page.OnGetAsync("fragment", "forest", null);
        Assert.Empty(page.Rows);
    }

    [Fact]
    public async Task List_FeatureAndPhraseTabs_ShowTheirKind()
    {
        var page = List(Core());
        await page.OnGetAsync("feature", null, null);
        Assert.Equal(new[] { "t1" }, page.Rows.Select(r => r.Id));
        Assert.Contains("version 3", page.Rows[0].Details);
        page = List(Core());
        await page.OnGetAsync("phrase", null, null);
        Assert.Equal(new[] { "p1" }, page.Rows.Select(r => r.Id));
        Assert.Contains("A river flows north of here.", page.Rows[0].Details);
    }

    [Fact]
    public async Task List_CoreRefusedSession_Is403()
    {
        var core = Core();
        core.RefuseLibraryCalls = true;
        var page = List(core);
        Assert.IsType<PageResult>(await page.OnGetAsync(null, null, null));
        Assert.Equal(StatusCodes.Status403Forbidden, page.Response.StatusCode);
        Assert.Equal(TerrainLibrary.ForbiddenError, page.Error);
    }

    [Theory]
    [InlineData("fragment", "g1")]
    [InlineData("feature", "t1")]
    [InlineData("phrase", "p1")]
    public async Task Delete_Ok_RedirectsWithNotice_AndRemoves(string kind, string id)
    {
        var core = Core();
        var page = List(core);
        page.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(page.HttpContext, new MemoryTempData());
        var result = await page.OnPostDeleteAsync(kind, id, kind);
        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("Deleted.", (string)page.TempData["Notice"]!);
        Assert.False(core.Fragments.ContainsKey(id) || core.FeatureTemplates.ContainsKey(id) || core.NeighborPhrases.ContainsKey(id));
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
        var page = List(core);
        page.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(page.HttpContext, new MemoryTempData());
        Assert.IsType<RedirectToPageResult>(await page.OnPostDeleteAsync("fragment", "g1", null));
        Assert.Equal(expected, (string)page.TempData["Notice"]!);
        Assert.True(core.Fragments.ContainsKey("g1"));
    }

    [Fact]
    public async Task Delete_UnknownKind_IsRefusedWithoutACall()
    {
        var core = Core();
        var page = List(core);
        Assert.IsType<PageResult>(await page.OnPostDeleteAsync("bogus", "g1", null));
        Assert.Equal(StatusCodes.Status400BadRequest, page.Response.StatusCode);
        Assert.DoesNotContain("DeleteFragment", core.LibraryCalls);
    }

    [Fact]
    public async Task Delete_CoreRefusedSession_Is403()
    {
        var core = Core();
        core.RefuseLibraryCalls = true;
        var page = List(core);
        await page.OnPostDeleteAsync("fragment", "g1", null);
        Assert.Equal(StatusCodes.Status403Forbidden, page.Response.StatusCode);
    }

    // ---- edit: load ----

    [Fact]
    public async Task Edit_New_Fragment_ListsKnownTerrains_AndWarnsWhenNoneTicked()
    {
        var page = Edit(Core());
        Assert.IsType<PageResult>(await page.OnGetAsync("fragment", null));
        Assert.True(page.IsNew);
        Assert.Equal(new[] { "forest", "grass", "river" }, page.TerrainChoices);
        Assert.True(page.Unused);
    }

    [Fact]
    public async Task Edit_Existing_LoadsFields_AndNoWarningWhenAssigned()
    {
        var page = Edit(Core());
        await page.OnGetAsync("feature", "t1");
        Assert.Equal("a rock", page.Short);
        Assert.Equal(3, page.Version);
        Assert.False(page.Unused);
        Assert.Contains("from 3 to 4", page.VersionHint);
    }

    [Fact]
    public async Task Edit_Existing_WithEmptyTerrains_ShowsUnusedWarning()
    {
        var page = Edit(Core());
        await page.OnGetAsync("fragment", "orphan");
        Assert.True(page.Unused);
    }

    [Fact]
    public async Task Edit_UnknownKindOrId_Is404()
    {
        var page = Edit(Core());
        await page.OnGetAsync("bogus", null);
        Assert.Equal(StatusCodes.Status404NotFound, page.Response.StatusCode);
        Assert.True(page.Missing);
        page = Edit(Core());
        await page.OnGetAsync("phrase", "nope");
        Assert.Equal(StatusCodes.Status404NotFound, page.Response.StatusCode);
    }

    // ---- edit: save ----

    [Fact]
    public async Task Save_NewFragment_SendsParsedLists_AndRedirects()
    {
        var core = Core();
        var page = Edit(core);
        page.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(page.HttpContext, new MemoryTempData());
        page.Id = " new1 "; page.Slot = "opening"; page.Text = "Text."; page.Weight = 4; page.Tags = "a, b  c";
        page.Terrains = new List<string> { "grass" }; page.ExtraTerrains = "swamp, grass";
        var result = await page.OnPostSaveAsync("fragment", null);
        Assert.IsType<RedirectResult>(result);
        var saved = core.Fragments["new1"];
        Assert.Equal(new[] { "grass", "swamp" }, saved.Terrains);
        Assert.Equal(new[] { "a", "b", "c" }, saved.Tags);
        Assert.Equal(4, saved.Weight);
        Assert.Contains("CreateFragment", core.LibraryCalls);
    }

    [Fact]
    public async Task Save_EmptyTerrains_IsSentEmpty()
    {
        var core = Core();
        var page = Edit(core);
        page.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(page.HttpContext, new MemoryTempData());
        page.Id = "x"; page.NeighborTerrain = "river"; page.Text = "{dir}";
        await page.OnPostSaveAsync("phrase", null);
        Assert.Empty(core.NeighborPhrases["x"].Terrains);
    }

    [Fact]
    public async Task Save_Edit_UsesQueryId_NotTheFormId_AndUpdates()
    {
        var core = Core();
        var page = Edit(core);
        page.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(page.HttpContext, new MemoryTempData());
        page.Id = "other"; page.Short = "a big rock"; page.Long = "A rock."; page.Chance = 0.5; page.Weight = 1; page.Terrains = new List<string> { "forest" };
        await page.OnPostSaveAsync("feature", "t1");
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
        var page = Edit(core);
        page.Id = "x"; page.Text = "no placeholder"; page.NeighborTerrain = "river";
        var result = await page.OnPostSaveAsync("phrase", null);
        Assert.IsType<PageResult>(result);
        Assert.Equal("no placeholder", page.Text);
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
        var page = Edit(core);
        page.Id = "x"; page.Slot = "s"; page.Text = "t";
        Assert.IsType<PageResult>(await page.OnPostSaveAsync("fragment", null));
        Assert.Equal(expected, page.Error);
    }

    [Fact]
    public async Task Save_NotFound_Is404()
    {
        var core = Core();
        core.LibraryResult = AdminResult.NotFound;
        var page = Edit(core);
        await page.OnPostSaveAsync("fragment", "g1");
        Assert.Equal(StatusCodes.Status404NotFound, page.Response.StatusCode);
    }

    [Fact]
    public async Task Save_CoreRefusedSession_Is403()
    {
        var core = Core();
        core.RefuseLibraryCalls = true;
        var page = Edit(core);
        page.Id = "x";
        await page.OnPostSaveAsync("fragment", null);
        Assert.Equal(StatusCodes.Status403Forbidden, page.Response.StatusCode);
    }

    // ---- shape ----

    [Fact]
    public void Pages_AreCreatorOrAdminOnly()
    {
        foreach (var t in new[] { typeof(TerrainListModel), typeof(TerrainEditModel) })
        {
            var attr = (AuthorizeAttribute?)Attribute.GetCustomAttribute(t, typeof(AuthorizeAttribute));
            Assert.NotNull(attr);
            Assert.Equal(WebPolicies.CreatorOrAdmin, attr!.Policy);
        }
    }

    [Fact]
    public void EditModel_BoundProperties_AreExactlyTheFormFields_NoKindOrVersion()
    {
        var bound = typeof(TerrainEditModel).GetProperties()
            .Where(p => p.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.BindPropertyAttribute), true).Any()).Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Adjectives", "Chance", "Detail", "ExtraTerrains", "Handles", "Id", "Long", "Merge", "NeighborTerrain", "RoomLine", "Short", "Slot", "Tags", "Terrains", "Text", "Weight" }, bound);
        Assert.DoesNotContain("Kind", bound);
        Assert.DoesNotContain("Version", bound);
    }

    [Fact]
    public void ListModel_BindsNothing()
    {
        Assert.DoesNotContain(typeof(TerrainListModel).GetProperties(), p => p.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.BindPropertyAttribute), true).Any());
    }

    [Fact]
    public void Split_And_Preview_Helpers()
    {
        Assert.Equal(new[] { "a", "b" }, TerrainLibrary.Split(" a,,b a "));
        Assert.Equal("A river flows north of here.", TerrainLibrary.PhrasePreview("A river flows {dir} of here."));
    }

    private class MemoryTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
