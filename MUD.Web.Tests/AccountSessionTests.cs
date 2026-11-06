using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MUD.Contracts;
using MUD.Web.Pages;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

// A fake of core's Admin service as the web sees it. Tests drive the outcome; nothing here touches a database or a socket.
public class FakeAdminClient : IAdminClient
{
    public AuthResult AuthenticateResult { get; set; } = AuthResult.AuthOk;
    public string IssuedToken { get; set; } = "token-1";
    public bool Unavailable { get; set; }
    public List<(string Name, string Password, string RemoteKey)> Authentications { get; } = new();
    public List<string> LoggedOut { get; } = new();
    public Dictionary<string, SessionInfoReply> Sessions { get; } = new();
    public PasswordChangeOutcome PasswordOutcome { get; set; } = PasswordChangeOutcome.Changed;
    public string PasswordMessage { get; set; } = string.Empty;
    public List<(string Token, string Current, string New, string Confirm)> PasswordChanges { get; } = new();

    public Task<WebAuthReply> WebAuthenticateAsync(string name, string password, string remoteKey, CancellationToken cancellation = default)
    {
        if (Unavailable) { throw new CoreUnavailableException(); }
        Authentications.Add((name, password, remoteKey));
        return Task.FromResult(new WebAuthReply
        {
            Result = AuthenticateResult,
            Token = AuthenticateResult == AuthResult.AuthOk ? IssuedToken : string.Empty,
            PlayerName = name,
        });
    }

    public Task LogoutAsync(string token, CancellationToken cancellation = default)
    {
        LoggedOut.Add(token);
        Sessions.Remove(token);
        return Task.CompletedTask;
    }

    public Task<SessionInfoReply?> GetSessionAsync(string token, CancellationToken cancellation = default)
    {
        if (Unavailable) { throw new CoreUnavailableException(); }
        return Task.FromResult(Sessions.TryGetValue(token, out var session) ? session : null);
    }

    public Task<ChangePasswordReply> ChangePasswordAsync(string token, string currentPassword, string newPassword, string confirmPassword, CancellationToken cancellation = default)
    {
        if (Unavailable) { throw new CoreUnavailableException(); }
        PasswordChanges.Add((token, currentPassword, newPassword, confirmPassword));
        return Task.FromResult(new ChangePasswordReply { Outcome = PasswordOutcome, Message = PasswordMessage });
    }

    // Rooms. The fake keeps the rooms it is given and answers edits with the reply a test sets; it records what the web sent.
    public Dictionary<string, RoomDetail> Rooms { get; } = new();
    public bool RefuseRoomCalls { get; set; }
    public bool RefuseBecauseSessionEnded { get; set; }
    public AdminResult RoomEditResult { get; set; } = AdminResult.Ok;
    public string RoomEditMessage { get; set; } = string.Empty;
    public List<FieldError> RoomEditErrors { get; } = new();
    public List<RoomEditRequest> RoomCreates { get; } = new();
    public List<RoomEditRequest> RoomUpdates { get; } = new();
    public List<string> RoomDeletes { get; } = new();
    public List<string> RoomTokens { get; } = new();

    public FakeAdminClient AddRoom(string key, string area = "t", int? x = 0, int? y = 0, int? z = 0, params (string Dir, string To)[] exits)
    {
        var room = new RoomDetail { Key = key, ShortDescription = "short " + key, LongDescription = "long " + key, Area = area };
        if (x.HasValue) { room.X = x.Value; }
        if (y.HasValue) { room.Y = y.Value; }
        if (z.HasValue) { room.Z = z.Value; }
        room.Exits.AddRange(exits.Select(e => new RoomExit { Name = e.Dir, To = e.To }));
        Rooms[key] = room;
        return this;
    }

    private void CheckRoomCall(string token)
    {
        if (Unavailable) { throw new CoreUnavailableException(); }
        RoomTokens.Add(token);
        if (RefuseRoomCalls) { throw new CoreRefusedException(RefuseBecauseSessionEnded); }
    }

    private RoomEditReply EditReply(RoomDetail? room)
    {
        var reply = new RoomEditReply { Result = RoomEditResult, Message = RoomEditMessage };
        reply.Errors.AddRange(RoomEditErrors);
        if (room != null) { reply.Room = room; }
        return reply;
    }

    public Task<IReadOnlyList<RoomDetail>> ListRoomsAsync(string token, CancellationToken cancellation = default)
    {
        CheckRoomCall(token);
        return Task.FromResult<IReadOnlyList<RoomDetail>>(Rooms.Values.ToList());
    }

    public Task<RoomDetail?> GetRoomAsync(string token, string key, CancellationToken cancellation = default)
    {
        CheckRoomCall(token);
        return Task.FromResult(Rooms.TryGetValue(key, out var room) ? room : null);
    }

    public Task<RoomEditReply> CreateRoomAsync(string token, RoomEditRequest request, CancellationToken cancellation = default)
    {
        CheckRoomCall(token);
        RoomCreates.Add(request);
        return Task.FromResult(EditReply(null));
    }

    public Task<RoomEditReply> UpdateRoomAsync(string token, RoomEditRequest request, CancellationToken cancellation = default)
    {
        CheckRoomCall(token);
        RoomUpdates.Add(request);
        RoomDetail? current = null;
        if (RoomEditResult == AdminResult.Ok && Rooms.TryGetValue(request.Key, out var existing))
        {
            // What core would answer: the room as it is after the edit.
            current = existing.Clone();
            current.ShortDescription = request.ShortDescription;
            current.LongDescription = request.LongDescription;
            current.Area = request.Area;
            current.ClearX(); current.ClearY(); current.ClearZ();
            if (request.HasX) { current.X = request.X; }
            if (request.HasY) { current.Y = request.Y; }
            if (request.HasZ) { current.Z = request.Z; }
            current.Exits.Clear();
            current.Exits.AddRange(request.Exits);
            Rooms[request.Key] = current;
        }
        else if (Rooms.TryGetValue(request.Key, out var unchanged))
        {
            current = unchanged;
        }
        return Task.FromResult(EditReply(current));
    }

    public Task<RoomEditReply> DeleteRoomAsync(string token, string key, CancellationToken cancellation = default)
    {
        CheckRoomCall(token);
        RoomDeletes.Add(key);
        if (RoomEditResult == AdminResult.Ok) { Rooms.Remove(key); }
        return Task.FromResult(EditReply(null));
    }
}

public class LoginPageTests
{
    private static LoginModel Make(FakeAdminClient core, string name = "Ann", string password = "secret", string ip = "9.9.9.9")
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        return new LoginModel(core) { Name = name, Password = password, PageContext = new PageContext { HttpContext = http } };
    }

    [Fact]
    public async Task Success_RedirectsToTheCharacterPage_AndSetsTheTokenCookie()
    {
        var core = new FakeAdminClient();
        var page = Make(core);
        var result = await page.OnPostAsync();
        Assert.Equal("/character", Assert.IsType<RedirectResult>(result).Url);
        var cookie = page.Response.Headers.SetCookie.ToString();
        Assert.Contains(SessionAuthentication.CookieName + "=token-1", cookie);
    }

    [Fact]
    public async Task TheCookie_IsHttpOnly_AndHoldsNothingButTheToken()
    {
        var page = Make(new FakeAdminClient());
        await page.OnPostAsync();
        var cookie = page.Response.Headers.SetCookie.ToString();
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ann", cookie);
        Assert.DoesNotContain("secret", cookie);
    }

    [Fact]
    public async Task CoreIsGivenTheNamePasswordAndTheBrowserAddress()
    {
        var core = new FakeAdminClient();
        await Make(core, "  Ann ", "secret", "1.2.3.4").OnPostAsync();
        Assert.Equal(new[] { ("  Ann ", "secret", "1.2.3.4") }, core.Authentications);
    }

    [Theory]
    [InlineData(AuthResult.AuthBadCredentials)]
    [InlineData(AuthResult.AuthThrottled)]
    [InlineData(AuthResult.Unspecified)]
    public async Task BadCredentialsAndThrottle_GiveTheSameGenericMessage_AndNoCookie(AuthResult result)
    {
        var page = Make(new FakeAdminClient { AuthenticateResult = result });
        Assert.IsType<PageResult>(await page.OnPostAsync());
        Assert.Equal("Invalid name or password.", page.Error);
        Assert.False(page.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public async Task ARetiredCharacter_IsRefusedWithTheRetiredMessage_AndNoCookie()
    {
        var page = Make(new FakeAdminClient { AuthenticateResult = AuthResult.AuthRetired });
        Assert.IsType<PageResult>(await page.OnPostAsync());
        Assert.Equal(LoginModel.RetiredError, page.Error);
        Assert.False(page.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public async Task TryAgainLater_ShowsTheUnavailableMessage_AndNoCookie()
    {
        var page = Make(new FakeAdminClient { AuthenticateResult = AuthResult.AuthTryAgainLater });
        await page.OnPostAsync();
        Assert.Equal(LoginModel.UnavailableError, page.Error);
        Assert.False(page.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public async Task CoreNotReachable_ShowsTheUnavailableMessage_AndNoCookie()
    {
        var page = Make(new FakeAdminClient { Unavailable = true });
        await page.OnPostAsync();
        Assert.Equal(LoginModel.UnavailableError, page.Error);
        Assert.False(page.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public async Task AnOkReplyWithoutAToken_IsNotASignIn()
    {
        var page = Make(new FakeAdminClient { IssuedToken = string.Empty });
        await page.OnPostAsync();
        Assert.False(page.Response.Headers.ContainsKey("Set-Cookie"));
        Assert.NotNull(page.Error);
    }

    [Fact]
    public async Task Errors_NeverContainThePassword()
    {
        var page = Make(new FakeAdminClient { AuthenticateResult = AuthResult.AuthBadCredentials }, "Ann", "hunter2-secret");
        await page.OnPostAsync();
        Assert.DoesNotContain("hunter2-secret", page.Error ?? "");
    }
}

public class LogoutPageTests
{
    private static LogoutModel Make(FakeAdminClient core, string? token)
    {
        var http = new DefaultHttpContext();
        if (token != null) { http.Request.Headers.Cookie = SessionAuthentication.CookieName + "=" + token; }
        return new LogoutModel(core) { PageContext = new PageContext { HttpContext = http } };
    }

    [Fact]
    public async Task Post_TellsCoreToEndTheSession_AndClearsTheCookie()
    {
        var core = new FakeAdminClient();
        var page = Make(core, "token-1");
        var result = await page.OnPostAsync();
        Assert.Equal("/login", Assert.IsType<RedirectResult>(result).Url);
        Assert.Equal(new[] { "token-1" }, core.LoggedOut);
        Assert.Contains(SessionAuthentication.CookieName + "=;", page.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public async Task Post_WithoutACookie_StillRedirects_AndCallsNothing()
    {
        var core = new FakeAdminClient();
        var result = await Make(core, null).OnPostAsync();
        Assert.Equal("/login", Assert.IsType<RedirectResult>(result).Url);
        Assert.Empty(core.LoggedOut);
    }

    [Fact]
    public void Get_DoesNothing_ItOnlyRedirects()
    {
        var core = new FakeAdminClient();
        Assert.Equal("/", Assert.IsType<RedirectResult>(Make(core, "token-1").OnGet()).Url);
        Assert.Empty(core.LoggedOut);
    }
}

public class SessionAuthenticationHandlerTests
{
    private static async Task<(AuthenticateResult Result, HttpContext Http)> Run(FakeAdminClient core, string? token)
    {
        var http = new DefaultHttpContext();
        if (token != null) { http.Request.Headers.Cookie = SessionAuthentication.CookieName + "=" + token; }
        var handler = new SessionAuthenticationHandler(new StaticOptions(), NullLoggerFactory.Instance, UrlEncoder.Default, core);
        await handler.InitializeAsync(new AuthenticationScheme(SessionAuthentication.Scheme, null, typeof(SessionAuthenticationHandler)), http);
        return (await handler.AuthenticateAsync(), http);
    }

    private sealed class StaticOptions : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue { get; } = new();
        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }

    private static FakeAdminClient WithSession(AdminRole role)
    {
        var core = new FakeAdminClient();
        core.Sessions["token-1"] = new SessionInfoReply { PlayerName = "Ann", Role = role };
        return core;
    }

    [Theory]
    [InlineData(AdminRole.Player, "Player")]
    [InlineData(AdminRole.Creator, "Creator")]
    [InlineData(AdminRole.Admin, "Admin")]
    public async Task AKnownToken_BecomesTheSignedInUser_WithTheRoleCoreReports(AdminRole role, string expected)
    {
        var (result, _) = await Run(WithSession(role), "token-1");
        Assert.True(result.Succeeded);
        Assert.Equal("Ann", result.Principal!.FindFirstValue(ClaimTypes.Name));
        Assert.Equal(expected, result.Principal.FindFirstValue(ClaimTypes.Role));
    }

    [Fact]
    public async Task TheRole_IsReadFromCoreOnEveryRequest_SoADemotionAppliesAtOnce()
    {
        var core = WithSession(AdminRole.Admin);
        Assert.Equal("Admin", (await Run(core, "token-1")).Result.Principal!.FindFirstValue(ClaimTypes.Role));
        core.Sessions["token-1"] = new SessionInfoReply { PlayerName = "Ann", Role = AdminRole.Player };
        Assert.Equal("Player", (await Run(core, "token-1")).Result.Principal!.FindFirstValue(ClaimTypes.Role));
    }

    [Fact]
    public async Task NoCookie_IsAnonymous()
    {
        var (result, _) = await Run(WithSession(AdminRole.Admin), null);
        Assert.False(result.Succeeded);
        Assert.True(result.None);
    }

    [Fact]
    public async Task ATokenCoreNoLongerKnows_IsAnonymous_AndTheStaleCookieIsRemoved()
    {
        var core = WithSession(AdminRole.Admin);
        core.Sessions.Clear(); // revoked: retire, demote, logout elsewhere or a core restart
        var (result, http) = await Run(core, "token-1");
        Assert.False(result.Succeeded);
        Assert.Contains(SessionAuthentication.CookieName + "=;", http.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public async Task WhenCoreCannotBeAsked_TheRequestIsAnonymous_ButTheCookieIsKept()
    {
        var core = WithSession(AdminRole.Admin);
        core.Unavailable = true;
        var (result, http) = await Run(core, "token-1");
        Assert.False(result.Succeeded);
        Assert.False(http.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public async Task AGuessedTokenIsNotAUser()
    {
        var (result, _) = await Run(WithSession(AdminRole.Admin), "not-a-real-token");
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task PolicyCreatorOrAdmin_FollowsTheRoleFromCore()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(WebPolicies.Configure);
        var authorization = services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
        var creator = (await Run(WithSession(AdminRole.Creator), "token-1")).Result.Principal!;
        var player = (await Run(WithSession(AdminRole.Player), "token-1")).Result.Principal!;
        Assert.True((await authorization.AuthorizeAsync(creator, WebPolicies.CreatorOrAdmin)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(player, WebPolicies.CreatorOrAdmin)).Succeeded);
    }
}

public class SessionAccountServiceTests
{
    private static SessionAccountService For(string? name, string? role)
    {
        var http = new DefaultHttpContext();
        if (name != null)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.Role, role!) }, "test"));
        }
        return new SessionAccountService(new HttpContextAccessor { HttpContext = http });
    }

    [Fact]
    public void TheSignedInPlayer_IsFoundByNameCaseInsensitively_WithTheRoleFromTheSession()
    {
        var account = For("Ann", "Creator").GetByName("  ANN ");
        Assert.NotNull(account);
        Assert.Equal("Ann", account!.PlayerName);
        Assert.Equal(AccountRole.Creator, account.Role);
        Assert.False(account.Retired);
    }

    [Fact]
    public void ADifferentName_IsNotFound() => Assert.Null(For("Ann", "Admin").GetByName("Bob"));

    [Fact]
    public void Anonymous_IsNotFound() => Assert.Null(For(null, null).GetByName("Ann"));

    [Fact]
    public void ARolelessOrUnknownRoleClaim_IsNotFound() => Assert.Null(For("Ann", "Wizard").GetByName("Ann"));

    [Fact]
    public void RoleMapping_CoversEveryContractRole()
    {
        Assert.Equal(AccountRole.Admin, SessionAuthentication.RoleFrom(AdminRole.Admin));
        Assert.Equal(AccountRole.Creator, SessionAuthentication.RoleFrom(AdminRole.Creator));
        Assert.Equal(AccountRole.Player, SessionAuthentication.RoleFrom(AdminRole.Player));
        Assert.Equal(AccountRole.Player, SessionAuthentication.RoleFrom(AdminRole.Unspecified));
    }
}

public class PasswordPageTests
{
    private static PasswordModel Make(FakeAdminClient core, LoginThrottle throttle, string ip = "9.9.9.9", string token = "token-1")
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        http.Request.Headers.Cookie = SessionAuthentication.CookieName + "=" + token;
        return new PasswordModel(core, throttle) { PageContext = new PageContext { HttpContext = http } };
    }

    private static async Task<PasswordModel> Post(FakeAdminClient core, string current = "oldsecret", string replacement = "newsecret", string confirm = "newsecret", LoginThrottle? throttle = null)
    {
        var page = Make(core, throttle ?? new LoginThrottle());
        page.Current = current; page.New = replacement; page.Confirm = confirm;
        await page.OnPostAsync();
        return page;
    }

    [Fact]
    public void Page_RequiresSignIn() =>
        Assert.NotNull(Attribute.GetCustomAttribute(typeof(PasswordModel), typeof(AuthorizeAttribute)));

    [Fact]
    public async Task Success_ShowsConfirmation_AndSendsTheTokenAndThePasswordsToCore()
    {
        var core = new FakeAdminClient();
        var page = await Post(core);
        Assert.True(page.Succeeded);
        Assert.Null(page.Error);
        Assert.Equal(new[] { ("token-1", "oldsecret", "newsecret", "newsecret") }, core.PasswordChanges);
    }

    [Fact]
    public async Task WrongCurrent_ShowsGenericError_AndCountsTowardThePageLimit()
    {
        var core = new FakeAdminClient { PasswordOutcome = PasswordChangeOutcome.WrongCurrent };
        var throttle = new LoginThrottle();
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            var page = await Post(core, "nope", throttle: throttle);
            Assert.Equal(PasswordModel.WrongCurrentError, page.Error);
            Assert.False(page.Succeeded);
        }
        Assert.True(throttle.IsBlocked("9.9.9.9"));
        var calls = core.PasswordChanges.Count;

        // Locked out: the generic error again, and core is not even asked.
        core.PasswordOutcome = PasswordChangeOutcome.Changed;
        var blocked = await Post(core, "oldsecret", throttle: throttle);
        Assert.Equal(PasswordModel.WrongCurrentError, blocked.Error);
        Assert.False(blocked.Succeeded);
        Assert.Equal(calls, core.PasswordChanges.Count);
    }

    [Theory]
    [InlineData(PasswordChangeOutcome.Mismatch, "The new passwords did not match.")]
    [InlineData(PasswordChangeOutcome.Same, "The new password must be different from the current one.")]
    [InlineData(PasswordChangeOutcome.SaveFailed, "Your password could not be saved. Please try again later.")]
    [InlineData(PasswordChangeOutcome.Unspecified, "Your password could not be saved. Please try again later.")]
    public async Task OtherOutcomes_ShowFixedMessages_AndDoNotThrottle(PasswordChangeOutcome outcome, string message)
    {
        var throttle = new LoginThrottle();
        var page = await Post(new FakeAdminClient { PasswordOutcome = outcome }, throttle: throttle);
        Assert.Equal(message, page.Error);
        Assert.False(page.Succeeded);
        Assert.False(throttle.IsBlocked("9.9.9.9"));
    }

    [Fact]
    public async Task RuleViolation_ShowsTheRuleMessageCoreSent()
    {
        var page = await Post(new FakeAdminClient { PasswordOutcome = PasswordChangeOutcome.Invalid, PasswordMessage = "Passwords must be 6 to 64 characters long." }, replacement: "abc", confirm: "abc");
        Assert.Equal("Passwords must be 6 to 64 characters long.", page.Error);
    }

    [Fact]
    public async Task CoreNotReachable_ShowsTheUnavailableMessage()
    {
        var page = await Post(new FakeAdminClient { Unavailable = true });
        Assert.Equal(PasswordModel.UnavailableError, page.Error);
        Assert.False(page.Succeeded);
    }

    [Fact]
    public async Task Errors_NeverContainSubmittedPasswords()
    {
        var page = await Post(new FakeAdminClient { PasswordOutcome = PasswordChangeOutcome.Mismatch }, "oldsecret", "nope", "nope2");
        Assert.DoesNotContain("oldsecret", page.Error ?? "");
        Assert.DoesNotContain("nope", page.Error ?? "");
    }
}

public class LoginThrottleTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void BlocksAfterMaxFailuresThenExpires()
    {
        var clock = new Clock();
        var t = new LoginThrottle(clock);
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            Assert.False(t.IsBlocked("1.1.1.1"));
            t.RecordFailure("1.1.1.1");
        }
        Assert.True(t.IsBlocked("1.1.1.1"));
        Assert.False(t.IsBlocked("2.2.2.2"));
        clock.Now += LoginThrottle.Window + TimeSpan.FromSeconds(1);
        Assert.False(t.IsBlocked("1.1.1.1"));
    }

    [Fact]
    public void ResetClears()
    {
        var t = new LoginThrottle();
        for (var i = 0; i < LoginThrottle.MaxFailures; i++) t.RecordFailure("x");
        t.Reset("x");
        Assert.False(t.IsBlocked("x"));
    }
}

public class CoreSocketTests
{
    [Fact]
    public void TheSocketIsFrontendsSock_InsideTheRunDirectory()
    {
        var saved = Environment.GetEnvironmentVariable(CoreSocket.RunDirectoryVariable);
        try
        {
            Environment.SetEnvironmentVariable(CoreSocket.RunDirectoryVariable, "/tmp/mud-run-test");
            Assert.Equal("/tmp/mud-run-test/frontends.sock", CoreSocket.ResolvePath());
        }
        finally { Environment.SetEnvironmentVariable(CoreSocket.RunDirectoryVariable, saved); }
    }
}

public class NoMongoForAccountsTests
{
    [Fact]
    public void TheAccountTypes_NoLongerReferenceMongo()
    {
        var web = typeof(IAccountService).Assembly;
        Assert.Null(web.GetType("MUD.Web.Services.MongoAccountStore"));
        Assert.Null(web.GetType("MUD.Web.Services.IAccountStore"));
        Assert.Null(web.GetType("MUD.Web.Services.AccountService"));
    }
}

// The role rule the player editor still uses until card P6-4 moves it behind the Admin service (D45, G30). Moved with the rule from MongoAccountStore.
public class RoleMappingTests
{
    private static MongoDB.Bson.BsonDocument Doc(MongoDB.Bson.BsonValue roles) => new() { { "roles", roles } };

    private static MongoDB.Bson.BsonDocument Roles(bool creator, bool admin) => new() { { "creator", creator }, { "admin", admin }, { "currentDirectory", MongoDB.Bson.BsonNull.Value } };

    [Fact]
    public void NoRolesComponent_IsPlayer() => Assert.Equal(AccountRole.Player, MongoPlayerEditStore.RoleFrom(new MongoDB.Bson.BsonDocument()));

    [Fact]
    public void EmptyOrMalformedRoles_IsPlayer()
    {
        Assert.Equal(AccountRole.Player, MongoPlayerEditStore.RoleFrom(Doc(new MongoDB.Bson.BsonDocument())));
        Assert.Equal(AccountRole.Player, MongoPlayerEditStore.RoleFrom(Doc("admin")));
        Assert.Equal(AccountRole.Player, MongoPlayerEditStore.RoleFrom(Doc(new MongoDB.Bson.BsonDocument { { "creator", "true" }, { "admin", 1 } })));
    }

    [Fact]
    public void BothFlagsFalse_IsPlayer() => Assert.Equal(AccountRole.Player, MongoPlayerEditStore.RoleFrom(Doc(Roles(false, false))));

    [Fact]
    public void Creator() => Assert.Equal(AccountRole.Creator, MongoPlayerEditStore.RoleFrom(Doc(Roles(true, false))));

    [Fact]
    public void Admin() => Assert.Equal(AccountRole.Admin, MongoPlayerEditStore.RoleFrom(Doc(Roles(false, true))));

    [Fact]
    public void AdminWinsOverCreator() => Assert.Equal(AccountRole.Admin, MongoPlayerEditStore.RoleFrom(Doc(Roles(true, true))));

    [Fact]
    public void TheOldExtendedPropertiesShapeIsNotRead()
    {
        var old = new MongoDB.Bson.BsonDocument { { "ExtendedProperties", new MongoDB.Bson.BsonArray { new MongoDB.Bson.BsonDocument("_t", new MongoDB.Bson.BsonArray { "Entity", "ExtendedProperty", "AdminProperty" }) } } };
        Assert.Equal(AccountRole.Player, MongoPlayerEditStore.RoleFrom(old));
    }
}

// The real client over a real Unix socket against a small stand-in for core's Admin service (the real service is tested in MUD.Tests).
public class GrpcAdminClientSocketTests : IDisposable
{
    private sealed class StandInAdmin : Admin.AdminBase
    {
        public override Task<WebAuthReply> WebAuthenticate(WebAuthRequest request, Grpc.Core.ServerCallContext context) =>
            Task.FromResult(request.Password == "right"
                ? new WebAuthReply { Result = AuthResult.AuthOk, Token = "tok", PlayerName = request.Name, Role = AdminRole.Creator }
                : new WebAuthReply { Result = AuthResult.AuthBadCredentials });

        public override Task<SessionInfoReply> GetSession(TokenRequest request, Grpc.Core.ServerCallContext context) =>
            request.Token == "tok"
                ? Task.FromResult(new SessionInfoReply { PlayerName = "Ann", Role = AdminRole.Creator })
                : throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unauthenticated, "no"));

        public override Task<Ack> Logout(TokenRequest request, Grpc.Core.ServerCallContext context) => Task.FromResult(new Ack());

        public override Task<ChangePasswordReply> ChangePassword(ChangePasswordRequest request, Grpc.Core.ServerCallContext context) =>
            Task.FromResult(new ChangePasswordReply { Outcome = PasswordChangeOutcome.Changed });

        private static void RequireToken(string token)
        {
            if (token == "stale") { throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unauthenticated, "no")); }
            if (token == "low") { throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.PermissionDenied, "no")); }
            if (token == "busy") { throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "no")); }
        }

        public override Task<ListRoomsReply> ListRooms(TokenRequest request, Grpc.Core.ServerCallContext context)
        {
            RequireToken(request.Token);
            var reply = new ListRoomsReply();
            reply.Rooms.Add(new RoomDetail { Key = "t.hall", Area = "t", X = 0 });
            return Task.FromResult(reply);
        }

        public override Task<GetRoomReply> GetRoom(GetRoomRequest request, Grpc.Core.ServerCallContext context)
        {
            RequireToken(request.Token);
            return Task.FromResult(request.Key == "t.hall"
                ? new GetRoomReply { Found = true, Room = new RoomDetail { Key = "t.hall", Area = "t" } }
                : new GetRoomReply { Found = false });
        }

        public override Task<RoomEditReply> CreateRoom(RoomEditRequest request, Grpc.Core.ServerCallContext context)
        {
            RequireToken(request.Token);
            return Task.FromResult(new RoomEditReply { Result = AdminResult.Ok, Room = new RoomDetail { Key = request.Key } });
        }

        public override Task<RoomEditReply> UpdateRoom(RoomEditRequest request, Grpc.Core.ServerCallContext context)
        {
            RequireToken(request.Token);
            return Task.FromResult(new RoomEditReply { Result = AdminResult.NoChange });
        }

        public override Task<RoomEditReply> DeleteRoom(DeleteRoomRequest request, Grpc.Core.ServerCallContext context)
        {
            RequireToken(request.Token);
            return Task.FromResult(new RoomEditReply { Result = AdminResult.Refused, Message = "fixed" });
        }
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wc" + Guid.NewGuid().ToString("N").Substring(0, 8));
    private WebApplication? _app;

    private async Task<GrpcAdminClient> Start()
    {
        Directory.CreateDirectory(_directory);
        var socket = Path.Combine(_directory, "f.sock");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.ListenUnixSocket(socket, o => o.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2));
        builder.Services.AddGrpc();
        _app = builder.Build();
        _app.MapGrpcService<StandInAdmin>();
        await _app.StartAsync();
        return new GrpcAdminClient(new Admin.AdminClient(CoreSocket.OpenChannel(socket)));
    }

    public void Dispose()
    {
        _app?.StopAsync().GetAwaiter().GetResult();
        try { Directory.Delete(_directory, true); } catch (Exception) { }
    }

    [Fact]
    public async Task SignIn_SessionLookup_AndLogout_WorkOverTheSocket()
    {
        var client = await Start();
        var reply = await client.WebAuthenticateAsync("Ann", "right", "1.2.3.4");
        Assert.Equal(AuthResult.AuthOk, reply.Result);
        Assert.Equal("tok", reply.Token);
        Assert.Equal(AdminRole.Creator, (await client.GetSessionAsync("tok"))!.Role);
        Assert.Equal(AuthResult.AuthBadCredentials, (await client.WebAuthenticateAsync("Ann", "wrong", "1.2.3.4")).Result);
        await client.LogoutAsync("tok");
        Assert.Equal(PasswordChangeOutcome.Changed, (await client.ChangePasswordAsync("tok", "a", "b", "b")).Outcome);
    }

    [Fact]
    public async Task RoomCalls_WorkOverTheSocket_AndCarryTheTokenAndKey()
    {
        var client = await Start();
        var rooms = await client.ListRoomsAsync("tok");
        Assert.Equal("t.hall", Assert.Single(rooms).Key);
        Assert.Equal("t.hall", (await client.GetRoomAsync("tok", "t.hall"))!.Key);
        Assert.Null(await client.GetRoomAsync("tok", "t.nope"));
        var created = await client.CreateRoomAsync("tok", new RoomEditRequest { Key = "t.new" });
        Assert.Equal((AdminResult.Ok, "t.new"), (created.Result, created.Room.Key));
        Assert.Equal(AdminResult.NoChange, (await client.UpdateRoomAsync("tok", new RoomEditRequest { Key = "t.hall" })).Result);
        var deleted = await client.DeleteRoomAsync("tok", "t.hall");
        Assert.Equal((AdminResult.Refused, "fixed"), (deleted.Result, deleted.Message));
    }

    [Fact]
    public async Task RoomCalls_MapCoresStatusesOntoTheTwoExceptions()
    {
        var client = await Start();
        var ended = await Assert.ThrowsAsync<CoreRefusedException>(() => client.ListRoomsAsync("stale"));
        Assert.True(ended.SessionEnded);
        var tooLow = await Assert.ThrowsAsync<CoreRefusedException>(() => client.GetRoomAsync("low", "t.hall"));
        Assert.False(tooLow.SessionEnded);
        await Assert.ThrowsAsync<CoreUnavailableException>(() => client.CreateRoomAsync("busy", new RoomEditRequest { Key = "t.x" }));
        await Assert.ThrowsAsync<CoreUnavailableException>(() => client.DeleteRoomAsync("busy", "t.x"));
    }

    [Fact]
    public async Task RoomCalls_WhenNothingListens_ReportCoreUnavailable()
    {
        Directory.CreateDirectory(_directory);
        var client = new GrpcAdminClient(new Admin.AdminClient(CoreSocket.OpenChannel(Path.Combine(_directory, "none.sock"))));
        await Assert.ThrowsAsync<CoreUnavailableException>(() => client.ListRoomsAsync("tok"));
    }

    [Fact]
    public async Task AnUnauthenticatedStatus_IsNoSession_NotAnError()
    {
        var client = await Start();
        Assert.Null(await client.GetSessionAsync("stale"));
        Assert.Null(await client.GetSessionAsync(""));
    }

    [Fact]
    public async Task WhenNothingListens_TheCallsReportCoreUnavailable_AndLogoutIsQuiet()
    {
        Directory.CreateDirectory(_directory);
        var client = new GrpcAdminClient(new Admin.AdminClient(CoreSocket.OpenChannel(Path.Combine(_directory, "none.sock"))));
        await Assert.ThrowsAsync<CoreUnavailableException>(() => client.WebAuthenticateAsync("Ann", "x", "k"));
        await Assert.ThrowsAsync<CoreUnavailableException>(() => client.GetSessionAsync("tok"));
        await client.LogoutAsync("tok");
    }
}
