using MUD.Contracts;
using MUD.Web.Services;

namespace MUD.Web.Pages;

public enum LoginOutcome { SignedIn, Failed }

// The sign-in logic of the Login page (moved verbatim from LoginModel.OnPostAsync), free of HttpContext so it can be tested.
// The component sets the cookie from Token when the outcome is SignedIn.
public sealed class LoginPresenter
{
    private readonly IAdminClient _core;

    public LoginPresenter(IAdminClient core) { _core = core; }

    public string? Error { get; private set; }
    public string? Token { get; private set; }

    public async Task<LoginOutcome> SignInAsync(string? name, string? password, string remoteKey, CancellationToken cancellation = default)
    {
        // Core owns the brute-force throttle (D43); the web only passes in the address as the throttle key.
        WebAuthReply reply;
        try
        {
            reply = await _core.WebAuthenticateAsync(name ?? string.Empty, password ?? string.Empty, remoteKey, cancellation);
        }
        catch (CoreUnavailableException)
        {
            Error = Messages.Unavailable;
            return LoginOutcome.Failed;
        }

        switch (reply.Result)
        {
            case AuthResult.AuthOk when !string.IsNullOrEmpty(reply.Token):
                Token = reply.Token;
                return LoginOutcome.SignedIn;
            case AuthResult.AuthRetired:
                // Only reached with the right password, so this does not reveal anything to someone without it.
                Error = Messages.LoginRetired;
                return LoginOutcome.Failed;
            case AuthResult.AuthTryAgainLater:
                Error = Messages.Unavailable;
                return LoginOutcome.Failed;
            default:
                // Bad credentials and a throttled address get the same message, so the lockout is not distinguishable by content.
                await Task.Delay(500, CancellationToken.None);
                Error = Messages.LoginGeneric;
                return LoginOutcome.Failed;
        }
    }
}
