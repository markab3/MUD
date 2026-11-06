using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using MUD.Contracts;

namespace MUD.Web.Services;

public static class SessionAuthentication
{
    public const string Scheme = "CoreSession";
    public const string CookieName = "mud.session";

    // The cookie holds only the opaque session token core issued (D31). HttpOnly so scripts cannot read it. No expiry: it is a
    // browser-session cookie, and core ends the session itself (logout, retire, demote, restart).
    public static CookieOptions CookieOptionsFor(HttpRequest request) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = request.IsHttps,
        IsEssential = true,
        Path = "/",
    };

    // The token the browser sent, for pages that call core on the user's behalf. Empty when there is no cookie.
    public static string TokenFrom(HttpRequest request) =>
        request.Cookies.TryGetValue(CookieName, out var token) ? token ?? string.Empty : string.Empty;

    public static AccountRole RoleFrom(AdminRole role) => role switch
    {
        AdminRole.Admin => AccountRole.Admin,
        AdminRole.Creator => AccountRole.Creator,
        _ => AccountRole.Player,
    };
}

// Builds the signed-in user for each request from the token in the cookie by asking core who it belongs to. The role in the claims is
// therefore the player's CURRENT role (D31): a demotion or a retirement applies on the next request. A token core no longer knows
// (logout, retire, demote, core restart) is anonymous, and the stale cookie is removed.
public sealed class SessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IAdminClient _core;

    public SessionAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IAdminClient core)
        : base(options, logger, encoder)
    {
        _core = core;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(SessionAuthentication.CookieName, out var token) || string.IsNullOrEmpty(token))
        {
            return AuthenticateResult.NoResult();
        }

        SessionInfoReply? session;
        try
        {
            session = await _core.GetSessionAsync(token, Context.RequestAborted);
        }
        catch (CoreUnavailableException)
        {
            // Core cannot be asked: treat as not signed in for this request but keep the cookie, so the user is back once core is.
            return AuthenticateResult.NoResult();
        }

        if (session == null)
        {
            Response.Cookies.Delete(SessionAuthentication.CookieName, SessionAuthentication.CookieOptionsFor(Request));
            return AuthenticateResult.NoResult();
        }

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, session.PlayerName),
            new Claim(ClaimTypes.Role, SessionAuthentication.RoleFrom(session.Role).ToString()),
        }, SessionAuthentication.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SessionAuthentication.Scheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var returnUrl = Uri.EscapeDataString(Request.Path + Request.QueryString);
        Response.Redirect("/login?ReturnUrl=" + returnUrl);
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.Redirect("/access-denied");
        return Task.CompletedTask;
    }
}

// The account of the current request, taken from the user the handler built (so no second call to core).
public sealed class SessionAccountService : IAccountService
{
    private readonly IHttpContextAccessor _http;

    public SessionAccountService(IHttpContextAccessor http) { _http = http; }

    public Account? GetByName(string name)
    {
        var user = _http.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true) { return null; }
        var signedInName = user.FindFirstValue(ClaimTypes.Name);
        if (signedInName == null || AccountNames.Normalize(signedInName) != AccountNames.Normalize(name)) { return null; }
        return Enum.TryParse<AccountRole>(user.FindFirstValue(ClaimTypes.Role), out var role) ? new Account(signedInName, role) : null;
    }
}
