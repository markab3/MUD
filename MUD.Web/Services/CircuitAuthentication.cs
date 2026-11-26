using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using MUD.Contracts;

namespace MUD.Web.Services;

public enum Revalidation { Keep, SignedOut, Replace }

public sealed record RevalidationResult(Revalidation Action, ClaimsPrincipal? Principal = null);

// The decision part of the circuit's periodic session check (WEB_BLAZOR.md 3.1, D31), kept free of the framework base class so it can be tested.
public static class SessionRevalidator
{
    // Asks core who the circuit's token belongs to now.
    //  - unknown token (logout, retire, demote, core restart): SignedOut (the user becomes anonymous);
    //  - core cannot answer: Keep (the user is not signed out because core is restarting);
    //  - otherwise Replace when the name or role differs from the current principal, else Keep. The new principal carries the CURRENT role.
    public static async Task<RevalidationResult> RevalidateAsync(IAdminClient core, ClaimsPrincipal current, CancellationToken cancellation = default)
    {
        var token = current.FindFirstValue(SessionAuthentication.TokenClaim);
        if (string.IsNullOrEmpty(token)) { return new RevalidationResult(Revalidation.SignedOut); }

        SessionInfoReply? session;
        try { session = await core.GetSessionAsync(token, cancellation); }
        catch (CoreUnavailableException) { return new RevalidationResult(Revalidation.Keep); }

        if (session == null) { return new RevalidationResult(Revalidation.SignedOut); }

        var role = SessionAuthentication.RoleFrom(session.Role).ToString();
        if (current.FindFirstValue(ClaimTypes.Role) == role && current.FindFirstValue(ClaimTypes.Name) == session.PlayerName)
        {
            return new RevalidationResult(Revalidation.Keep);
        }
        return new RevalidationResult(Revalidation.Replace,
            SessionAuthentication.BuildPrincipal(session, token, current.FindFirstValue(SessionAuthentication.RemoteClaim)));
    }
}

// Replaces the default authentication state provider of the circuit. The initial user is the one the request handler built from
// the cookie; this re-checks the session token with core on an interval, so a lowered role or an ended session is honoured.
public sealed class SessionAuthenticationStateProvider : RevalidatingServerAuthenticationStateProvider
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly IAdminClient _core;

    public SessionAuthenticationStateProvider(ILoggerFactory loggerFactory, IAdminClient core) : base(loggerFactory)
    {
        _core = core;
    }

    protected override TimeSpan RevalidationInterval => Interval;

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        var result = await SessionRevalidator.RevalidateAsync(_core, authenticationState.User, cancellationToken);
        switch (result.Action)
        {
            case Revalidation.SignedOut:
                return false;
            case Revalidation.Replace:
                SetAuthenticationState(Task.FromResult(new AuthenticationState(result.Principal!)));
                return true;
            default:
                return true;
        }
    }
}
