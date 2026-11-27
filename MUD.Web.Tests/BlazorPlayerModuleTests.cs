using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using MUD.Contracts;
using MUD.Web.Pages;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

// The player and module pages of the Blazor UI (WEB_BLAZOR.md 4.1): PlayerPagesTests and ModulesPageTests ported from the Razor page
// models to the presenters. Same arrangement over FakeAdminClient, same sentences and calls. PlayerRulesTests stay where they are.
// HTTP status codes become PlayerEditMissing values (WEB_BLAZOR.md 7.3). The Razor pages and their tests stay until the cutover card.
public class PlayerListPresenterTests
{
    private const string Token = "token-1";

    [Fact]
    public async Task List_AsksCoreForTheFilteredSortedPage_WithTheCircuitToken()
    {
        var core = new FakeAdminClient();
        for (var i = 0; i < 120; i++) { core.AddPlayer("P" + i.ToString("D3")); }
        core.AddPlayer("Zelda", AdminRole.Admin).AddPlayer("Bob", AdminRole.Creator);

        var list = new PlayerListPresenter(core, Token);
        await list.LoadAsync(null, null, 1);
        Assert.Equal(50, PlayerListPresenter.PageSize);
        Assert.Equal(50, list.Items.Count);
        Assert.Equal(122, list.Total);
        Assert.Equal(3, list.PageCount);
        Assert.Equal("Bob", list.Items[0].Name);
        Assert.Equal("unknown", PlayerListPresenter.OnlineText(list.Items[0]));
        Assert.Equal("Creator", PlayerListPresenter.RoleText(list.Items[0].Role));
        Assert.Equal(new[] { Token }, core.PlayerTokens);
        Assert.Equal(("", false, 1, 50), core.PlayerListCalls.Single());

        var descending = new PlayerListPresenter(core, Token);
        await descending.LoadAsync(null, "desc", 1);
        Assert.Equal("Zelda", descending.Items[0].Name);
        Assert.True(descending.Descending);

        var last = new PlayerListPresenter(core, Token);
        await last.LoadAsync(null, null, 99);
        Assert.Equal(3, last.Page);
        Assert.Equal(22, last.Items.Count);

        var filtered = new PlayerListPresenter(core, Token);
        await filtered.LoadAsync(" zel ", null, 1);
        Assert.Equal("Zelda", Assert.Single(filtered.Items).Name);
        Assert.Equal("zel", core.PlayerListCalls.Last().Filter);
    }

    [Fact]
    public async Task List_CutsALongFilter_AndNeverSendsAPageBelowOne()
    {
        var core = new FakeAdminClient().AddPlayer("Ann");
        var list = new PlayerListPresenter(core, Token);
        await list.LoadAsync(new string('x', 200), null, -4);
        var call = core.PlayerListCalls.First();
        Assert.Equal(PlayerListPresenter.MaxFilterLength, call.Filter.Length);
        Assert.Equal(1, call.Page);
    }

    [Fact]
    public async Task List_EmptyResult_IsPageOneOfOne()
    {
        var list = new PlayerListPresenter(new FakeAdminClient(), Token);
        Assert.Equal(PlayerListOutcome.Ok, await list.LoadAsync(null, null, 1));
        Assert.Empty(list.Items);
        Assert.Equal(1, list.Page);
        Assert.Equal(1, list.PageCount);
    }

    [Fact]
    public async Task List_CoreUnavailable_ShowsAFixedMessage()
    {
        var list = new PlayerListPresenter(new FakeAdminClient { Unavailable = true }, Token);
        Assert.Equal(PlayerListOutcome.Failed, await list.LoadAsync(null, null, 1));
        Assert.Equal(Messages.Unavailable, list.Error);
        Assert.Empty(list.Items);
    }

    [Fact]
    public async Task List_CoreRefusesTheToken_ShowsAFixedMessage()
    {
        var list = new PlayerListPresenter(new FakeAdminClient { RefusePlayerCalls = true }, Token);
        await list.LoadAsync(null, null, 1);
        Assert.Equal(Messages.PlayerListForbidden, list.Error);
    }

    [Fact]
    public void Urls_PointAtTheRealUrls_AndEscapeTheKeyAndFilter()
    {
        Assert.Equal("/creator/players/ann", PlayerListPresenter.EditUrl("ann"));
        Assert.DoesNotContain("\"", PlayerListPresenter.EditUrl("a\"b<c>"));
        Assert.DoesNotContain("<", WebRoutes.PlayerEdit("a\"b<c>"));
        Assert.Equal("/creator/players?q=a%26b&sort=desc&page=2", PlayerListPresenter.PageUrl("a&b", true, 2));
    }

    [Fact]
    public async Task UrlFor_KeepsFilterAndSort_AndCanFlipTheSort()
    {
        var core = new FakeAdminClient().AddPlayer("Ann");
        var list = new PlayerListPresenter(core, Token);
        await list.LoadAsync("an", "desc", 1);
        Assert.Equal("/creator/players?q=an&sort=desc&page=3", list.UrlFor(3));
        Assert.Equal("/creator/players?q=an&sort=asc&page=1", list.UrlFor(1, false));
    }

    [Fact]
    public void Status_IsWords()
    {
        Assert.Equal("Retired", PlayerListPresenter.StatusText(new PlayerSummary { Retired = true }));
        Assert.Equal("Active", PlayerListPresenter.StatusText(new PlayerSummary()));
    }
}

public class PlayerEditPresenterTests
{
    private const string Token = "token-1";

    private static FakeAdminClient Core() =>
        new FakeAdminClient().AddPlayer("Ann").AddPlayer("Ben", AdminRole.Creator).AddPlayer("Ada", AdminRole.Admin).AddPlayer("Cat", AdminRole.Creator).AddPlayer("Eve", AdminRole.Admin);

    private static PlayerEditPresenter Make(FakeAdminClient core, AccountRole editor, string key) => new(core, Token, editor, key);

    // ---- reading ----

    [Fact]
    public async Task Load_UnknownPlayer_IsMissingNotFound()
    {
        var p = Make(Core(), AccountRole.Admin, "nobody");
        Assert.Equal(PlayerEditOutcome.Missing, await p.LoadAsync());
        Assert.Equal(PlayerEditMissing.NotFound, p.Missing);
        Assert.Equal(Messages.PlayerNotFound, p.Error);
    }

    [Fact]
    public async Task Load_AsksCoreForThePlayerWithTheCircuitToken()
    {
        var core = Core();
        var p = Make(core, AccountRole.Creator, "ann");
        await p.LoadAsync();
        Assert.Equal(new[] { Token }, core.PlayerTokens);
        Assert.Equal("Ann", p.Short);
        Assert.Equal("human", p.Race);
        Assert.Equal("ann", p.NameKey);
    }

    [Fact]
    public async Task Load_ByCreator_OfAPlayer_AllowsEditing_ButNotRoleOrRetire()
    {
        var p = Make(Core(), AccountRole.Creator, "ann");
        await p.LoadAsync();
        Assert.True(p.CanEdit);
        Assert.False(p.CanSetRole);
        Assert.False(p.CanSetRetired);
    }

    [Theory]
    [InlineData("ben")]
    [InlineData("ada")]
    public async Task Load_ByCreator_OfCreatorOrAdmin_ShowsNoEditControls(string target)
    {
        var p = Make(Core(), AccountRole.Creator, target);
        await p.LoadAsync();
        Assert.False(p.CanEdit);
    }

    [Fact]
    public async Task Load_ByAdmin_OfAnAdmin_IsReadOnly_AdminOfPlayerAndCreatorIsEditable()
    {
        var eve = Make(Core(), AccountRole.Admin, "eve");
        await eve.LoadAsync();
        Assert.False(eve.CanEdit);
        var ann = Make(Core(), AccountRole.Admin, "ann");
        await ann.LoadAsync();
        Assert.True(ann.CanEdit);
        Assert.True(ann.CanSetRole);
        Assert.True(ann.CanSetRetired);
        var ben = Make(Core(), AccountRole.Admin, "ben");
        await ben.LoadAsync();
        Assert.True(ben.CanEdit);
    }

    [Fact]
    public async Task Load_CoreUnavailable_AndCoreRefusal_AreMissingWithFixedMessages()
    {
        var down = Make(new FakeAdminClient { Unavailable = true }, AccountRole.Admin, "ann");
        await down.LoadAsync();
        Assert.Equal(PlayerEditMissing.Unavailable, down.Missing);
        Assert.Equal(Messages.Unavailable, down.Error);

        var refused = Make(new FakeAdminClient { RefusePlayerCalls = true }, AccountRole.Admin, "ann");
        await refused.LoadAsync();
        Assert.Equal(PlayerEditMissing.Forbidden, refused.Missing);
        Assert.Equal(Messages.PlayerForbidden, refused.Error);
    }

    // ---- saving ----

    [Fact]
    public async Task Save_SendsTheFormAndTheRouteKeyAndTheToken_AndShowsTheSavedPlayer()
    {
        var core = Core();
        var p = Make(core, AccountRole.Creator, "ann");
        await p.LoadAsync();
        p.Short = "Ann the Bold";
        p.Race = "elf";
        Assert.Equal(PlayerEditOutcome.Ok, await p.SaveAsync());

        var sent = Assert.Single(core.PlayerUpdates);
        Assert.Equal("ann", sent.NameKey);
        Assert.Equal("Ann the Bold", sent.ShortDescription);
        Assert.Equal("elf", sent.Race);
        Assert.Equal(Token, core.PlayerTokens.Last());
        Assert.Equal(Messages.PlayerSaved, p.Notice);
        Assert.Null(p.Error);
        Assert.Equal("Ann the Bold", p.Current!.ShortDescription);
        Assert.Equal("Ann the Bold", core.Players["ann"].ShortDescription);
    }

    [Fact]
    public async Task Save_HasNoRevisionAnywhere_LastWriteWins()
    {
        // D30: two saves in a row both go to core; the web never sends or checks a revision.
        var core = Core();
        var p = Make(core, AccountRole.Creator, "ann");
        await p.LoadAsync();
        p.Short = "first";
        await p.SaveAsync();
        p.Short = "second";
        await p.SaveAsync();
        Assert.Equal(2, core.PlayerUpdates.Count);
        Assert.Equal("second", core.Players["ann"].ShortDescription);
        Assert.DoesNotContain(typeof(PlayerEditPresenter).GetProperties(), x => x.Name.Contains("Rev", StringComparison.Ordinal) || x.Name.Contains("Conflict", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(UpdatePlayerRequest).GetProperties(), x => x.Name.Contains("Rev", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Save_NoChange_ShowsTheNotice()
    {
        var core = Core();
        core.PlayerEditResult = AdminResult.NoChange;
        var p = Make(core, AccountRole.Creator, "ann");
        Assert.Equal(PlayerEditOutcome.NoChange, await p.SaveAsync());
        Assert.Equal(Messages.PlayerNoChange, p.Notice);
    }

    [Fact]
    public async Task Save_Invalid_ShowsCoresFieldMessages_AndKeepsInput()
    {
        var core = Core();
        core.PlayerEditResult = AdminResult.Invalid;
        core.PlayerEditErrors.Add(new FieldError { Field = "Short", Message = "Short description must not be empty." });
        var p = Make(core, AccountRole.Creator, "ann");
        await p.LoadAsync();
        p.Short = "";
        Assert.Equal(PlayerEditOutcome.Invalid, await p.SaveAsync());
        Assert.Contains("Short description must not be empty.", p.Problems);
        Assert.Equal("", p.Short);
        Assert.Equal("Ann", core.Players["ann"].ShortDescription);
    }

    [Theory]
    [InlineData(AdminResult.Forbidden, Messages.PlayerForbidden)]
    [InlineData(AdminResult.LastAdmin, Messages.PlayerLastAdmin)]
    [InlineData(AdminResult.AuditFailed, Messages.PlayerAuditFailed)]
    [InlineData(AdminResult.Failed, Messages.PlayerSaveFailed)]
    [InlineData(AdminResult.Refused, Messages.PlayerSaveFailed)]
    public async Task Save_CoreRefusals_ShowFixedMessages_AndTheStoredValues(AdminResult result, string message)
    {
        var core = Core();
        core.PlayerEditResult = result;
        var p = Make(core, AccountRole.Creator, "ann");
        await p.LoadAsync();
        p.Short = "mine";
        Assert.Equal(PlayerEditOutcome.Failed, await p.SaveAsync());
        Assert.Equal(message, p.Error);
        Assert.Equal("Ann", p.Current!.ShortDescription);
        Assert.Equal("mine", p.Short);   // the typed input stays in the form
        Assert.Equal("Ann", core.Players["ann"].ShortDescription);
    }

    [Fact]
    public async Task Save_NotFound_IsMissing()
    {
        var core = Core();
        core.PlayerEditResult = AdminResult.NotFound;
        var p = Make(core, AccountRole.Creator, "nobody");
        Assert.Equal(PlayerEditOutcome.Missing, await p.SaveAsync());
        Assert.Equal(PlayerEditMissing.NotFound, p.Missing);
    }

    [Fact]
    public async Task Save_ForgedRoleAndRetired_AreSentToCore_WhichRefuses()
    {
        // The page offers neither control to a Creator, but a call can be forged: the presenter does not decide, core does.
        var core = Core();
        core.PlayerEditResult = AdminResult.Forbidden;
        var p = Make(core, AccountRole.Creator, "ann");
        await p.LoadAsync();
        p.Role = AccountRole.Creator;
        p.Retired = true;
        await p.SaveAsync();
        var sent = Assert.Single(core.PlayerUpdates);
        Assert.Equal(AdminRole.Creator, sent.Role);
        Assert.True(sent.Retired);
        Assert.Equal(Messages.PlayerForbidden, p.Error);
        Assert.Equal(AdminRole.Player, core.Players["ann"].Role);
        Assert.False(core.Players["ann"].Retired);
    }

    [Theory]
    [InlineData(AccountRole.Player, AdminRole.Player)]
    [InlineData(AccountRole.Creator, AdminRole.Creator)]
    [InlineData(AccountRole.Admin, AdminRole.Admin)]
    public async Task Save_MapsTheRoleChoiceToCoresRole(AccountRole chosen, AdminRole expected)
    {
        var core = Core();
        var p = Make(core, AccountRole.Admin, "ann");
        await p.LoadAsync();
        p.Role = chosen;
        await p.SaveAsync();
        Assert.Equal(expected, core.PlayerUpdates.Single().Role);
    }

    [Fact]
    public async Task Save_CoreSessionEnded_IsMissingForbidden()
    {
        var core = Core();
        core.RefusePlayerCalls = true;
        core.RefuseBecauseSessionEnded = true;
        var p = Make(core, AccountRole.Creator, "ann");
        await p.SaveAsync();
        Assert.Equal(PlayerEditMissing.Forbidden, p.Missing);
        Assert.Equal(Messages.PlayerForbidden, p.Error);
    }

    [Fact]
    public async Task Save_CoreUnavailable_IsMissingUnavailable()
    {
        var p = Make(new FakeAdminClient { Unavailable = true }, AccountRole.Creator, "ann");
        await p.SaveAsync();
        Assert.Equal(PlayerEditMissing.Unavailable, p.Missing);
        Assert.Equal(Messages.Unavailable, p.Error);
    }

    // ---- retire confirmation (new in the Blazor UI) ----

    [Fact]
    public async Task WouldRetire_OnlyWhenAnAdminNewlyRetiresALivePlayer()
    {
        var admin = Make(Core(), AccountRole.Admin, "ann");
        await admin.LoadAsync();
        Assert.False(admin.WouldRetire);
        admin.Retired = true;
        Assert.True(admin.WouldRetire);

        var already = Make(new FakeAdminClient().AddPlayer("Ann", AdminRole.Player, retired: true), AccountRole.Admin, "ann");
        await already.LoadAsync();
        Assert.False(already.WouldRetire);

        var creator = Make(Core(), AccountRole.Creator, "ann");
        await creator.LoadAsync();
        creator.Retired = true;   // not offered to a creator, so no dialog; core refuses the forged flag
        Assert.False(creator.WouldRetire);
    }

    // ---- messages and shape ----

    [Fact]
    public void ThePlayerTypes_HaveNoPasswordProperty()
    {
        foreach (var t in new[] { typeof(PlayerDetail), typeof(PlayerSummary), typeof(UpdatePlayerRequest), typeof(PlayerEditPresenter), typeof(PlayerListPresenter) })
        {
            Assert.DoesNotContain(t.GetProperties(), p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void ThePresenter_TakesTheNameKeyFromTheConstructorOnly_AndSetsOnlyTheAllowList()
    {
        Assert.Null(typeof(PlayerEditPresenter).GetProperty("NameKey")!.SetMethod);
        var settable = typeof(PlayerEditPresenter).GetProperties().Where(p => p.SetMethod is { IsPublic: true }).Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Class", "Gender", "Long", "Notice", "Race", "Retired", "Role", "Short" }, settable);
    }

    [Fact]
    public void FixedMessages_ContainNoPlaceholders_AndArePinned()
    {
        foreach (var s in new[] { Messages.PlayerNotFound, Messages.PlayerSaveFailed, Messages.PlayerAuditFailed, Messages.PlayerForbidden, Messages.PlayerLastAdmin, Messages.PlayerListFailed, Messages.PlayerListForbidden, Messages.Unavailable })
        {
            Assert.DoesNotContain("{", s);
        }
        Assert.Equal("That player does not exist.", Messages.PlayerNotFound);
        Assert.Equal("The player could not be saved. Please try again later.", Messages.PlayerSaveFailed);
        Assert.Equal("The change could not be recorded, so nothing was saved. Please try again later.", Messages.PlayerAuditFailed);
        Assert.Equal("You are not allowed to make that change to this player.", Messages.PlayerForbidden);
        Assert.Equal("The last Admin cannot lose the Admin role.", Messages.PlayerLastAdmin);
        Assert.Equal("The game is not reachable right now. Please try again later.", Messages.Unavailable);
        Assert.Equal("The player list could not be loaded. Please try again later.", Messages.PlayerListFailed);
        Assert.Equal("You are not allowed to list players.", Messages.PlayerListForbidden);
    }
}

public class ModulesPresenterTests
{
    private const string Token = "token-1";

    private static FakeAdminClient Core() =>
        new FakeAdminClient()
            .AddModule("alpha", ModuleRunState.Ready, required: true, version: "1.2")
            .AddModule("beta", ModuleRunState.Failed, lastFailure: "exited with code 3")
            .AddModule("gamma", ModuleRunState.Stopped);

    public static IEnumerable<object[]> Actions() => new[]
    {
        new object[] { ModuleAction.Start }, new object[] { ModuleAction.Stop }, new object[] { ModuleAction.Restart },
    };

    private static List<string> Calls(FakeAdminClient core, ModuleAction action) => action switch
    {
        ModuleAction.Start => core.ModuleStarts,
        ModuleAction.Stop => core.ModuleStops,
        _ => core.ModuleRestarts,
    };

    [Fact]
    public async Task List_ShowsEveryModuleWithItsFacts()
    {
        var core = Core();
        var p = new ModulesPresenter(core, Token);
        Assert.Equal(ModulesOutcome.Ok, await p.LoadAsync());
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, p.Items.Select(i => i.Name));
        Assert.Equal(new[] { Token }, core.ModuleTokens);
        Assert.Null(p.Error);
    }

    [Fact]
    public void RunningText_IsWords_NotColour()
    {
        var core = Core();
        Assert.Equal("Running", ModulesPresenter.RunningText(core.Modules["alpha"]));
        Assert.Equal("Not running", ModulesPresenter.RunningText(core.Modules["beta"]));
        Assert.Equal("Not running", ModulesPresenter.RunningText(core.Modules["gamma"]));
        Assert.Equal("Ready", ModulesPresenter.StateText(core.Modules["alpha"]));
        Assert.Equal("Failed", ModulesPresenter.StateText(core.Modules["beta"]));
        Assert.Equal("1.2", ModulesPresenter.VersionText(core.Modules["alpha"]));
        Assert.Equal("unknown", ModulesPresenter.VersionText(new ModuleInfo { Version = "" }));
    }

    [Theory]
    [InlineData(ModuleRunState.Stopped, "Stopped")]
    [InlineData(ModuleRunState.Starting, "Starting")]
    [InlineData(ModuleRunState.Connected, "Connected")]
    [InlineData(ModuleRunState.Ready, "Ready")]
    [InlineData(ModuleRunState.Backoff, "Backoff (waiting to retry)")]
    [InlineData(ModuleRunState.Failed, "Failed")]
    [InlineData(ModuleRunState.Refused, "Refused")]
    [InlineData(ModuleRunState.Stopping, "Stopping")]
    public void StateText_NamesEveryState(ModuleRunState state, string text) =>
        Assert.Equal(text, ModulesPresenter.StateText(new ModuleInfo { State = state }));

    [Fact]
    public void NameText_MarksAMissingFolder() =>
        Assert.Equal("x (folder missing)", ModulesPresenter.NameText(new ModuleInfo { Name = "x", FolderMissing = true }));

    [Fact]
    public async Task List_CoreUnavailable_ShowsTheMessage()
    {
        var p = new ModulesPresenter(new FakeAdminClient { Unavailable = true }, Token);
        Assert.Equal(ModulesOutcome.Failed, await p.LoadAsync());
        Assert.Equal(Messages.Unavailable, p.Error);
        Assert.Empty(p.Items);
    }

    [Fact]
    public async Task List_CoreRefuses_ShowsForbidden()
    {
        var p = new ModulesPresenter(new FakeAdminClient { RefuseModuleCalls = true }, Token);
        await p.LoadAsync();
        Assert.Equal(Messages.ModuleForbidden, p.Error);
    }

    [Theory, MemberData(nameof(Actions))]
    public async Task Act_Ok_SendsNameAndToken_AndSetsNotice(ModuleAction action)
    {
        var core = Core();
        core.ModuleActionResult = AdminResult.Ok;
        var p = new ModulesPresenter(core, Token);
        Assert.Equal(ModulesOutcome.Ok, await p.ActAsync(action, "alpha"));
        Assert.Equal(new[] { "alpha" }, Calls(core, action));
        Assert.Equal(new[] { Token }, core.ModuleTokens);
        Assert.Contains("requested", p.Notice);
    }

    [Fact]
    public async Task Restart_NoticeSaysNewInstanceStartsBeforeOldStops()
    {
        var p = new ModulesPresenter(Core(), Token);
        await p.ActAsync(ModuleAction.Restart, "alpha");
        Assert.Contains("starts a new instance before it stops the old one", p.Notice);
    }

    [Theory, MemberData(nameof(Actions))]
    public async Task Act_OkWithCoreMessage_ShowsCoreMessage(ModuleAction action)
    {
        var core = Core();
        core.ModuleActionMessage = "Queued by core.";
        var p = new ModulesPresenter(core, Token);
        await p.ActAsync(action, "alpha");
        Assert.Equal("Queued by core.", p.Notice);
    }

    [Theory, MemberData(nameof(Actions))]
    public async Task Act_NotFound_ShowsNotice(ModuleAction action)
    {
        var core = Core();
        core.ModuleActionResult = AdminResult.NotFound;
        var p = new ModulesPresenter(core, Token);
        Assert.Equal(ModulesOutcome.Ok, await p.ActAsync(action, "nope"));
        Assert.Equal("That module does not exist.", p.Notice);
    }

    [Theory, MemberData(nameof(Actions))]
    public async Task Act_Refused_ShowsCoreRefusalMessage(ModuleAction action)
    {
        var core = Core();
        core.ModuleActionResult = AdminResult.Refused;
        core.ModuleActionMessage = "The supervisor is not running.";
        var p = new ModulesPresenter(core, Token);
        await p.ActAsync(action, "alpha");
        Assert.Equal("The supervisor is not running.", p.Notice);
    }

    [Theory, MemberData(nameof(Actions))]
    public async Task Act_NoChange_ShowsNotice(ModuleAction action)
    {
        var core = Core();
        core.ModuleActionResult = AdminResult.NoChange;
        var p = new ModulesPresenter(core, Token);
        await p.ActAsync(action, "alpha");
        Assert.Equal("Nothing was changed.", p.Notice);
    }

    [Theory, MemberData(nameof(Actions))]
    public async Task Act_Forbidden_ShowsTheFixedSentence_NeverCoreText(ModuleAction action)
    {
        var core = Core();
        core.ModuleActionResult = AdminResult.Forbidden;
        core.ModuleActionMessage = "internal detail";
        var p = new ModulesPresenter(core, Token);
        await p.ActAsync(action, "alpha");
        Assert.Equal(Messages.ModuleForbidden, p.Notice);
    }

    [Theory, MemberData(nameof(Actions))]
    public async Task Act_CoreUnavailable_ShowsError(ModuleAction action)
    {
        var core = Core();
        core.Unavailable = true;
        var p = new ModulesPresenter(core, Token);
        Assert.Equal(ModulesOutcome.Failed, await p.ActAsync(action, "alpha"));
        Assert.Equal(Messages.Unavailable, p.Error);
    }

    [Theory, MemberData(nameof(Actions))]
    public async Task Act_CoreRefusesCaller_ShowsForbidden(ModuleAction action)
    {
        // A creator can forge the call; core refuses it and the page says so.
        var core = Core();
        core.RefuseModuleCalls = true;
        var p = new ModulesPresenter(core, Token);
        Assert.Equal(ModulesOutcome.Failed, await p.ActAsync(action, "alpha"));
        Assert.Equal(Messages.ModuleForbidden, p.Error);
    }

    [Theory, MemberData(nameof(Actions))]
    public async Task Act_MissingName_IsSentAsEmpty(ModuleAction action)
    {
        var core = Core();
        await new ModulesPresenter(core, Token).ActAsync(action, null);
        Assert.Equal(new[] { "" }, Calls(core, action));
    }

    [Fact]
    public void Messages_ArePinned()
    {
        Assert.Equal("The game is not reachable right now. Please try again later.", Messages.Unavailable);
        Assert.Equal("You are not allowed to do that.", Messages.ModuleForbidden);
        Assert.Equal("The module request could not be completed. Please try again later.", Messages.ModuleFailed);
        Assert.Equal("The module list could not be loaded. Please try again later.", Messages.ModuleListFailed);
        Assert.Equal("Restart starts a new instance before it stops the old one.", Messages.ModuleRestartHint);
    }
}

public class PlayerModuleComponentTests
{
    private static string Read(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MUD.sln"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static Type Component(string name) =>
        typeof(IAdminClient).Assembly.GetTypes().Single(t => t.FullName == "MUD.Web.Components.Pages." + name);

    [Theory]
    [InlineData("Modules", "/creator/modules")]
    public void ThePages_RequireTheCreatorOrAdminPolicy_OnTheirRoutes(string name, string route)
    {
        var type = Component(name);
        Assert.Equal(WebPolicies.CreatorOrAdmin, type.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal(route, type.GetCustomAttribute<RouteAttribute>()!.Template);
    }

    [Theory]
    [InlineData("PlayerList", "/creator/players")]
    [InlineData("PlayerEdit", "/creator/players/{NameKey}")]
    public void ThePlayerPages_RequireTheAdminOnlyPolicy_OnTheirRoutes(string name, string route)
    {
        var type = Component(name);
        Assert.Equal(WebPolicies.AdminOnly, type.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal(route, type.GetCustomAttribute<RouteAttribute>()!.Template);
    }

    [Fact]
    public void RouteConstants_MatchThePageRoutes()
    {
        Assert.Equal(WebRoutes.Players, Component("PlayerList").GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal(WebRoutes.Modules, Component("Modules").GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal(WebRoutes.PlayerEdit("{NameKey}").Replace("%7BNameKey%7D", "{NameKey}"), Component("PlayerEdit").GetCustomAttribute<RouteAttribute>()!.Template);
    }

    [Fact]
    public void TheNameKey_ComesOnlyFromTheRoute()
    {
        var key = Component("PlayerEdit").GetProperty("NameKey")!;
        Assert.NotNull(key.GetCustomAttribute<ParameterAttribute>());
        Assert.Null(key.GetCustomAttribute<SupplyParameterFromQueryAttribute>());
        var src = Read("MUD.Web/Components/Pages/PlayerEdit.razor");
        Assert.DoesNotContain("p.NameKey =", src);
        Assert.DoesNotContain("Conflict", src);
    }

    [Fact]
    public void TheListQuery_IsTheOnlyStateOfThePage()
    {
        var type = Component("PlayerList");
        foreach (var (prop, name) in new[] { ("Filter", "q"), ("Sort", "sort"), ("PageNumber", "page") })
        {
            Assert.Equal(name, type.GetProperty(prop)!.GetCustomAttribute<SupplyParameterFromQueryAttribute>()!.Name);
        }
    }

    [Fact]
    public void Sources_NeverReferencePassword_NeverUseRawHtml_NeverTouchTheDatabase()
    {
        foreach (var f in new[] { "Components/Pages/PlayerList.razor", "Components/Pages/PlayerEdit.razor", "Components/Pages/Modules.razor", "Pages/PlayerListPresenter.cs", "Pages/PlayerEditPresenter.cs", "Pages/ModulesPresenter.cs" })
        {
            var text = Read("MUD.Web/" + f);
            Assert.DoesNotContain("Password", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("MarkupString", text);
            Assert.DoesNotContain("Mongo", text);
            Assert.DoesNotContain("<script", text);
            Assert.DoesNotContain("http://", text);
            Assert.DoesNotContain("https://", text);
        }
        var web = typeof(PlayerEditPresenter).Assembly;
        foreach (var name in new[] { "MongoPlayerEditStore", "MongoAuditLog", "IPlayerEditStore", "IPlayerWriter", "PlayerWriter", "IAuditLog", "AuditEntry", "RoomActor" })
        {
            Assert.Null(web.GetType("MUD.Web.Services." + name));
        }
    }

    [Fact]
    public void ModuleButtons_AreAdminOnly_AndStopAndRestartAskFirst()
    {
        var src = Read("MUD.Web/Components/Pages/Modules.razor");
        // The buttons are rendered only in the admin branch and the handler re-checks the role.
        Assert.Equal(2, Regex.Matches(src, @"@if \(_admin\) \{ <(td|div)").Count);
        Assert.Contains("|| !_admin", src);
        // Stop and Restart open the confirm dialog; only Start goes straight through.
        Assert.Contains("Ask(ModuleAction.Stop", src);
        Assert.Contains("Ask(ModuleAction.Restart", src);
        Assert.Contains("RunAsync(ModuleAction.Start", src);
        Assert.DoesNotContain("RunAsync(ModuleAction.Stop", src);
        Assert.DoesNotContain("RunAsync(ModuleAction.Restart", src);
        Assert.Contains("<ConfirmDialog", src);
        // The running indicator is words, and the required flag stays visible to everyone.
        Assert.Contains("RunningText(m)", src);
        Assert.Contains("m.Required", src);
    }

    [Fact]
    public void PlayerEdit_RetireAsksFirst_AndHasNoRoleOrRetireControlWithoutThePermission()
    {
        var src = Read("MUD.Web/Components/Pages/PlayerEdit.razor");
        Assert.Contains("p.CanSetRole", src);
        Assert.Contains("p.CanSetRetired", src);
        Assert.Contains("WouldRetire", src);
        Assert.Contains("<ConfirmDialog", src);
        Assert.Contains("Messages.PlayerCannotEdit", src);
    }

    [Fact]
    public void ListsAreTablesWideAndStackedCardsNarrow()
    {
        foreach (var f in new[] { "PlayerList.razor", "Modules.razor" })
        {
            var src = Read("MUD.Web/Components/Pages/" + f);
            Assert.Contains("mud-table-wrap", src);
            Assert.Contains("mud-cards", src);
        }
        var css = Read("MUD.Web/wwwroot/css/mobile.css");
        Assert.Matches(@"@media \(min-width: 600px\)\s*\{\s*\.mud-table-wrap \{ display: block; \}\s*\.mud-cards \{ display: none; \}", css);
        Assert.Contains(".mud-table-wrap { display: none;", css);
    }

    [Fact]
    public void TheNav_LinksToTheBlazorPlayerAndModulePages_UnderTheCreatorPolicy()
    {
        // The links are in the pull-out menu's sections (NavMenu): Modules in the creatorOrAdmin branch, Players in the admin-only branch.
        var nav = Read("MUD.Web/Pages/NavMenu.cs");
        var creator = nav.IndexOf("if (creatorOrAdmin)", StringComparison.Ordinal);
        Assert.True(creator >= 0);
        Assert.True(nav.IndexOf("WebRoutes.Modules", StringComparison.Ordinal) > creator);
        Assert.True(nav.IndexOf("WebRoutes.Players", StringComparison.Ordinal) > nav.IndexOf("if (admin)", StringComparison.Ordinal));
        Assert.Contains("NavMenu.SectionsFor(_creator, _admin)", Read("MUD.Web/Components/Layout/NavShell.razor"));
        Assert.DoesNotContain("LegacyPlayers", nav);
        Assert.DoesNotContain("LegacyModules", nav);
    }
}
