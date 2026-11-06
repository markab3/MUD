using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using MUD.Contracts;
using MUD.Web.Pages;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

public class PlayerRulesTests
{
    [Theory]
    [InlineData(AccountRole.Player, AccountRole.Player, false)]
    [InlineData(AccountRole.Player, AccountRole.Creator, false)]
    [InlineData(AccountRole.Creator, AccountRole.Player, true)]
    [InlineData(AccountRole.Creator, AccountRole.Creator, false)]
    [InlineData(AccountRole.Creator, AccountRole.Admin, false)]
    [InlineData(AccountRole.Admin, AccountRole.Player, true)]
    [InlineData(AccountRole.Admin, AccountRole.Creator, true)]
    [InlineData(AccountRole.Admin, AccountRole.Admin, false)]
    public void CanEdit_RequiresStrictlyHigherRole(AccountRole editor, AccountRole target, bool expected) =>
        Assert.Equal(expected, PlayerRules.CanEdit(editor, target));

    [Theory]
    [InlineData(AccountRole.Creator, AccountRole.Player, AccountRole.Creator, false)]   // only Admin grants
    [InlineData(AccountRole.Admin, AccountRole.Player, AccountRole.Creator, true)]
    [InlineData(AccountRole.Admin, AccountRole.Creator, AccountRole.Player, true)]
    [InlineData(AccountRole.Admin, AccountRole.Player, AccountRole.Admin, true)]         // not above own role
    [InlineData(AccountRole.Admin, AccountRole.Admin, AccountRole.Player, false)]        // nobody edits an Admin
    public void CanSetRole(AccountRole editor, AccountRole target, AccountRole to, bool expected) =>
        Assert.Equal(expected, PlayerRules.CanSetRole(editor, target, to));

    [Fact]
    public void CannotGrantARoleAboveYourOwn()
    {
        Assert.False(PlayerRules.CanSetRole(AccountRole.Creator, AccountRole.Player, AccountRole.Admin));
    }

    [Theory]
    [InlineData(AccountRole.Creator, AccountRole.Player, false)]
    [InlineData(AccountRole.Admin, AccountRole.Player, true)]
    [InlineData(AccountRole.Admin, AccountRole.Creator, true)]
    [InlineData(AccountRole.Admin, AccountRole.Admin, false)]
    public void OnlyAnAdminRetires_AndNeverAnAdmin(AccountRole editor, AccountRole target, bool expected) =>
        Assert.Equal(expected, PlayerRules.CanSetRetired(editor, target));
}

// The player pages over a fake of core's Admin service: the pages send what the form holds with the cookie's token and show what core
// answers. The rules themselves (roles, validation, audit, live objects) are core's and are tested in MUD.Tests/AdminApiTests.cs.
public class PlayerPagesTests
{
    private static ClaimsPrincipal User(string? name, string? role) =>
        new(new ClaimsIdentity(name == null ? Array.Empty<Claim>() : new[] { new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.Role, role!) }, name == null ? null : "test"));

    private static T Wire<T>(T model, ClaimsPrincipal user, string token = "token-1") where T : PageModel
    {
        var http = new DefaultHttpContext { User = user };
        if (!string.IsNullOrEmpty(token)) { http.Request.Headers.Cookie = SessionAuthentication.CookieName + "=" + token; }
        model.PageContext = new PageContext { HttpContext = http };
        return model;
    }

    private static FakeAdminClient Core() =>
        new FakeAdminClient().AddPlayer("Ann").AddPlayer("Ben", AdminRole.Creator).AddPlayer("Ada", AdminRole.Admin).AddPlayer("Cat", AdminRole.Creator).AddPlayer("Eve", AdminRole.Admin);

    private static PlayerEditModel EditPage(FakeAdminClient core, string editorName, AccountRole editorRole) =>
        Wire(new PlayerEditModel(core), User(editorName, editorRole.ToString()));

    // ---- policy / routes ----

    [Fact]
    public void Pages_RequireCreatorOrAdmin()
    {
        foreach (var t in new[] { typeof(PlayerListModel), typeof(PlayerEditModel) })
        {
            var attr = (AuthorizeAttribute?)Attribute.GetCustomAttribute(t, typeof(AuthorizeAttribute));
            Assert.NotNull(attr);
            Assert.Equal(WebPolicies.CreatorOrAdmin, attr!.Policy);
        }
    }

    [Fact]
    public void Routes_AreCreatorPlayers_AndCreatorPlayersNameKey()
    {
        Assert.StartsWith("@page \"/creator/players\"", File.ReadAllText(Find("MUD.Web/Pages/PlayerList.cshtml")));
        Assert.StartsWith("@page \"/creator/players/{nameKey}\"", File.ReadAllText(Find("MUD.Web/Pages/PlayerEdit.cshtml")));
    }

    [Theory]
    [InlineData(null, false, false)]       // anonymous: challenged
    [InlineData("Player", false, true)]    // plain player: forbidden
    [InlineData("Creator", true, false)]
    [InlineData("Admin", true, false)]
    public async Task PolicyMatrix_AnonymousPlayerCreatorAdmin(string? role, bool succeeds, bool forbidden)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(WebPolicies.Configure);
        var sp = services.BuildServiceProvider();
        var policy = (await sp.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(WebPolicies.CreatorOrAdmin))!;
        var evaluator = new Microsoft.AspNetCore.Authorization.Policy.PolicyEvaluator(sp.GetRequiredService<IAuthorizationService>());
        var user = User(role == null ? null : "Someone", role);
        var context = new DefaultHttpContext { User = user };
        var auth = role == null ? Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult()
            : Microsoft.AspNetCore.Authentication.AuthenticateResult.Success(new Microsoft.AspNetCore.Authentication.AuthenticationTicket(user, "test"));
        var result = await evaluator.AuthorizeAsync(policy, auth, context, null);
        Assert.Equal(succeeds, result.Succeeded);
        Assert.Equal(forbidden, result.Forbidden);
        Assert.Equal(role == null, result.Challenged);
    }

    // ---- list ----

    [Fact]
    public async Task List_AsksCoreForTheFilteredSortedPage_WithTheCookieToken()
    {
        var core = new FakeAdminClient();
        for (var i = 0; i < 120; i++) { core.AddPlayer("P" + i.ToString("D3")); }
        core.AddPlayer("Zelda", AdminRole.Admin).AddPlayer("Bob", AdminRole.Creator);

        var list = Wire(new PlayerListModel(core), User("Ada", "Admin"));
        await list.OnGetAsync();
        Assert.Equal(50, PlayerListModel.PageSize);
        Assert.Equal(50, list.Items.Count);
        Assert.Equal(122, list.Total);
        Assert.Equal(3, list.PageCount);
        Assert.Equal("Bob", list.Items[0].Name);
        Assert.Equal("unknown", PlayerListModel.OnlineText(list.Items[0]));
        Assert.Equal("Creator", PlayerListModel.RoleText(list.Items[0].Role));
        Assert.Equal(new[] { "token-1" }, core.PlayerTokens);
        Assert.Equal(("", false, 1, 50), core.PlayerListCalls.Single());

        var descending = Wire(new PlayerListModel(core) { Sort = "desc" }, User("Ada", "Admin"));
        await descending.OnGetAsync();
        Assert.Equal("Zelda", descending.Items[0].Name);

        var last = Wire(new PlayerListModel(core) { PageNumber = 99 }, User("Ada", "Admin"));
        await last.OnGetAsync();
        Assert.Equal(3, last.Page);
        Assert.Equal(22, last.Items.Count);

        var filtered = Wire(new PlayerListModel(core) { Filter = " zel " }, User("Ada", "Admin"));
        await filtered.OnGetAsync();
        Assert.Equal("Zelda", Assert.Single(filtered.Items).Name);
        Assert.Equal("zel", core.PlayerListCalls.Last().Filter);
    }

    [Fact]
    public async Task List_CutsALongFilter_AndNeverSendsAPageBelowOne()
    {
        var core = Core();
        var list = Wire(new PlayerListModel(core) { Filter = new string('x', 200), PageNumber = -4 }, User("Ada", "Admin"));
        await list.OnGetAsync();
        var call = core.PlayerListCalls.First();
        Assert.Equal(PlayerListModel.MaxFilterLength, call.Filter.Length);
        Assert.Equal(1, call.Page);
    }

    [Fact]
    public async Task List_EmptyResult_IsPageOneOfOne()
    {
        var list = Wire(new PlayerListModel(new FakeAdminClient()), User("Ada", "Admin"));
        await list.OnGetAsync();
        Assert.Empty(list.Items);
        Assert.Equal(1, list.Page);
        Assert.Equal(1, list.PageCount);
    }

    [Fact]
    public async Task List_CoreUnavailable_ShowsAFixedMessage()
    {
        var list = Wire(new PlayerListModel(new FakeAdminClient { Unavailable = true }), User("Ada", "Admin"));
        await list.OnGetAsync();
        Assert.Equal(PlayerListModel.UnavailableError, list.Error);
    }

    [Fact]
    public async Task List_CoreRefusesTheToken_ShowsAFixedMessage()
    {
        var list = Wire(new PlayerListModel(new FakeAdminClient { RefusePlayerCalls = true }), User("Ada", "Admin"));
        await list.OnGetAsync();
        Assert.Equal(PlayerListModel.ForbiddenError, list.Error);
    }

    [Fact]
    public void EditUrl_EscapesTheKey()
    {
        Assert.Equal("/creator/players/ann", PlayerListModel.EditUrl("ann"));
        Assert.DoesNotContain("\"", PlayerListModel.EditUrl("a\"b<c>"));
        Assert.DoesNotContain("<", PlayerEditModel.PlayerUrl("a\"b<c>"));
    }

    // ---- edit page: reading ----

    [Fact]
    public void EditModel_BindsOnlyTheAllowList_NoPasswordNameKeyInventoryLocationOrRevision()
    {
        var bound = typeof(PlayerEditModel).GetProperties()
            .Where(p => p.GetCustomAttributes(typeof(BindPropertyAttribute), true).Any()).Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Class", "Gender", "Long", "Race", "Retired", "Role", "Short" }, bound);
    }

    [Fact]
    public async Task Get_UnknownPlayer_Is404()
    {
        var page = EditPage(Core(), "Ada", AccountRole.Admin);
        await page.OnGetAsync("nobody");
        Assert.Equal(404, page.Response.StatusCode);
        Assert.True(page.Missing);
    }

    [Fact]
    public async Task Get_AsksCoreForThePlayerWithTheCookieToken()
    {
        var core = Core();
        var page = EditPage(core, "Cat", AccountRole.Creator);
        await page.OnGetAsync("ann");
        Assert.Equal(new[] { "token-1" }, core.PlayerTokens);
        Assert.Equal("Ann", page.Short);
        Assert.Equal("human", page.Race);
    }

    [Fact]
    public async Task Get_ByCreator_OfAPlayer_AllowsEditing_ButNotRoleOrRetire()
    {
        var page = EditPage(Core(), "Cat", AccountRole.Creator);
        await page.OnGetAsync("ann");
        Assert.True(page.CanEdit);
        Assert.False(page.CanSetRole);
        Assert.False(page.CanSetRetired);
    }

    [Fact]
    public async Task Get_ByCreator_OfCreatorOrAdmin_ShowsNoEditControls()
    {
        var page = EditPage(Core(), "Cat", AccountRole.Creator);
        await page.OnGetAsync("ben");
        Assert.False(page.CanEdit);
        await page.OnGetAsync("ada");
        Assert.False(page.CanEdit);
    }

    [Fact]
    public async Task Get_ByAdmin_OfAnAdmin_IsReadOnly_AdminOfPlayerAndCreatorIsEditable()
    {
        var page = EditPage(Core(), "Ada", AccountRole.Admin);
        await page.OnGetAsync("eve");
        Assert.False(page.CanEdit);
        await page.OnGetAsync("ann");
        Assert.True(page.CanEdit);
        Assert.True(page.CanSetRole);
        Assert.True(page.CanSetRetired);
        await page.OnGetAsync("ben");
        Assert.True(page.CanEdit);
    }

    [Fact]
    public async Task Get_CoreUnavailable_Is503_AndCoreRefusal_Is403()
    {
        var down = EditPage(new FakeAdminClient { Unavailable = true }, "Ada", AccountRole.Admin);
        await down.OnGetAsync("ann");
        Assert.Equal(503, down.Response.StatusCode);
        Assert.Equal(PlayerEditModel.UnavailableError, down.Error);

        var refused = EditPage(new FakeAdminClient { RefusePlayerCalls = true }, "Ada", AccountRole.Admin);
        await refused.OnGetAsync("ann");
        Assert.Equal(403, refused.Response.StatusCode);
        Assert.Equal(PlayerEditModel.ForbiddenError, refused.Error);
    }

    // ---- edit page: saving ----

    [Fact]
    public async Task Post_SendsTheFormAndTheRouteKeyAndTheToken_AndShowsTheSavedPlayer()
    {
        var core = Core();
        var page = EditPage(core, "Cat", AccountRole.Creator);
        await page.OnGetAsync("ann");
        page.Short = "Ann the Bold";
        page.Race = "elf";
        var result = await page.OnPostAsync("ann");

        Assert.IsType<PageResult>(result);
        var sent = Assert.Single(core.PlayerUpdates);
        Assert.Equal("ann", sent.NameKey);
        Assert.Equal("Ann the Bold", sent.ShortDescription);
        Assert.Equal("elf", sent.Race);
        Assert.Equal("token-1", core.PlayerTokens.Last());
        Assert.NotNull(page.Notice);
        Assert.Null(page.Error);
        Assert.Equal("Ann the Bold", page.Current!.ShortDescription);
        Assert.Equal("Ann the Bold", core.Players["ann"].ShortDescription);
    }

    [Fact]
    public async Task Post_HasNoRevisionAnywhere_LastWriteWins()
    {
        // D30: two saves in a row both go to core; the web never sends or checks a revision.
        var core = Core();
        var page = EditPage(core, "Cat", AccountRole.Creator);
        await page.OnGetAsync("ann");
        page.Short = "first";
        await page.OnPostAsync("ann");
        page.Short = "second";
        await page.OnPostAsync("ann");
        Assert.Equal(2, core.PlayerUpdates.Count);
        Assert.Equal("second", core.Players["ann"].ShortDescription);
        Assert.DoesNotContain(typeof(PlayerEditModel).GetProperties(), p => p.Name.Contains("Rev", StringComparison.Ordinal) || p.Name.Contains("Conflict", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(UpdatePlayerRequest).GetProperties(), p => p.Name.Contains("Rev", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Post_NoChange_ShowsTheNotice()
    {
        var core = Core();
        core.PlayerEditResult = AdminResult.NoChange;
        var page = EditPage(core, "Cat", AccountRole.Creator);
        await page.OnPostAsync("ann");
        Assert.Equal("Nothing was changed.", page.Notice);
    }

    [Fact]
    public async Task Post_Invalid_ShowsCoresFieldMessages_AndKeepsInput()
    {
        var core = Core();
        core.PlayerEditResult = AdminResult.Invalid;
        core.PlayerEditErrors.Add(new FieldError { Field = "Short", Message = "Short description must not be empty." });
        var page = EditPage(core, "Cat", AccountRole.Creator);
        await page.OnGetAsync("ann");
        page.Short = "";
        await page.OnPostAsync("ann");
        Assert.False(page.ModelState.IsValid);
        Assert.Contains(page.ModelState[string.Empty]!.Errors, e => e.ErrorMessage == "Short description must not be empty.");
        Assert.Equal("", page.Short);
        Assert.Equal("Ann", core.Players["ann"].ShortDescription);
    }

    [Theory]
    [InlineData(AdminResult.Forbidden, PlayerEditModel.ForbiddenError)]
    [InlineData(AdminResult.LastAdmin, PlayerEditModel.LastAdminError)]
    [InlineData(AdminResult.AuditFailed, PlayerEditModel.AuditFailedError)]
    [InlineData(AdminResult.Failed, PlayerEditModel.FailedError)]
    [InlineData(AdminResult.Refused, PlayerEditModel.FailedError)]
    public async Task Post_CoreRefusals_ShowFixedMessages_AndTheStoredValues(AdminResult result, string message)
    {
        var core = Core();
        core.PlayerEditResult = result;
        var page = EditPage(core, "Cat", AccountRole.Creator);
        await page.OnGetAsync("ann");
        page.Short = "mine";
        await page.OnPostAsync("ann");
        Assert.Equal(message, page.Error);
        Assert.Equal("Ann", page.Current!.ShortDescription);
        Assert.Equal("Ann", core.Players["ann"].ShortDescription);
    }

    [Fact]
    public async Task Post_NotFound_Is404()
    {
        var core = Core();
        core.PlayerEditResult = AdminResult.NotFound;
        var page = EditPage(core, "Cat", AccountRole.Creator);
        await page.OnPostAsync("nobody");
        Assert.Equal(404, page.Response.StatusCode);
        Assert.True(page.Missing);
    }

    [Fact]
    public async Task Post_ForgedRoleAndRetired_AreSentToCore_WhichRefuses()
    {
        // The page offers neither control to a Creator, but a form can be forged: the page does not decide, core does.
        var core = Core();
        core.PlayerEditResult = AdminResult.Forbidden;
        var page = EditPage(core, "Cat", AccountRole.Creator);
        await page.OnGetAsync("ann");
        page.Role = AccountRole.Creator;
        page.Retired = true;
        await page.OnPostAsync("ann");
        var sent = Assert.Single(core.PlayerUpdates);
        Assert.Equal(AdminRole.Creator, sent.Role);
        Assert.True(sent.Retired);
        Assert.Equal(PlayerEditModel.ForbiddenError, page.Error);
        Assert.Equal(AdminRole.Player, core.Players["ann"].Role);
        Assert.False(core.Players["ann"].Retired);
    }

    [Theory]
    [InlineData(AccountRole.Player, AdminRole.Player)]
    [InlineData(AccountRole.Creator, AdminRole.Creator)]
    [InlineData(AccountRole.Admin, AdminRole.Admin)]
    public async Task Post_MapsTheRoleChoiceToCoresRole(AccountRole chosen, AdminRole expected)
    {
        var core = Core();
        var page = EditPage(core, "Ada", AccountRole.Admin);
        await page.OnGetAsync("ann");
        page.Role = chosen;
        await page.OnPostAsync("ann");
        Assert.Equal(expected, core.PlayerUpdates.Single().Role);
    }

    [Fact]
    public async Task Post_CoreSessionEnded_IsForbidden()
    {
        var core = Core();
        core.RefusePlayerCalls = true;
        core.RefuseBecauseSessionEnded = true;
        var page = EditPage(core, "Cat", AccountRole.Creator);
        await page.OnPostAsync("ann");
        Assert.Equal(403, page.Response.StatusCode);
        Assert.Equal(PlayerEditModel.ForbiddenError, page.Error);
    }

    [Fact]
    public async Task Post_CoreUnavailable_Is503()
    {
        var page = EditPage(new FakeAdminClient { Unavailable = true }, "Cat", AccountRole.Creator);
        await page.OnPostAsync("ann");
        Assert.Equal(503, page.Response.StatusCode);
        Assert.Equal(PlayerEditModel.UnavailableError, page.Error);
    }

    // ---- no password, no stored value in messages ----

    [Fact]
    public void ThePlayerMessagesAndPageModels_HaveNoPasswordProperty()
    {
        foreach (var t in new[] { typeof(PlayerDetail), typeof(PlayerSummary), typeof(UpdatePlayerRequest), typeof(PlayerEditModel), typeof(PlayerListModel) })
        {
            Assert.DoesNotContain(t.GetProperties(), p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void FixedMessages_ContainNoPlaceholders()
    {
        foreach (var s in new[] { PlayerEditModel.NotFoundError, PlayerEditModel.FailedError, PlayerEditModel.AuditFailedError, PlayerEditModel.ForbiddenError, PlayerEditModel.LastAdminError, PlayerEditModel.UnavailableError, PlayerListModel.FailedError, PlayerListModel.UnavailableError, PlayerListModel.ForbiddenError })
        {
            Assert.DoesNotContain("{", s);
        }
    }

    // ---- source checks ----

    [Fact]
    public void PageSources_NeverReferencePassword_AndUseNoRawHtml_AndPostFormsHaveAntiforgery()
    {
        foreach (var f in new[] { "PlayerList.cshtml", "PlayerList.cshtml.cs", "PlayerEdit.cshtml", "PlayerEdit.cshtml.cs" })
        {
            var text = File.ReadAllText(Find("MUD.Web/Pages/" + f));
            Assert.DoesNotContain("Password", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Html.Raw", text);
        }
        var edit = File.ReadAllText(Find("MUD.Web/Pages/PlayerEdit.cshtml"));
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(edit, "<form method=\"post\"").Count);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(edit, "Html.AntiForgeryToken\\(\\)").Count);
        Assert.DoesNotContain("Rev", edit);
    }

    [Fact]
    public void ThePlayerPages_DoNotTouchTheDatabase()
    {
        var web = typeof(PlayerEditModel).Assembly;
        foreach (var name in new[] { "MongoPlayerEditStore", "MongoAuditLog", "IPlayerEditStore", "IPlayerWriter", "PlayerWriter", "IAuditLog", "AuditEntry", "RoomActor" })
        {
            Assert.Null(web.GetType("MUD.Web.Services." + name));
        }
        foreach (var f in new[] { "PlayerList.cshtml.cs", "PlayerEdit.cshtml.cs" })
        {
            Assert.DoesNotContain("Mongo", File.ReadAllText(Find("MUD.Web/Pages/" + f)));
        }
    }

    [Fact]
    public void Nav_HasAPlayersLink_UnderTheCreatorPolicy()
    {
        var layout = File.ReadAllText(Find("MUD.Web/Pages/Shared/_Layout.cshtml"));
        var policyIndex = layout.IndexOf("CreatorOrAdmin", StringComparison.Ordinal);
        var linkIndex = layout.IndexOf("href=\"/creator/players\"", StringComparison.Ordinal);
        Assert.True(policyIndex >= 0 && linkIndex > policyIndex);
    }

    private static string Find(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MUD.sln"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        return Path.GetFullPath(Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
    }
}
