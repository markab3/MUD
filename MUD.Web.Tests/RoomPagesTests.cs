using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MUD.Contracts;
using MUD.Web.Pages;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

public class RoomCreationServiceTests
{
    private static RoomForm Form(string key = "new", string area = "t") => new()
    {
        Key = key, Short = "a new room", Long = "Long.", Area = area,
        Exits = { new ExitInput { Dir = "north", To = "t.hall" }, new ExitInput() },
    };

    [Fact]
    public async Task Create_SendsTheFullKeyAndTheFormToCore_WithTheToken()
    {
        var core = new FakeAdminClient();
        var result = await new RoomCreationService(core).CreateAsync(Form(), "token-1");
        Assert.Equal(RoomCreationStatus.Created, result.Status);
        Assert.Equal("t.new", result.CreatedKey);
        var sent = Assert.Single(core.RoomCreates);
        Assert.Equal("t.new", sent.Key);
        Assert.Equal("t", sent.Area);
        Assert.Equal("a new room", sent.ShortDescription);
        Assert.Equal("Long.", sent.LongDescription);
        var exit = Assert.Single(sent.Exits);
        Assert.Equal(("north", "t.hall"), (exit.Name, exit.To));
        Assert.Equal(new[] { "token-1" }, core.RoomTokens);
    }

    [Fact]
    public async Task Create_BlankCoordinates_AreSentAsAbsent_NotZero()
    {
        var core = new FakeAdminClient();
        await new RoomCreationService(core).CreateAsync(Form(), "token-1");
        var sent = Assert.Single(core.RoomCreates);
        Assert.False(sent.HasX);
        Assert.False(sent.HasY);
        Assert.False(sent.HasZ);

        var placed = Form();
        placed.X = 0; placed.Y = 3; placed.Z = -1;
        await new RoomCreationService(core).CreateAsync(placed, "token-1");
        var second = core.RoomCreates[1];
        Assert.Equal((0, 3, -1), (second.X, second.Y, second.Z));
        Assert.True(second.HasX);
    }

    [Fact]
    public async Task Create_ExistingKey_IsReportedAsKeyExists()
    {
        var core = new FakeAdminClient { RoomEditResult = AdminResult.AlreadyExists };
        Assert.Equal(RoomCreationStatus.KeyExists, (await new RoomCreationService(core).CreateAsync(Form("hall"), "token-1")).Status);
    }

    [Fact]
    public async Task Create_InvalidReply_CarriesCoreFieldErrors()
    {
        var core = new FakeAdminClient { RoomEditResult = AdminResult.Invalid };
        core.RoomEditErrors.Add(new FieldError { Field = "Short", Message = "Too long." });
        var result = await new RoomCreationService(core).CreateAsync(Form(), "token-1");
        Assert.Equal(RoomCreationStatus.Invalid, result.Status);
        Assert.Contains(result.FieldErrors, e => e.Field == "Short" && e.Message == "Too long.");
    }

    [Fact]
    public async Task Create_ForbiddenResultOrRefusedSession_IsForbidden()
    {
        Assert.Equal(RoomCreationStatus.Forbidden,
            (await new RoomCreationService(new FakeAdminClient { RoomEditResult = AdminResult.Forbidden }).CreateAsync(Form(), "t")).Status);
        Assert.Equal(RoomCreationStatus.Forbidden,
            (await new RoomCreationService(new FakeAdminClient { RefuseRoomCalls = true }).CreateAsync(Form(), "t")).Status);
    }

    [Theory]
    [InlineData(AdminResult.AuditFailed)]
    [InlineData(AdminResult.Failed)]
    [InlineData(AdminResult.Refused)]
    public async Task Create_OtherRefusals_AreFailed(AdminResult result)
    {
        var core = new FakeAdminClient { RoomEditResult = result };
        Assert.Equal(RoomCreationStatus.Failed, (await new RoomCreationService(core).CreateAsync(Form(), "token-1")).Status);
    }

    [Fact]
    public async Task Create_InvalidLocalKey_IsRejectedHere_AndCoreIsNotCalled()
    {
        var core = new FakeAdminClient();
        var result = await new RoomCreationService(core).CreateAsync(Form("Bad Key"), "token-1");
        Assert.Equal(RoomCreationStatus.Invalid, result.Status);
        Assert.Contains(result.FieldErrors, e => e.Field == "Key");
        Assert.Empty(core.RoomCreates);
    }

    [Fact]
    public async Task Create_WhenCoreIsUnavailable_Throws()
    {
        await Assert.ThrowsAsync<CoreUnavailableException>(() =>
            new RoomCreationService(new FakeAdminClient { Unavailable = true }).CreateAsync(Form(), "token-1"));
    }
}

public class RoomValidatorParityTests
{
    // The importer, core and the website form all use MUD.Data.RoomRules; this table pins the verdicts so a change in one place
    // shows up in all (the importer side is covered by RoomRulesCharacterizationTests in MUD.Tests).
    [Theory]
    [InlineData("hall", true)]
    [InlineData("hall_2", true)]
    [InlineData("Hall", false)]
    [InlineData("a.b", false)]
    [InlineData("", false)]
    public void LocalKey(string key, bool ok) => Assert.Equal(ok, MUD.Data.RoomRules.CheckLocalKey(key) == null);

    [Theory]
    [InlineData("t", true)]
    [InlineData("a.b.c", true)]
    [InlineData("T", false)]
    [InlineData("a..b", false)]
    public void AreaPrefix(string prefix, bool ok) => Assert.Equal(ok, MUD.Data.RoomRules.CheckAreaPrefix(prefix) == null);

    [Fact]
    public void WebPagesDoNotReferenceMudCore()
    {
        var referenced = typeof(RoomEditModel).Assembly.GetReferencedAssemblies().Select(a => a.Name);
        Assert.DoesNotContain("MUD.Core", referenced);
    }
}

public class RoomEditPageTests
{
    private const string Token = "token-1";

    private static (RoomEditModel Page, FakeAdminClient Core) Page(FakeAdminClient? core = null)
    {
        core ??= new FakeAdminClient().AddRoom("t.start").AddRoom("t.hall", "t", 1, 1, 0, ("north", "t.start"));
        var model = new RoomEditModel(core);
        var http = new DefaultHttpContext();
        http.Request.Headers.Cookie = SessionAuthentication.CookieName + "=" + Token;
        http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "Bob"), new Claim(ClaimTypes.Role, "Creator") }, "test"));
        model.PageContext = new PageContext { HttpContext = http };
        return (model, core);
    }

    [Fact]
    public void Page_RequiresCreatorOrAdminPolicy()
    {
        var attr = (AuthorizeAttribute?)Attribute.GetCustomAttribute(typeof(RoomEditModel), typeof(AuthorizeAttribute));
        Assert.NotNull(attr);
        Assert.Equal(WebPolicies.CreatorOrAdmin, attr!.Policy);
    }

    [Fact]
    public void Page_RouteIsCreatorRoomsKey()
    {
        var cshtml = File.ReadAllText(FindFile("MUD.Web/Pages/RoomEdit.cshtml"));
        Assert.StartsWith("@page \"/creator/rooms/{key}\"", cshtml);
    }

    [Fact]
    public void Page_FormsHaveAntiforgery()
    {
        var cshtml = File.ReadAllText(FindFile("MUD.Web/Pages/RoomEdit.cshtml"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(cshtml, "<form method=\"post\"").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(cshtml, "Html.AntiForgeryToken\\(\\)").Count);
    }

    [Fact]
    public void Page_EditModelDoesNotBindTheKey_OrAnyStateOrPasswordField_AndHasNoRevision()
    {
        var bound = typeof(RoomEditModel).GetProperties()
            .Where(p => p.GetCustomAttributes(typeof(BindPropertyAttribute), true).Any()).Select(p => p.Name).ToHashSet();
        Assert.DoesNotContain("Key", bound);
        Assert.DoesNotContain("Items", bound);
        Assert.DoesNotContain("Features", bound);
        Assert.DoesNotContain("Password", bound);
        Assert.DoesNotContain("Rev", bound); // D30: no optimistic concurrency
        Assert.Equal(new[] { "Area", "ConfirmDangling", "Exits", "Long", "Short", "X", "Y", "Z" }, bound.OrderBy(b => b).ToArray());
    }

    [Fact]
    public void Page_Source_HasNoRevisionOrConflictLeftOver()
    {
        var cshtml = File.ReadAllText(FindFile("MUD.Web/Pages/RoomEdit.cshtml"));
        Assert.DoesNotContain("Rev", cshtml);
        Assert.DoesNotContain("Conflict", cshtml);
    }

    [Fact]
    public async Task Get_UnknownRoom_Is404()
    {
        var (model, _) = Page();
        var result = await model.OnGetAsync("t.nope");
        Assert.IsType<PageResult>(result);
        Assert.Equal(404, model.Response.StatusCode);
        Assert.True(model.Missing);
    }

    [Fact]
    public async Task Get_LoadsValues_AndSendsTheCookieToken()
    {
        var (model, core) = Page();
        await model.OnGetAsync("t.hall");
        Assert.Equal("short t.hall", model.Short);
        Assert.Equal("long t.hall", model.Long);
        Assert.Equal(1, model.X);
        Assert.Single(model.Exits);
        Assert.Equal(new[] { Token }, core.RoomTokens);
    }

    [Fact]
    public async Task Get_UnplacedRoom_LoadsBlankCoordinates()
    {
        var (model, _) = Page(new FakeAdminClient().AddRoom("t.hall", "t", null, null, null));
        await model.OnGetAsync("t.hall");
        Assert.Null(model.X);
        Assert.Null(model.Y);
        Assert.Null(model.Z);
    }

    [Fact]
    public async Task Get_WhenCoreIsUnavailable_Shows503AndAFixedMessage()
    {
        var (model, _) = Page(new FakeAdminClient { Unavailable = true });
        await model.OnGetAsync("t.hall");
        Assert.Equal(503, model.Response.StatusCode);
        Assert.Equal(RoomEditModel.UnavailableError, model.Error);
    }

    [Fact]
    public async Task Get_WhenCoreRefusesTheSession_Shows403()
    {
        var (model, _) = Page(new FakeAdminClient { RefuseRoomCalls = true });
        await model.OnGetAsync("t.hall");
        Assert.Equal(403, model.Response.StatusCode);
        Assert.Equal(RoomEditModel.ForbiddenError, model.Error);
    }

    [Fact]
    public async Task Save_Success_SendsTheWholeForm_ReloadsFromCoreReply_AndShowsNotice()
    {
        var (model, core) = Page();
        await model.OnGetAsync("t.hall");
        model.Short = "edited";
        await model.OnPostSaveAsync("t.hall");
        var sent = Assert.Single(core.RoomUpdates);
        Assert.Equal("t.hall", sent.Key);
        Assert.Equal("edited", sent.ShortDescription);
        Assert.Equal("long t.hall", sent.LongDescription);
        Assert.Equal(1, sent.X);
        Assert.Equal(new[] { Token }, core.RoomTokens.Distinct());
        Assert.Equal("edited", model.Short);
        Assert.NotNull(model.Notice);
        Assert.Null(model.Error);
    }

    [Fact]
    public async Task Save_BlankCoordinates_AreSentAsAbsent()
    {
        var (model, core) = Page(new FakeAdminClient().AddRoom("t.hall", "t", 1, 1, 0));
        await model.OnGetAsync("t.hall");
        model.X = null; model.Y = null; model.Z = null;
        await model.OnPostSaveAsync("t.hall");
        var sent = Assert.Single(core.RoomUpdates);
        Assert.False(sent.HasX);
        Assert.False(sent.HasY);
        Assert.False(sent.HasZ);
        Assert.Null(model.X);
    }

    [Fact]
    public async Task Save_TwoEditsInARow_BothApply_LastWriteWins()
    {
        var (model, core) = Page();
        await model.OnGetAsync("t.hall");
        model.Short = "first";
        await model.OnPostSaveAsync("t.hall");
        model.Short = "second";
        await model.OnPostSaveAsync("t.hall");
        Assert.Equal(2, core.RoomUpdates.Count);
        Assert.Equal("second", core.Rooms["t.hall"].ShortDescription);
        Assert.Null(model.Error);
    }

    [Fact]
    public async Task Save_NoChange_ShowsNotice()
    {
        var (model, core) = Page(new FakeAdminClient { RoomEditResult = AdminResult.NoChange }.AddRoom("t.hall"));
        await model.OnGetAsync("t.hall");
        await model.OnPostSaveAsync("t.hall");
        Assert.Equal("Nothing was changed.", model.Notice);
    }

    [Fact]
    public async Task Save_Invalid_ShowsCoreFieldErrors_AndKeepsTheUsersInput()
    {
        var core = new FakeAdminClient { RoomEditResult = AdminResult.Invalid }.AddRoom("t.hall");
        core.RoomEditErrors.Add(new FieldError { Field = "Short", Message = "Short is too long." });
        var (model, _) = Page(core);
        model.Short = "my input";
        await model.OnPostSaveAsync("t.hall");
        Assert.Contains(model.ModelState[string.Empty]!.Errors, e => e.ErrorMessage == "Short is too long.");
        Assert.Equal("my input", model.Short);
    }

    [Theory]
    [InlineData(AdminResult.AuditFailed, RoomEditModel.AuditFailedError)]
    [InlineData(AdminResult.Forbidden, RoomEditModel.ForbiddenError)]
    [InlineData(AdminResult.Failed, RoomEditModel.FailedError)]
    public async Task Save_OtherResults_ShowFixedMessages(AdminResult result, string expected)
    {
        var (model, _) = Page(new FakeAdminClient { RoomEditResult = result }.AddRoom("t.hall"));
        await model.OnPostSaveAsync("t.hall");
        Assert.Equal(expected, model.Error);
    }

    [Fact]
    public async Task Save_NotFound_Is404()
    {
        var (model, _) = Page(new FakeAdminClient { RoomEditResult = AdminResult.NotFound });
        await model.OnPostSaveAsync("t.gone");
        Assert.Equal(404, model.Response.StatusCode);
        Assert.True(model.Missing);
    }

    [Fact]
    public async Task Save_WhenCoreRefusesTheRole_ShowsForbidden_AndNothingIsReloaded()
    {
        var (model, core) = Page(new FakeAdminClient { RefuseRoomCalls = true }.AddRoom("t.hall"));
        model.Short = "x";
        await model.OnPostSaveAsync("t.hall");
        Assert.Equal(RoomEditModel.ForbiddenError, model.Error);
        Assert.Empty(core.RoomUpdates);
    }

    [Fact]
    public async Task Save_WhenCoreIsUnavailable_ShowsAFixedMessage()
    {
        var (model, _) = Page(new FakeAdminClient { Unavailable = true });
        await model.OnPostSaveAsync("t.hall");
        Assert.Equal(RoomEditModel.UnavailableError, model.Error);
    }

    [Fact]
    public async Task Save_FormCannotRedirectTheSaveToAnotherRoom()
    {
        var (model, core) = Page(new FakeAdminClient().AddRoom("t.hall").AddRoom("t.other", "t", 2, 2, 0));
        model.Short = "x"; model.Long = "y"; model.Area = "t";
        await model.OnPostSaveAsync("t.hall");
        Assert.All(core.RoomUpdates, u => Assert.Equal("t.hall", u.Key));
        Assert.Equal("short t.other", core.Rooms["t.other"].ShortDescription);
    }

    [Fact]
    public async Task Save_MoreExitRowsThanTheLimit_AreCut()
    {
        var (model, core) = Page(new FakeAdminClient().AddRoom("t.hall"));
        model.Exits = Enumerable.Range(0, RoomEditModel.MaxFormExits + 5).Select(i => new ExitInput { Dir = "d" + i, To = "t.x" }).ToList();
        await model.OnPostSaveAsync("t.hall");
        Assert.Equal(RoomEditModel.MaxFormExits, Assert.Single(core.RoomUpdates).Exits.Count);
    }

    [Fact]
    public void ErrorMessages_AreFixedStrings_WithNoPlaceholdersForInput()
    {
        foreach (var s in new[] { RoomEditModel.NotFoundError, RoomEditModel.FailedError, RoomEditModel.AuditFailedError, RoomEditModel.ForbiddenError, RoomEditModel.UnavailableError })
        {
            Assert.DoesNotContain("{", s);
        }
    }

    [Fact]
    public async Task AddExit_And_RemoveExit_ChangeTheFormOnly_NoWrite()
    {
        var (model, core) = Page(new FakeAdminClient().AddRoom("t.hall", "t", 1, 1, 0, ("north", "t.start")));
        await model.OnGetAsync("t.hall");
        await model.OnPostAddExitAsync("t.hall");
        Assert.Equal(2, model.Exits.Count);
        await model.OnPostRemoveExitAsync("t.hall", 0);
        Assert.Single(model.Exits);
        Assert.Empty(core.RoomUpdates);
        Assert.Empty(core.RoomCreates);
        Assert.Empty(core.RoomDeletes);
    }

    [Fact]
    public async Task AddExit_ForAMissingRoom_Is404()
    {
        var (model, _) = Page(new FakeAdminClient());
        await model.OnPostAddExitAsync("t.gone");
        Assert.Equal(404, model.Response.StatusCode);
    }

    [Fact]
    public async Task Delete_Refused_ShowsCoresSentence_AndKeepsTheRoom()
    {
        var core = new FakeAdminClient { RoomEditResult = AdminResult.Refused, RoomEditMessage = "The default room can never be deleted." }.AddRoom("t.start");
        var (model, _) = Page(core);
        await model.OnPostDeleteAsync("t.start");
        Assert.Equal("The default room can never be deleted.", model.Error);
        Assert.True(core.Rooms.ContainsKey("t.start"));
        Assert.Equal("short t.start", model.Short); // the form shows the stored values again
    }

    [Fact]
    public async Task Delete_Refused_WithoutASentence_ShowsAFixedMessage()
    {
        var (model, _) = Page(new FakeAdminClient { RoomEditResult = AdminResult.Refused }.AddRoom("t.start"));
        await model.OnPostDeleteAsync("t.start");
        Assert.Equal(RoomEditModel.FailedError, model.Error);
    }

    [Fact]
    public async Task Delete_Success_RedirectsToTheMap()
    {
        var (model, core) = Page();
        model.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(model.HttpContext, new NoTempData());
        var result = await model.OnPostDeleteAsync("t.hall");
        Assert.Equal("/creator/map", Assert.IsType<RedirectResult>(result).Url);
        Assert.Equal(new[] { "t.hall" }, core.RoomDeletes);
        Assert.False(core.Rooms.ContainsKey("t.hall"));
    }

    [Fact]
    public async Task Delete_NotFound_Is404()
    {
        var (model, _) = Page(new FakeAdminClient { RoomEditResult = AdminResult.NotFound });
        await model.OnPostDeleteAsync("t.gone");
        Assert.Equal(404, model.Response.StatusCode);
    }

    [Fact]
    public async Task Delete_WhenCoreRefusesTheRole_Shows403()
    {
        var (model, core) = Page(new FakeAdminClient { RefuseRoomCalls = true }.AddRoom("t.hall"));
        await model.OnPostDeleteAsync("t.hall");
        Assert.Equal(403, model.Response.StatusCode);
        Assert.True(core.Rooms.ContainsKey("t.hall"));
    }

    [Fact]
    public async Task Delete_WhenCoreIsUnavailable_Shows503()
    {
        var (model, _) = Page(new FakeAdminClient { Unavailable = true });
        await model.OnPostDeleteAsync("t.hall");
        Assert.Equal(503, model.Response.StatusCode);
    }

    private class NoTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    [Fact]
    public void RoomUrl_EscapesTheKey_SoItCannotBreakOutOfTheLink()
    {
        Assert.Equal("/creator/rooms/t.hall", RoomEditModel.RoomUrl("t.hall"));
        Assert.DoesNotContain("\"", RoomEditModel.RoomUrl("a\"b<c>"));
        Assert.DoesNotContain("<", RoomEditModel.RoomUrl("a\"b<c>"));
    }

    [Fact]
    public void RoomEditPage_Source_UsesNoRawHtmlOutput()
    {
        var cshtml = File.ReadAllText(FindFile("MUD.Web/Pages/RoomEdit.cshtml"));
        Assert.DoesNotContain("Html.Raw", cshtml);
        var creator = File.ReadAllText(FindFile("MUD.Web/Pages/Creator.cshtml"));
        Assert.DoesNotContain("Html.Raw", creator);
        Assert.DoesNotContain("not implemented", creator, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MUD.sln"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
    }
}

public class CreatorPageTests
{
    private static CreatorModel Make(FakeAdminClient core, string token = "token-1")
    {
        var model = new CreatorModel(new RoomCreationService(core));
        var http = new DefaultHttpContext();
        http.Request.Headers.Cookie = SessionAuthentication.CookieName + "=" + token;
        model.PageContext = new PageContext { HttpContext = http };
        return model;
    }

    private static void Fill(CreatorModel page)
    {
        page.Form = new RoomForm { Key = "new", Short = "a room", Long = "Long.", Area = "t" };
        page.OnGet();
        page.Form.Key = "new"; page.Form.Short = "a room"; page.Form.Long = "Long."; page.Form.Area = "t";
    }

    [Fact]
    public async Task Post_Created_RedirectsToTheEditPage_AndSendsTheCookieToken()
    {
        var core = new FakeAdminClient();
        var page = Make(core, "abc");
        Fill(page);
        var result = await page.OnPostAsync();
        Assert.Equal("/creator/rooms/t.new", Assert.IsType<RedirectResult>(result).Url);
        Assert.Equal(new[] { "abc" }, core.RoomTokens);
    }

    [Fact]
    public async Task Post_KeyExists_ShowsFixedError()
    {
        var page = Make(new FakeAdminClient { RoomEditResult = AdminResult.AlreadyExists });
        Fill(page);
        Assert.IsType<PageResult>(await page.OnPostAsync());
        Assert.Equal(CreatorModel.KeyExistsError, page.Error);
    }

    [Fact]
    public async Task Post_CoreUnavailable_ShowsFixedError()
    {
        var page = Make(new FakeAdminClient { Unavailable = true });
        Fill(page);
        Assert.IsType<PageResult>(await page.OnPostAsync());
        Assert.Equal(CreatorModel.UnavailableError, page.Error);
    }

    [Fact]
    public async Task Post_RefusedSession_ShowsForbidden()
    {
        var page = Make(new FakeAdminClient { RefuseRoomCalls = true });
        Fill(page);
        await page.OnPostAsync();
        Assert.Equal(CreatorModel.ForbiddenError, page.Error);
    }
}
