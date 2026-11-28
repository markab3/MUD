using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MUD.Contracts;
using MUD.Web.Pages;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

// The Blazor components' logic classes (WEB_BLAZOR.md 4.1). They carry the same behaviour as the Razor page models, so these are the
// page-model tests (LoginPageTests, PasswordPageTests, LogoutPageTests) ported to the presenters and the logout endpoint.
public class LoginPresenterTests
{
    private static async Task<(LoginPresenter P, LoginOutcome O)> Run(FakeAdminClient core, string name = "Ann", string password = "secret", string ip = "9.9.9.9")
    {
        var p = new LoginPresenter(core);
        return (p, await p.SignInAsync(name, password, ip));
    }

    [Fact]
    public async Task Success_ReturnsTheToken()
    {
        var (p, o) = await Run(new FakeAdminClient());
        Assert.Equal(LoginOutcome.SignedIn, o);
        Assert.Equal("token-1", p.Token);
        Assert.Null(p.Error);
    }

    [Fact]
    public async Task CoreIsGivenTheNamePasswordAndTheBrowserAddress()
    {
        var core = new FakeAdminClient();
        await Run(core, "  Ann ", "secret", "1.2.3.4");
        Assert.Equal(new[] { ("  Ann ", "secret", "1.2.3.4") }, core.Authentications);
    }

    [Theory]
    [InlineData(AuthResult.AuthBadCredentials)]
    [InlineData(AuthResult.AuthThrottled)]
    [InlineData(AuthResult.Unspecified)]
    public async Task BadCredentialsAndThrottle_GiveTheSameGenericMessage_AndNoToken(AuthResult result)
    {
        var (p, o) = await Run(new FakeAdminClient { AuthenticateResult = result });
        Assert.Equal(LoginOutcome.Failed, o);
        Assert.Equal("Invalid name or password.", p.Error);
        Assert.Null(p.Token);
    }

    [Fact]
    public async Task BadCredentials_AreDelayed()
    {
        var start = DateTime.UtcNow;
        await Run(new FakeAdminClient { AuthenticateResult = AuthResult.AuthBadCredentials });
        Assert.True(DateTime.UtcNow - start >= TimeSpan.FromMilliseconds(450));
    }

    [Fact]
    public async Task ARetiredCharacter_IsRefusedWithTheRetiredMessage()
    {
        var (p, o) = await Run(new FakeAdminClient { AuthenticateResult = AuthResult.AuthRetired });
        Assert.Equal(LoginOutcome.Failed, o);
        Assert.Equal(Messages.LoginRetired, p.Error);
        Assert.Null(p.Token);
    }

    [Fact]
    public async Task TryAgainLater_AndCoreNotReachable_ShowTheUnavailableMessage()
    {
        Assert.Equal(Messages.Unavailable, (await Run(new FakeAdminClient { AuthenticateResult = AuthResult.AuthTryAgainLater })).P.Error);
        var (p, o) = await Run(new FakeAdminClient { Unavailable = true });
        Assert.Equal(LoginOutcome.Failed, o);
        Assert.Equal(Messages.Unavailable, p.Error);
        Assert.Null(p.Token);
    }

    [Fact]
    public async Task AnOkReplyWithoutAToken_IsNotASignIn()
    {
        var (p, o) = await Run(new FakeAdminClient { IssuedToken = string.Empty });
        Assert.Equal(LoginOutcome.Failed, o);
        Assert.NotNull(p.Error);
    }

    [Fact]
    public async Task Errors_NeverContainThePassword()
    {
        var (p, _) = await Run(new FakeAdminClient { AuthenticateResult = AuthResult.AuthBadCredentials }, "Ann", "hunter2-secret");
        Assert.DoesNotContain("hunter2-secret", p.Error ?? "");
    }
}

public class PasswordPresenterTests
{
    private static async Task<PasswordPresenter> Post(FakeAdminClient core, string current = "oldsecret", string replacement = "newsecret", string confirm = "newsecret", LoginThrottle? throttle = null)
    {
        var p = new PasswordPresenter(core, throttle ?? new LoginThrottle());
        await p.ChangeAsync("token-1", "9.9.9.9", current, replacement, confirm);
        return p;
    }

    [Fact]
    public void Component_RequiresSignIn() =>
        Assert.NotNull(typeof(MUD.Web.Components.Pages.Password).GetCustomAttribute<AuthorizeAttribute>());

    [Fact]
    public async Task Success_ShowsConfirmation_AndSendsTheTokenAndThePasswordsToCore()
    {
        var core = new FakeAdminClient();
        var p = await Post(core);
        Assert.True(p.Succeeded);
        Assert.Null(p.Error);
        Assert.Equal(new[] { ("token-1", "oldsecret", "newsecret", "newsecret") }, core.PasswordChanges);
    }

    [Fact]
    public async Task WrongCurrent_ShowsGenericError_CountsTowardTheLimit_AndALockedOutAddressIsNotForwardedToCore()
    {
        var core = new FakeAdminClient { PasswordOutcome = PasswordChangeOutcome.WrongCurrent };
        var throttle = new LoginThrottle();
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            var p = await Post(core, "nope", throttle: throttle);
            Assert.Equal(Messages.PasswordWrongCurrent, p.Error);
            Assert.False(p.Succeeded);
        }
        Assert.True(throttle.IsBlocked("9.9.9.9"));
        var calls = core.PasswordChanges.Count;

        core.PasswordOutcome = PasswordChangeOutcome.Changed;
        var blocked = await Post(core, "oldsecret", throttle: throttle);
        Assert.Equal(Messages.PasswordWrongCurrent, blocked.Error);
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
        var p = await Post(new FakeAdminClient { PasswordOutcome = outcome }, throttle: throttle);
        Assert.Equal(message, p.Error);
        Assert.False(p.Succeeded);
        Assert.False(throttle.IsBlocked("9.9.9.9"));
    }

    [Fact]
    public async Task RuleViolation_ShowsTheRuleMessageCoreSent()
    {
        var p = await Post(new FakeAdminClient { PasswordOutcome = PasswordChangeOutcome.Invalid, PasswordMessage = "Passwords must be 6 to 64 characters long." }, replacement: "abc", confirm: "abc");
        Assert.Equal("Passwords must be 6 to 64 characters long.", p.Error);
    }

    [Fact]
    public async Task CoreNotReachable_ShowsTheUnavailableMessage()
    {
        var p = await Post(new FakeAdminClient { Unavailable = true });
        Assert.Equal(Messages.Unavailable, p.Error);
        Assert.False(p.Succeeded);
    }

    [Fact]
    public async Task ASecondSubmit_ClearsTheEarlierResult()
    {
        var core = new FakeAdminClient();
        var p = new PasswordPresenter(core, new LoginThrottle());
        await p.ChangeAsync("token-1", "9.9.9.9", "a", "b", "b");
        Assert.True(p.Succeeded);
        core.PasswordOutcome = PasswordChangeOutcome.Mismatch;
        await p.ChangeAsync("token-1", "9.9.9.9", "a", "b", "c");
        Assert.False(p.Succeeded);
        Assert.Equal(Messages.PasswordMismatch, p.Error);
    }

    [Fact]
    public async Task Errors_NeverContainSubmittedPasswords()
    {
        var p = await Post(new FakeAdminClient { PasswordOutcome = PasswordChangeOutcome.Mismatch }, "oldsecret", "nope", "nope2");
        Assert.DoesNotContain("oldsecret", p.Error ?? "");
        Assert.DoesNotContain("nope", p.Error ?? "");
    }
}

public class LogoutEndpointTests
{
    private sealed class FakeAntiforgery : IAntiforgery
    {
        public bool Valid { get; set; } = true;
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => new("r", "c", "f", "h");
        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) => new("r", "c", "f", "h");
        public Task<bool> IsRequestValidAsync(HttpContext httpContext) => Task.FromResult(Valid);
        public void SetCookieTokenAndHeader(HttpContext httpContext) { }
        public Task ValidateRequestAsync(HttpContext httpContext) =>
            Valid ? Task.CompletedTask : throw new AntiforgeryValidationException("bad token");
    }

    private static DefaultHttpContext Http(string? token)
    {
        var http = new DefaultHttpContext();
        if (token != null) { http.Request.Headers.Cookie = SessionAuthentication.CookieName + "=" + token; }
        return http;
    }

    private static string RedirectOf(IResult result)
    {
        var redirect = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.RedirectHttpResult>(result);
        return redirect.Url;
    }

    [Fact]
    public async Task Post_TellsCoreToEndTheSession_ClearsTheCookie_AndGoesToLogin()
    {
        var core = new FakeAdminClient();
        var http = Http("token-1");
        var result = await LogoutEndpoint.HandlePostAsync(http, new FakeAntiforgery(), core);
        Assert.Equal(WebRoutes.Login, RedirectOf(result));
        Assert.Equal(new[] { "token-1" }, core.LoggedOut);
        Assert.Contains(SessionAuthentication.CookieName + "=;", http.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public async Task Post_WithoutACookie_StillRedirects_AndCallsNothing()
    {
        var core = new FakeAdminClient();
        var result = await LogoutEndpoint.HandlePostAsync(Http(null), new FakeAntiforgery(), core);
        Assert.Equal(WebRoutes.Login, RedirectOf(result));
        Assert.Empty(core.LoggedOut);
    }

    [Fact]
    public async Task Post_WithoutAValidAntiforgeryToken_IsRefused_AndSignsNobodyOut()
    {
        var core = new FakeAdminClient();
        var http = Http("token-1");
        var result = await LogoutEndpoint.HandlePostAsync(http, new FakeAntiforgery { Valid = false }, core);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.BadRequest>(result);
        Assert.Empty(core.LoggedOut);
        Assert.False(http.Response.Headers.ContainsKey("Set-Cookie"));
    }
}

public class SessionRevalidatorTests
{
    private static ClaimsPrincipal Principal(AdminRole role, string token = "token-1") =>
        SessionAuthentication.BuildPrincipal(new SessionInfoReply { PlayerName = "Ann", Role = role }, token, "9.9.9.9");

    private static FakeAdminClient Core(AdminRole role)
    {
        var core = new FakeAdminClient();
        core.Sessions["token-1"] = new SessionInfoReply { PlayerName = "Ann", Role = role };
        return core;
    }

    [Fact]
    public async Task AnUnchangedSession_IsKept()
    {
        var r = await SessionRevalidator.RevalidateAsync(Core(AdminRole.Admin), Principal(AdminRole.Admin));
        Assert.Equal(Revalidation.Keep, r.Action);
    }

    [Fact]
    public async Task AnEndedSession_SignsTheCircuitOut()
    {
        var core = Core(AdminRole.Admin);
        core.Sessions.Clear(); // logout elsewhere, retire, demote or a core restart
        Assert.Equal(Revalidation.SignedOut, (await SessionRevalidator.RevalidateAsync(core, Principal(AdminRole.Admin))).Action);
    }

    [Fact]
    public async Task APrincipalWithoutAToken_IsSignedOut_AndCoreIsNotAsked()
    {
        var core = Core(AdminRole.Admin);
        var noToken = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "Ann") }, "x"));
        Assert.Equal(Revalidation.SignedOut, (await SessionRevalidator.RevalidateAsync(core, noToken)).Action);
    }

    [Fact]
    public async Task ALoweredRole_ReplacesThePrincipalWithTheCurrentRole_ButKeepsTheTokenAndAddress()
    {
        var r = await SessionRevalidator.RevalidateAsync(Core(AdminRole.Player), Principal(AdminRole.Admin));
        Assert.Equal(Revalidation.Replace, r.Action);
        Assert.Equal("Player", r.Principal!.FindFirstValue(ClaimTypes.Role));
        Assert.Equal("token-1", r.Principal.FindFirstValue(SessionAuthentication.TokenClaim));
        Assert.Equal("9.9.9.9", r.Principal.FindFirstValue(SessionAuthentication.RemoteClaim));
    }

    [Fact]
    public async Task WhenCoreCannotBeAsked_TheUserIsKept()
    {
        var core = Core(AdminRole.Admin);
        core.Unavailable = true;
        Assert.Equal(Revalidation.Keep, (await SessionRevalidator.RevalidateAsync(core, Principal(AdminRole.Admin))).Action);
    }

    [Fact]
    public async Task ThePolicy_FollowsTheReplacedPrincipal()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(WebPolicies.Configure);
        var authz = services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
        var lowered = (await SessionRevalidator.RevalidateAsync(Core(AdminRole.Player), Principal(AdminRole.Creator))).Principal!;
        Assert.False((await authz.AuthorizeAsync(lowered, WebPolicies.CreatorOrAdmin)).Succeeded);
    }
}

public class BlazorShellTests
{
    private static IEnumerable<(Type Type, string Route)> Components() =>
        typeof(MUD.Web.Services.IAdminClient).Assembly.GetTypes()
            .Where(t => typeof(IComponent).IsAssignableFrom(t))
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>().Select(r => (t, r.Template)));

    [Fact]
    public void EveryRoutableComponent_DeclaresItsAuthorisation_SoNoPageIsAddedUnprotected()
    {
        var routes = Components().ToList();
        Assert.NotEmpty(routes);
        foreach (var (type, route) in routes)
        {
            Assert.True(type.GetCustomAttributes<AuthorizeAttribute>().Any() || type.GetCustomAttributes<AllowAnonymousAttribute>().Any(),
                type.Name + " (" + route + ") has neither [Authorize] nor [AllowAnonymous]");
        }
    }

    [Fact]
    public void RazorPagesAreGone_BlazorIsTheOnlyUi_AndOwnsTheRealUrls()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "MUD.sln"))) { root = root.Parent; }
        Assert.NotNull(root);
        var web = Path.Combine(root!.FullName, "MUD.Web");
        Assert.Empty(Directory.EnumerateFiles(web, "*.cshtml", SearchOption.AllDirectories));
        Assert.DoesNotContain("MapRazorPages", File.ReadAllText(Path.Combine(web, "Program.cs")));
        Assert.DoesNotContain("AddRazorPages", File.ReadAllText(Path.Combine(web, "Program.cs")));
        Assert.Equal(string.Empty, WebRoutes.Prefix);
        Assert.Equal("/login", WebRoutes.Login);
        Assert.Equal("/logout", WebRoutes.Logout);
        Assert.Equal("/character", WebRoutes.Character);
        Assert.Equal("/account/password", WebRoutes.Password);
        Assert.Equal("/creator/map", WebRoutes.Map);
        Assert.Equal("/creator/players", WebRoutes.Players);
        Assert.Equal("/creator/modules", WebRoutes.Modules);
        Assert.Equal("/creator/terrain", WebRoutes.Terrain);
        Assert.Equal("/creator/areas", WebRoutes.Areas);
    }

    [Fact]
    public void TheRoutes_MatchTheRouteConstants_AndTheSignedInPagesRequireSignIn()
    {
        var byRoute = Components().ToDictionary(c => c.Route, c => c.Type);
        Assert.Equal(new[] { WebRoutes.AccessDenied, WebRoutes.Character, WebRoutes.Home, WebRoutes.Login, WebRoutes.Password,
                WebRoutes.Creator, WebRoutes.Map, WebRoutes.Prefix + "/creator/rooms/{Key}",
                WebRoutes.Players, WebRoutes.Prefix + "/creator/players/{NameKey}", WebRoutes.Modules,
                WebRoutes.Terrain, WebRoutes.TerrainEditPage, WebRoutes.Areas, WebRoutes.ItemTemplates, WebRoutes.ItemTemplateEditPage, WebRoutes.NpcTemplates }.OrderBy(x => x),
            byRoute.Keys.OrderBy(x => x));
        Assert.NotNull(byRoute[WebRoutes.Character].GetCustomAttribute<AuthorizeAttribute>());
        Assert.NotNull(byRoute[WebRoutes.Password].GetCustomAttribute<AuthorizeAttribute>());
        Assert.NotNull(byRoute[WebRoutes.Login].GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.NotNull(byRoute[WebRoutes.AccessDenied].GetCustomAttribute<AllowAnonymousAttribute>());
    }

    [Fact]
    public void TheMessages_ArePinned()
    {
        Assert.Equal("Invalid name or password.", Messages.LoginGeneric);
        Assert.Equal("This character has been retired and can no longer be played.", Messages.LoginRetired);
        Assert.Equal("The game is not reachable right now. Please try again later.", Messages.Unavailable);
        Assert.Equal("The game is not reachable right now. Please try again later.", Messages.Unavailable);
        Assert.Equal("Your password could not be changed. Check your current password and try again.", Messages.PasswordWrongCurrent);
        Assert.Equal("The new passwords did not match.", Messages.PasswordMismatch);
        Assert.Equal("The new password must be different from the current one.", Messages.PasswordSame);
        Assert.Equal("Your password could not be saved. Please try again later.", Messages.PasswordSaveFailed);
    }

}
