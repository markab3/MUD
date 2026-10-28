using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MUD.Web.Pages;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

// In-memory player store with a stored Password, so tests can prove it never comes back out.
public class FakePlayerEditStore : IPlayerEditStore
{
    public const string SecretPassword = "hunter2-SECRET";

    public Dictionary<string, PlayerDetail> Players { get; } = new();
    public List<(string Id, int ExpectedRev, BsonDocument Set)> Updates { get; } = new();
    public Action<string>? BeforeUpdate { get; set; }
    public List<string> Names { get; } = new();

    public FakePlayerEditStore Add(string name, AccountRole role = AccountRole.Player, int rev = 0, bool retired = false, BsonDocument? roles = null)
    {
        var key = name.ToLowerInvariant();
        roles ??= RoleDocument(role);
        Players[key] = new PlayerDetail("id-" + key, key, name, name, "tall", "human", "female", "fighter", retired, role, rev, roles);
        return this;
    }

    // The stored roles component: creator and admin flags plus the game-owned currentDirectory.
    public static BsonDocument RoleDocument(AccountRole role) => new()
    {
        { "creator", role >= AccountRole.Creator },
        { "admin", role == AccountRole.Admin },
        { "currentDirectory", "/home" },
    };

    public PlayerPage List(string? nameFilter, bool descending, int pageNumber, int pageSize)
    {
        var all = Players.Values
            .Where(p => string.IsNullOrWhiteSpace(nameFilter) || p.Name.Contains(nameFilter.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.NameKey, StringComparer.Ordinal).ToList();
        if (descending) { all.Reverse(); }
        var items = all.Skip((pageNumber - 1) * pageSize).Take(pageSize)
            .Select(p => new PlayerSummary(p.NameKey, p.Name, p.Race, p.Class, p.Gender, "t.hall", p.Role, p.Retired)).ToList();
        return new PlayerPage(items, all.Count, pageNumber, pageSize);
    }

    public PlayerDetail? Find(string nameKey) => Players.TryGetValue(nameKey, out var p) ? p : null;

    public StoreUpdateOutcome UpdateIfRev(string id, int expectedRev, BsonDocument set)
    {
        BeforeUpdate?.Invoke(id);
        Updates.Add((id, expectedRev, set));
        var p = Players.Values.FirstOrDefault(x => x.Id == id);
        if (p == null) { return StoreUpdateOutcome.Missing; }
        if (p.Rev != expectedRev) { return StoreUpdateOutcome.Conflict; }
        string? S(string f, string? cur) => set.Contains(f) ? (set[f].IsBsonNull ? null : set[f].AsString) : cur;
        var roles = set.Contains("roles") ? set["roles"].AsBsonDocument : p.RolesDocument;
        var role = MongoAccountStore.RoleFrom(new BsonDocument("roles", roles));
        Players[p.NameKey] = p with
        {
            Short = S("ShortDescription", p.Short), Long = S("LongDescription", p.Long), Race = S("Race", p.Race),
            Gender = S("Gender", p.Gender), Class = S("Class", p.Class),
            Retired = set.Contains("Retired") ? set["Retired"].AsBoolean : p.Retired,
            RolesDocument = roles, Role = role, Rev = p.Rev + 1,
        };
        return StoreUpdateOutcome.Applied;
    }

    public int CountAdmins() => Players.Values.Count(p => p.Role == AccountRole.Admin);
}

public class CapturingLogger : ILogger
{
    public List<string> Lines { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Add(formatter(state, exception) + (exception?.ToString() ?? string.Empty));
}

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
    [InlineData(AccountRole.Admin, AccountRole.Creator, 1, true)]    // lowering the only Admin
    [InlineData(AccountRole.Admin, AccountRole.Player, 1, true)]
    [InlineData(AccountRole.Admin, AccountRole.Player, 2, false)]    // another Admin remains
    [InlineData(AccountRole.Admin, AccountRole.Admin, 1, false)]     // not a demotion
    [InlineData(AccountRole.Creator, AccountRole.Player, 1, false)]  // target is not an Admin
    public void LastAdminGuard(AccountRole target, AccountRole newRole, int admins, bool refuses) =>
        Assert.Equal(refuses, PlayerRules.WouldRemoveLastAdmin(target, newRole, admins));
}

public class PlayerWriterTests
{
    private static readonly RoomActor Creator = new("id-cat", "Cat", AccountRole.Creator);
    private static readonly RoomActor Admin = new("id-ada", "Ada", AccountRole.Admin);

    private static (PlayerWriter Writer, FakePlayerEditStore Store, FakeAuditLog Audit, CapturingLogger Log) Make()
    {
        var store = new FakePlayerEditStore()
            .Add("Ann").Add("Ben", AccountRole.Creator).Add("Ada", AccountRole.Admin).Add("Eve", AccountRole.Admin, 0);
        var audit = new FakeAuditLog();
        var log = new CapturingLogger();
        return (new PlayerWriter(store, audit, null, log), store, audit, log);
    }

    private static PlayerEdit Edit(PlayerDetail cur, string? shortText = null, string? longText = null, string? race = null, string? gender = null,
        string? cls = null, bool? retired = null, AccountRole? role = null, int? rev = null) =>
        new(cur.NameKey, shortText ?? cur.Short, longText ?? cur.Long, race ?? cur.Race, gender ?? cur.Gender, cls ?? cur.Class,
            retired ?? cur.Retired, role ?? cur.Role, rev ?? cur.Rev);

    // ---- authorization matrix: editor role x target role ----

    [Theory]
    [InlineData("Creator", "Ann", PlayerEditStatus.Saved)]
    [InlineData("Creator", "Ben", PlayerEditStatus.Forbidden)]
    [InlineData("Creator", "Ada", PlayerEditStatus.Forbidden)]
    [InlineData("Admin", "Ann", PlayerEditStatus.Saved)]
    [InlineData("Admin", "Ben", PlayerEditStatus.Saved)]
    [InlineData("Admin", "Ada", PlayerEditStatus.Forbidden)]
    [InlineData("Player", "Ann", PlayerEditStatus.Forbidden)]
    [InlineData("Player", "Ben", PlayerEditStatus.Forbidden)]
    public void Matrix_EditingTextFields(string editorRole, string target, PlayerEditStatus expected)
    {
        var (w, store, audit, _) = Make();
        var actor = new RoomActor("id-x", "Zed", Enum.Parse<AccountRole>(editorRole));
        var cur = store.Find(target.ToLowerInvariant())!;
        var result = w.Save(actor, Edit(cur, shortText: "edited"));
        Assert.Equal(expected, result.Status);
        if (expected != PlayerEditStatus.Saved)
        {
            Assert.Empty(store.Updates);
            Assert.Empty(audit.Entries);
        }
    }

    [Fact]
    public void Creator_CannotChangeRoleOrRetirement_EvenOfAPlayer()
    {
        var (w, store, audit, _) = Make();
        var ann = store.Find("ann")!;
        Assert.Equal(PlayerEditStatus.Forbidden, w.Save(Creator, Edit(ann, role: AccountRole.Creator)).Status);
        Assert.Equal(PlayerEditStatus.Forbidden, w.Save(Creator, Edit(ann, retired: true)).Status);
        Assert.Empty(store.Updates);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public void Admin_GrantsCreator_AddsOnlyTheRoleElement_AndAudits()
    {
        var (w, store, audit, _) = Make();
        var ann = store.Find("ann")!;
        var result = w.Save(Admin, Edit(ann, role: AccountRole.Creator));
        Assert.Equal(PlayerEditStatus.Saved, result.Status);
        Assert.Equal(AccountRole.Creator, store.Find("ann")!.Role);
        var set = Assert.Single(store.Updates).Set;
        Assert.Equal(new[] { "roles", PlayerFields_WebUpdatedAt }, set.Names.ToArray());
        // Only the flags change; the game-owned currentDirectory in the same document is kept.
        Assert.Equal("/home", store.Find("ann")!.RolesDocument["currentDirectory"].AsString);
        Assert.True(store.Find("ann")!.RolesDocument["creator"].AsBoolean);
        Assert.False(store.Find("ann")!.RolesDocument["admin"].AsBoolean);
        var row = Assert.Single(audit.Entries);
        Assert.Equal("Role", row.Field);
        Assert.Equal("Player", row.Old.AsString);
        Assert.Equal("Creator", row.New.AsString);
        Assert.Equal("player", row.Entity);
        Assert.Equal("Admin", row.ActorRole);
    }

    private const string PlayerFields_WebUpdatedAt = "WebUpdatedAt";

    [Fact]
    public void Admin_RevokesCreator_KeepsTheGameOwnedDirectory()
    {
        var (w, store, _, _) = Make();
        var ben = store.Find("ben")!;
        Assert.Equal(PlayerEditStatus.Saved, w.Save(Admin, Edit(ben, role: AccountRole.Player)).Status);
        var after = store.Find("ben")!;
        Assert.Equal(AccountRole.Player, after.Role);
        Assert.False(after.RolesDocument["creator"].AsBoolean);
        Assert.False(after.RolesDocument["admin"].AsBoolean);
        Assert.Equal("/home", after.RolesDocument["currentDirectory"].AsString);
    }

    [Fact]
    public void GrantingARoleHigherThanOwn_IsRefused()
    {
        var (w, store, audit, _) = Make();
        var ann = store.Find("ann")!;
        Assert.Equal(PlayerEditStatus.Forbidden, w.Save(Creator, Edit(ann, role: AccountRole.Admin)).Status);
        // An Admin may name Admin as a role value, but nobody grants Admin through this page's controls; the rule is "not above your own".
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public void Nobody_EditsAnAdmin_NotEvenAnotherAdmin_NorThemselves()
    {
        var (w, store, audit, _) = Make();
        var eve = store.Find("eve")!;
        Assert.Equal(PlayerEditStatus.Forbidden, w.Save(Admin, Edit(eve, shortText: "x")).Status);
        var selfActor = new RoomActor("id-ada", "Ada", AccountRole.Admin);
        var ada = store.Find("ada")!;
        Assert.Equal(PlayerEditStatus.Forbidden, w.Save(selfActor, Edit(ada, role: AccountRole.Player)).Status);
        Assert.Empty(audit.Entries);
    }

    // ---- last Admin guard (defensive: checked before the generic rule) ----

    [Fact]
    public void LastAdmin_CannotLoseTheRole()
    {
        var store = new FakePlayerEditStore().Add("Ada", AccountRole.Admin);
        var w = new PlayerWriter(store, new FakeAuditLog());
        var ada = store.Find("ada")!;
        var result = w.Save(Admin, Edit(ada, role: AccountRole.Player));
        Assert.Equal(PlayerEditStatus.LastAdmin, result.Status);
        Assert.Empty(store.Updates);
        Assert.Equal(AccountRole.Admin, store.Find("ada")!.Role);
    }

    [Fact]
    public void WithTwoAdmins_TheGuardDoesNotFire_ButTheEqualRoleRuleStillBlocks()
    {
        var (w, store, _, _) = Make();
        var result = w.Save(Admin, Edit(store.Find("ada")!, role: AccountRole.Player));
        Assert.Equal(PlayerEditStatus.Forbidden, result.Status);
    }

    // ---- fields written ----

    [Fact]
    public void Save_SetsOnlyChangedAllowListedFields_PlusWebUpdatedAt_AndNothingElse()
    {
        var (w, store, audit, _) = Make();
        var ann = store.Find("ann")!;
        var result = w.Save(Creator, Edit(ann, shortText: "Ann the Bold", race: "elf"));
        Assert.Equal(PlayerEditStatus.Saved, result.Status);
        var (id, expectedRev, set) = Assert.Single(store.Updates);
        Assert.Equal("id-ann", id);
        Assert.Equal(0, expectedRev);
        Assert.Equal(new[] { "ShortDescription", "Race", "WebUpdatedAt" }, set.Names.ToArray());
        Assert.Equal(1, store.Find("ann")!.Rev);
        Assert.Equal(new[] { "ShortDescription", "Race" }, audit.Entries.Select(e => e.Field).ToArray());
        Assert.All(audit.Entries, e => { Assert.Equal(0, e.RevBefore); Assert.Equal(1, e.RevAfter); Assert.True(audit.Outcomes[e.Id]); });
    }

    [Fact]
    public void Save_NeverWritesPasswordNameInventoryEquipmentLocationOrId()
    {
        var (w, store, _, _) = Make();
        var ann = store.Find("ann")!;
        w.Save(Admin, Edit(ann, shortText: "a", longText: "b", race: "c", gender: "d", cls: "e", retired: true, role: AccountRole.Creator));
        var set = Assert.Single(store.Updates).Set;
        foreach (var banned in new[] { "Password", "NameKey", "PlayerName", "Inventory", "WornItems", "HeldItems", "CurrentLocationId", "_id", "SelectedTerm", "Rev" })
        {
            Assert.False(set.Contains(banned), banned);
        }
        Assert.True(set.Names.All(n => n == "WebUpdatedAt" || n == "roles" || MUD.Data.PlayerFields.WebEditable.Contains(n)));
    }

    [Fact]
    public void Retire_ByAdmin_SetsTheFlagOnly_AndAudits()
    {
        var (w, store, audit, _) = Make();
        var ann = store.Find("ann")!;
        Assert.Equal(PlayerEditStatus.Saved, w.Save(Admin, Edit(ann, retired: true)).Status);
        Assert.True(store.Find("ann")!.Retired);
        Assert.Equal(new[] { "Retired", "WebUpdatedAt" }, store.Updates.Single().Set.Names.ToArray());
        var row = Assert.Single(audit.Entries);
        Assert.False(row.Old.AsBoolean);
        Assert.True(row.New.AsBoolean);
    }

    [Fact]
    public void Unretire_ByAdmin_IsAllowed()
    {
        var store = new FakePlayerEditStore().Add("Ann", retired: true).Add("Ada", AccountRole.Admin);
        var w = new PlayerWriter(store, new FakeAuditLog());
        Assert.Equal(PlayerEditStatus.Saved, w.Save(Admin, Edit(store.Find("ann")!, retired: false)).Status);
        Assert.False(store.Find("ann")!.Retired);
    }

    [Fact]
    public void NoChange_WritesNothing()
    {
        var (w, store, audit, _) = Make();
        var result = w.Save(Creator, Edit(store.Find("ann")!));
        Assert.Equal(PlayerEditStatus.NoChange, result.Status);
        Assert.Empty(store.Updates);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public void UnknownPlayer_IsNotFound()
    {
        var (w, _, _, _) = Make();
        Assert.Equal(PlayerEditStatus.NotFound, w.Save(Creator, new PlayerEdit("nobody", "s", null, null, null, null, false, AccountRole.Player, 0)).Status);
    }

    [Fact]
    public void AnonymousOrPlayerActor_IsForbidden()
    {
        var (w, store, _, _) = Make();
        Assert.Equal(PlayerEditStatus.Forbidden, w.Save(null!, Edit(store.Find("ann")!, shortText: "x")).Status);
        Assert.Equal(PlayerEditStatus.Forbidden, w.Save(new RoomActor("id-p", "P", AccountRole.Player), Edit(store.Find("ann")!, shortText: "x")).Status);
    }

    // ---- validation ----

    public static IEnumerable<object?[]> BadInputs() => new[]
    {
        new object?[] { "", "ok", "human", "x", "y" },                         // short required
        new object?[] { new string('s', 101), "ok", "human", "x", "y" },       // short too long
        new object?[] { "a\u0007b", "ok", "human", "x", "y" },                 // control char in short
        new object?[] { "ok", new string('l', 4001), "human", "x", "y" },      // long too long
        new object?[] { "ok", "bad\u0001text", "human", "x", "y" },            // control char in long
        new object?[] { "ok", "ok", new string('r', 33), "x", "y" },           // race too long
        new object?[] { "ok", "ok", "hu\u0000man", "x", "y" },                 // control char in race
        new object?[] { "ok", "ok", "human", new string('g', 33), "y" },       // gender too long
        new object?[] { "ok", "ok", "human", "x", "cl\nass" },                 // control char in class
    };

    [Theory]
    [MemberData(nameof(BadInputs))]
    public void InvalidInput_IsRejected_WritesNothing(string? shortText, string? longText, string? race, string? gender, string? cls)
    {
        var (w, store, audit, _) = Make();
        var ann = store.Find("ann")!;
        var result = w.Save(Creator, new PlayerEdit("ann", shortText, longText, race, gender, cls, false, AccountRole.Player, 0));
        Assert.Equal(PlayerEditStatus.Invalid, result.Status);
        Assert.NotEmpty(result.FieldErrors);
        Assert.Empty(store.Updates);
        Assert.Empty(audit.Entries);
        Assert.Equal(ann, store.Find("ann"));
    }

    [Fact]
    public void ValidationMessages_DoNotRepeatTheSubmittedValue()
    {
        var (w, store, _, _) = Make();
        var result = w.Save(Creator, new PlayerEdit("ann", "ok", "ok", "XX" + new string('r', 40), "x", "y", false, AccountRole.Player, 0));
        Assert.All(result.FieldErrors, e => Assert.DoesNotContain("XXrrr", e.Message));
    }

    [Fact]
    public void FreeText_AcceptsAnyVocabulary_AndBlankClearsTheField()
    {
        var (w, store, _, _) = Make();
        var ann = store.Find("ann")!;
        var result = w.Save(Creator, new PlayerEdit("ann", "ok", "", "  Womble ", "", "Wizard of Wimbledon", false, AccountRole.Player, 0));
        Assert.Equal(PlayerEditStatus.Saved, result.Status);
        var after = store.Find("ann")!;
        Assert.Equal("Womble", after.Race);
        Assert.Null(after.Gender);
        Assert.Null(after.Long);
        Assert.Equal("Wizard of Wimbledon", after.Class);
    }

    // ---- conflict and stale save ----

    [Fact]
    public void StaleRev_IsAConflict_NothingIsWritten_AndTheCurrentValuesAreReturned()
    {
        var store = new FakePlayerEditStore().Add("Ann", rev: 3);
        var audit = new FakeAuditLog();
        var w = new PlayerWriter(store, audit);
        var result = w.Save(Creator, new PlayerEdit("ann", "mine", null, "human", "female", "fighter", false, AccountRole.Player, 2));
        Assert.Equal(PlayerEditStatus.Conflict, result.Status);
        Assert.Equal(3, result.Current!.Rev);
        Assert.Equal("Ann", store.Find("ann")!.Short);
        Assert.Empty(store.Updates);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public void RaceBetweenReadAndWrite_ConflictFromTheStore_RejectsTheAuditRows()
    {
        var store = new FakePlayerEditStore().Add("Ann", rev: 0);
        var audit = new FakeAuditLog();
        var w = new PlayerWriter(store, audit);
        // Someone else saves between our read and our conditional write.
        store.BeforeUpdate = _ => store.Players["ann"] = store.Players["ann"] with { Rev = 1, Short = "theirs" };
        var result = w.Save(Creator, new PlayerEdit("ann", "mine", null, "human", "female", "fighter", false, AccountRole.Player, 0));
        Assert.Equal(PlayerEditStatus.Conflict, result.Status);
        Assert.Equal("theirs", result.Current!.Short);
        Assert.All(audit.Entries, e => Assert.False(audit.Outcomes[e.Id])); // never marked applied
        Assert.Equal("theirs", store.Find("ann")!.Short);
    }

    [Fact]
    public void TwoEditorsSameRev_OnlyTheFirstWins()
    {
        var (w, store, _, _) = Make();
        var ann = store.Find("ann")!;
        Assert.Equal(PlayerEditStatus.Saved, w.Save(Creator, Edit(ann, shortText: "first")).Status);
        Assert.Equal(PlayerEditStatus.Conflict, w.Save(Creator, Edit(ann, shortText: "second")).Status);
        Assert.Equal("first", store.Find("ann")!.Short);
    }

    // ---- audit-first ----

    [Fact]
    public void AuditFailure_RefusesTheEdit_AndNothingChanges()
    {
        var (w, store, audit, _) = Make();
        audit.FailInsert = true;
        var result = w.Save(Creator, Edit(store.Find("ann")!, shortText: "x"));
        Assert.Equal(PlayerEditStatus.AuditFailed, result.Status);
        Assert.Empty(store.Updates);
        Assert.Equal("Ann", store.Find("ann")!.Short);
    }

    [Fact]
    public void Audit_IsWrittenBeforeTheChange_AsNotYetApplied()
    {
        var (w, store, audit, _) = Make();
        bool auditedBefore = false;
        store.BeforeUpdate = _ => auditedBefore = audit.Entries.Count == 1 && audit.Entries.All(e => audit.Outcomes[e.Id] == false);
        w.Save(Creator, Edit(store.Find("ann")!, shortText: "x"));
        Assert.True(auditedBefore);
    }

    // ---- Password never appears ----

    [Fact]
    public void Password_NeverAppearsInAuditRowsOrLogs_AcrossAFullEditFlow()
    {
        var (w, store, audit, log) = Make();
        var ann = store.Find("ann")!;
        w.Save(Admin, Edit(ann, shortText: "a", longText: "b", race: "c", gender: "d", cls: "e", retired: true, role: AccountRole.Creator));
        w.Save(Admin, Edit(store.Find("ann")!, shortText: "z"));
        audit.FailInsert = true;
        w.Save(Admin, Edit(store.Find("ann")!, shortText: "q"));          // audit-failure log path
        store.BeforeUpdate = _ => throw new InvalidOperationException("boom " + FakePlayerEditStore.SecretPassword);
        audit.FailInsert = false;
        w.Save(Admin, Edit(store.Find("ann")!, shortText: "r"));          // exception log path

        var everything = string.Join("|", audit.Entries.Select(e => $"{e.Field}{e.Old}{e.New}{e.ActorName}{e.EntityName}{e.EntityId}")) + string.Join("|", log.Lines);
        Assert.DoesNotContain(FakePlayerEditStore.SecretPassword, everything);
        Assert.DoesNotContain("Password", string.Join("|", audit.Entries.Select(e => e.Field)));
        Assert.DoesNotContain("boom", string.Join("|", log.Lines)); // exception text is never logged either
    }

    [Fact]
    public void UnexpectedStoreFailure_IsAGenericFailure_NoDetails()
    {
        var (w, store, _, _) = Make();
        store.BeforeUpdate = _ => throw new InvalidOperationException("secret detail");
        var result = w.Save(Creator, Edit(store.Find("ann")!, shortText: "x"));
        Assert.Equal(PlayerEditStatus.Failed, result.Status);
        Assert.Null(result.Current);
    }

    [Fact]
    public void EditModelsAndRecords_HaveNoPasswordProperty()
    {
        foreach (var t in new[] { typeof(PlayerDetail), typeof(PlayerSummary), typeof(PlayerEdit), typeof(PlayerEditModel), typeof(PlayerListModel), typeof(AuditEntry), typeof(PlayerEditResult) })
        {
            Assert.DoesNotContain(t.GetProperties(), p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
        }
    }
}

public class PlayerPagesTests
{
    private static ClaimsPrincipal User(string? name, string? role) =>
        new(new ClaimsIdentity(name == null ? Array.Empty<Claim>() : new[] { new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.Role, role!) }, name == null ? null : "test"));

    private static T Wire<T>(T model, ClaimsPrincipal user) where T : PageModel
    {
        var http = new DefaultHttpContext { User = user };
        model.PageContext = new PageContext { HttpContext = http };
        return model;
    }

    private static (PlayerEditModel Page, FakePlayerEditStore Store, FakeAuditLog Audit) EditPage(string editorName, AccountRole editorRole, FakePlayerEditStore? store = null)
    {
        store ??= new FakePlayerEditStore().Add("Ann").Add("Ben", AccountRole.Creator).Add("Ada", AccountRole.Admin).Add("Cat", AccountRole.Creator).Add("Eve", AccountRole.Admin);
        var audit = new FakeAuditLog();
        var accounts = new FakeAccounts().Add(editorName, editorRole);
        var page = Wire(new PlayerEditModel(new PlayerWriter(store, audit), store, accounts), User(editorName, editorRole.ToString()));
        return (page, store, audit);
    }

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
        var ctx = new DefaultHttpContext { User = user };
        var auth = role == null ? Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult()
            : Microsoft.AspNetCore.Authentication.AuthenticateResult.Success(new Microsoft.AspNetCore.Authentication.AuthenticationTicket(user, "test"));
        var result = await evaluator.AuthorizeAsync(policy, auth, ctx, null);
        Assert.Equal(succeeds, result.Succeeded);
        Assert.Equal(forbidden, result.Forbidden);
        Assert.Equal(role == null, result.Challenged);
    }

    // ---- list ----

    [Fact]
    public void List_ShowsRolesAndOnlineUnknown_PagesAt50_Sorts_AndFilters()
    {
        var store = new FakePlayerEditStore();
        for (var i = 0; i < 120; i++) { store.Add("P" + i.ToString("D3")); }
        store.Add("Zelda", AccountRole.Admin).Add("Bob", AccountRole.Creator);
        var list = Wire(new PlayerListModel(store), User("Ada", "Admin"));

        list.OnGet();
        Assert.Equal(50, PlayerListModel.PageSize);
        Assert.Equal(50, list.Result.Items.Count);
        Assert.Equal(122, list.Result.Total);
        Assert.Equal(3, list.Result.PageCount);
        Assert.Equal("Bob", list.Result.Items[0].Name);
        Assert.Equal("unknown", PlayerListModel.OnlineText(list.Result.Items[0]));
        Assert.Equal("Creator", PlayerListModel.RoleText(list.Result.Items[0].Role));

        var desc = Wire(new PlayerListModel(store) { Sort = "desc" }, User("Ada", "Admin"));
        desc.OnGet();
        Assert.Equal("Zelda", desc.Result.Items[0].Name);

        var last = Wire(new PlayerListModel(store) { PageNumber = 99 }, User("Ada", "Admin"));
        last.OnGet();
        Assert.Equal(3, last.Result.Page);
        Assert.Equal(22, last.Result.Items.Count);

        var filtered = Wire(new PlayerListModel(store) { Filter = " zel " }, User("Ada", "Admin"));
        filtered.OnGet();
        Assert.Equal("Zelda", Assert.Single(filtered.Result.Items).Name);
    }

    [Fact]
    public void List_StoreFailure_ShowsAFixedMessage()
    {
        var list = Wire(new PlayerListModel(new ThrowingPlayerStore()), User("Ada", "Admin"));
        list.OnGet();
        Assert.Equal(PlayerListModel.FailedError, list.Error);
    }

    private class ThrowingPlayerStore : IPlayerEditStore
    {
        public PlayerPage List(string? nameFilter, bool descending, int pageNumber, int pageSize) => throw new InvalidOperationException("secret");
        public PlayerDetail? Find(string nameKey) => throw new InvalidOperationException("secret");
        public StoreUpdateOutcome UpdateIfRev(string id, int expectedRev, BsonDocument set) => throw new InvalidOperationException("secret");
        public int CountAdmins() => throw new InvalidOperationException("secret");
    }

    [Fact]
    public void EditUrl_EscapesTheKey()
    {
        Assert.Equal("/creator/players/ann", PlayerListModel.EditUrl("ann"));
        Assert.DoesNotContain("\"", PlayerListModel.EditUrl("a\"b<c>"));
        Assert.DoesNotContain("<", PlayerEditModel.PlayerUrl("a\"b<c>"));
    }

    // ---- edit page ----

    [Fact]
    public void EditModel_BindsOnlyTheAllowList_NoPasswordNameKeyInventoryOrLocation()
    {
        var bound = typeof(PlayerEditModel).GetProperties()
            .Where(p => p.GetCustomAttributes(typeof(BindPropertyAttribute), true).Any()).Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Class", "Gender", "Long", "Race", "Retired", "Rev", "Role", "Short" }, bound);
    }

    [Fact]
    public void Get_UnknownPlayer_Is404()
    {
        var (page, _, _) = EditPage("Ada", AccountRole.Admin);
        page.OnGet("nobody");
        Assert.Equal(404, page.Response.StatusCode);
        Assert.True(page.Missing);
    }

    [Fact]
    public void Get_ByCreator_OfAPlayer_AllowsEditing_ButNotRoleOrRetire()
    {
        var (page, _, _) = EditPage("Cat", AccountRole.Creator);
        page.OnGet("ann");
        Assert.True(page.CanEdit);
        Assert.False(page.CanSetRole);
        Assert.False(page.CanSetRetired);
        Assert.Equal("Ann", page.Short);
    }

    [Fact]
    public void Get_ByCreator_OfCreatorOrAdmin_ShowsNoEditControls()
    {
        var (page, _, _) = EditPage("Cat", AccountRole.Creator);
        page.OnGet("ben");
        Assert.False(page.CanEdit);
        page.OnGet("ada");
        Assert.False(page.CanEdit);
    }

    [Fact]
    public void Get_ByAdmin_OfAnAdmin_IsReadOnly_AdminOfPlayerAndCreatorIsEditable()
    {
        var (page, _, _) = EditPage("Ada", AccountRole.Admin);
        page.OnGet("eve");
        Assert.False(page.CanEdit);
        page.OnGet("ann");
        Assert.True(page.CanEdit);
        Assert.True(page.CanSetRole);
        Assert.True(page.CanSetRetired);
        page.OnGet("ben");
        Assert.True(page.CanEdit);
    }

    [Fact]
    public void Post_Success_ReloadsNewRev_AndShowsNotice()
    {
        var (page, store, audit) = EditPage("Cat", AccountRole.Creator);
        page.OnGet("ann");
        page.Short = "Ann the Bold";
        page.OnPost("ann");
        Assert.Equal(1, page.Rev);
        Assert.NotNull(page.Notice);
        Assert.Null(page.Error);
        Assert.Equal("Ann the Bold", store.Find("ann")!.Short);
        Assert.NotEmpty(audit.Entries);
    }

    [Fact]
    public void Post_ByCreator_TryingToEditACreator_IsRefused_ServerSide()
    {
        var (page, store, audit) = EditPage("Cat", AccountRole.Creator);
        page.OnGet("ben");
        page.Short = "hacked";
        page.OnPost("ben");
        Assert.Equal(PlayerEditModel.ForbiddenError, page.Error);
        Assert.Equal("Ben", store.Find("ben")!.Short);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public void Post_ForgedRoleAndRetiredFields_ByACreator_AreRefused()
    {
        var (page, store, _) = EditPage("Cat", AccountRole.Creator);
        page.OnGet("ann");
        page.Role = AccountRole.Creator;      // not offered to a Creator, but the form can be forged
        page.OnPost("ann");
        Assert.Equal(PlayerEditModel.ForbiddenError, page.Error);
        Assert.Equal(AccountRole.Player, store.Find("ann")!.Role);

        var (page2, store2, _) = EditPage("Cat", AccountRole.Creator);
        page2.OnGet("ann");
        page2.Retired = true;
        page2.OnPost("ann");
        Assert.Equal(PlayerEditModel.ForbiddenError, page2.Error);
        Assert.False(store2.Find("ann")!.Retired);
    }

    [Fact]
    public void Post_ByAPlayerRoleCookie_WhoIsNoLongerACreator_IsRefused_RoleComesFromTheStore()
    {
        // The cookie says Creator, but the stored account is now a plain Player (revoked): power is gone at once.
        var store = new FakePlayerEditStore().Add("Ann");
        var accounts = new FakeAccounts().Add("Cat", AccountRole.Player);
        var page = Wire(new PlayerEditModel(new PlayerWriter(store, new FakeAuditLog()), store, accounts), User("Cat", "Creator"));
        page.OnGet("ann");
        page.Short = "x";
        page.OnPost("ann");
        Assert.Equal(PlayerEditModel.ForbiddenError, page.Error);
        Assert.Equal("Ann", store.Find("ann")!.Short);
    }

    [Fact]
    public void Post_Anonymous_OrRetiredEditor_IsRefused()
    {
        var store = new FakePlayerEditStore().Add("Ann");
        var accounts = new RetiredAccounts();
        var page = Wire(new PlayerEditModel(new PlayerWriter(store, new FakeAuditLog()), store, accounts), User("Cat", "Creator"));
        page.OnGet("ann");
        page.Short = "x";
        page.OnPost("ann");
        Assert.Equal(PlayerEditModel.ForbiddenError, page.Error);
        Assert.Equal("Ann", store.Find("ann")!.Short);
    }

    private class RetiredAccounts : IAccountService
    {
        public Account? Authenticate(string name, string password) => null;
        public Account? GetByName(string name) => new Account("Cat", AccountRole.Creator, "id-cat", Retired: true);
        public PasswordChangeResult ChangePassword(string name, string c, string n, string f) => PasswordChangeResult.SaveFailed;
    }

    [Fact]
    public void Post_StaleRev_ShowsConflict_KeepsInput_ShowsCurrentValues()
    {
        var (page, store, _) = EditPage("Cat", AccountRole.Creator);
        page.OnGet("ann");
        // Someone else saves first.
        store.Players["ann"] = store.Players["ann"] with { Rev = 5, Short = "theirs" };
        page.Short = "mine";
        page.OnPost("ann");
        Assert.True(page.Conflict);
        Assert.Equal(PlayerEditModel.ConflictError, page.Error);
        Assert.Equal("mine", page.Short);                 // the user's rejected input is preserved
        Assert.Equal("theirs", page.Current!.Short);       // beside the current stored values
        Assert.Equal(5, page.Rev);
        Assert.Equal("theirs", store.Find("ann")!.Short);
    }

    [Fact]
    public void Post_Invalid_ShowsFixedMessages_AndKeepsInput()
    {
        var (page, store, _) = EditPage("Cat", AccountRole.Creator);
        page.OnGet("ann");
        page.Short = "";
        page.OnPost("ann");
        Assert.False(page.ModelState.IsValid);
        Assert.Equal("", page.Short);
        Assert.Equal("Ann", store.Find("ann")!.Short);
    }

    [Fact]
    public void Post_LastAdmin_ShowsTheLastAdminMessage()
    {
        var store = new FakePlayerEditStore().Add("Ada", AccountRole.Admin);
        var (page, _, _) = EditPage("Ada", AccountRole.Admin, store);
        page.OnGet("ada");
        page.Role = AccountRole.Player;
        page.OnPost("ada");
        Assert.Equal(PlayerEditModel.LastAdminError, page.Error);
        Assert.Equal(AccountRole.Admin, store.Find("ada")!.Role);
    }

    [Fact]
    public void Post_AuditFailure_ShowsFixedMessage_NothingSaved()
    {
        var store = new FakePlayerEditStore().Add("Ann");
        var audit = new FakeAuditLog { FailInsert = true };
        var page = Wire(new PlayerEditModel(new PlayerWriter(store, audit), store, new FakeAccounts().Add("Cat", AccountRole.Creator)), User("Cat", "Creator"));
        page.OnGet("ann");
        page.Short = "x";
        page.OnPost("ann");
        Assert.Equal(PlayerEditModel.AuditFailedError, page.Error);
        Assert.Equal("Ann", store.Find("ann")!.Short);
    }

    // ---- source checks ----

    [Fact]
    public void PageSources_NeverReferencePassword_AndUseNoRawHtml_AndPostFormsHaveAntiforgery()
    {
        // Only the two page views and their models: they must not mention Password at all (comments included).
        foreach (var f in new[] { "PlayerList.cshtml", "PlayerList.cshtml.cs", "PlayerEdit.cshtml", "PlayerEdit.cshtml.cs" })
        {
            var text = File.ReadAllText(Find("MUD.Web/Pages/" + f));
            Assert.DoesNotContain("Password", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Html.Raw", text);
        }
        var edit = File.ReadAllText(Find("MUD.Web/Pages/PlayerEdit.cshtml"));
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(edit, "<form method=\"post\"").Count);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(edit, "Html.AntiForgeryToken\\(\\)").Count);
    }

    [Fact]
    public void Nav_HasAPlayersLink_UnderTheCreatorPolicy()
    {
        var layout = File.ReadAllText(Find("MUD.Web/Pages/Shared/_Layout.cshtml"));
        var policyIndex = layout.IndexOf("CreatorOrAdmin", StringComparison.Ordinal);
        var linkIndex = layout.IndexOf("href=\"/creator/players\"", StringComparison.Ordinal);
        Assert.True(policyIndex >= 0 && linkIndex > policyIndex);
    }

    [Fact]
    public void MongoStore_ReadProjection_IsInclusionOnly_AndNeverNamesPassword()
    {
        var src = File.ReadAllText(Find("MUD.Web/Services/MongoPlayerEditStore.cs"));
        // The only mentions are the comment explaining the rule; no Include/Exclude call names it.
        Assert.DoesNotContain("Include(\"Password\")", src);
        Assert.DoesNotContain("Exclude(", src);
        Assert.DoesNotContain("\"Password\"", src);
    }

    [Fact]
    public void StoreMapping_DropsAPasswordEvenIfTheDocumentCarriesOne()
    {
        // Defence in depth: the projection never asks for it, but if a document with one reached the mapping, nothing
        // that the pages render (every string property of the summary and the detail) may contain it.
        var doc = new BsonDocument
        {
            { "_id", "p.ann" }, { "PlayerName", "Ann" }, { "NameKey", "ann" }, { "ShortDescription", "Ann" }, { "LongDescription", "tall" },
            { "Race", "human" }, { "Gender", "f" }, { "Class", "fighter" }, { "CurrentLocationId", "t.hall" }, { "Retired", false }, { "Rev", 2 },
            { "Password", FakePlayerEditStore.SecretPassword }, { "roles", new BsonDocument() },
        };
        var all = new List<string>();
        foreach (var o in new object[] { MongoPlayerEditStore.Summary(doc), MongoPlayerEditStore.Detail(doc) })
        {
            foreach (var p in o.GetType().GetProperties())
            {
                var v = p.GetValue(o);
                if (v is BsonArray) { continue; } // role elements; never rendered, and contain no credential
                all.Add(v?.ToString() ?? string.Empty);
            }
        }
        Assert.DoesNotContain(FakePlayerEditStore.SecretPassword, string.Join("|", all));
    }

    [Fact]
    public void MongoFilter_EscapesTheUserText_SoItCannotActAsAPattern()
    {
        var serializer = MongoDB.Bson.Serialization.BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>();
        var rendered = MongoPlayerEditStore.Filter("a.*(b").Render(serializer, MongoDB.Bson.Serialization.BsonSerializer.SerializerRegistry);
        var regex = rendered["PlayerName"].AsBsonRegularExpression;
        Assert.Equal(System.Text.RegularExpressions.Regex.Escape("a.*(b"), regex.Pattern);
        Assert.Equal("i", regex.Options);
        Assert.Equal(0, MongoPlayerEditStore.Filter("   ").Render(serializer, MongoDB.Bson.Serialization.BsonSerializer.SerializerRegistry).ElementCount);
    }

    private static string Find(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MUD.sln"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        var p = Path.GetFullPath(Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
        return p;
    }
}
