using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MUD.Contracts;
using MUD.Web.Services;

namespace MUD.Web.Pages;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private const string GenericError = "Invalid name or password.";
    public const string RetiredError = "This character has been retired and can no longer be played.";
    public const string UnavailableError = "The game is not reachable right now. Please try again later.";

    private readonly IAdminClient _core;

    public LoginModel(IAdminClient core) { _core = core; }

    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? Password { get; set; }
    public string? Error { get; private set; }

    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? Redirect("/character") : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        // Core owns the brute-force throttle (D43); the web only passes in the address as the throttle key.
        var remoteKey = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        WebAuthReply reply;
        try
        {
            reply = await _core.WebAuthenticateAsync(Name ?? string.Empty, Password ?? string.Empty, remoteKey, HttpContext.RequestAborted);
        }
        catch (CoreUnavailableException)
        {
            Error = UnavailableError;
            return Page();
        }

        switch (reply.Result)
        {
            case AuthResult.AuthOk when !string.IsNullOrEmpty(reply.Token):
                Response.Cookies.Append(SessionAuthentication.CookieName, reply.Token, SessionAuthentication.CookieOptionsFor(Request));
                return Redirect("/character");
            case AuthResult.AuthRetired:
                // Only reached with the right password, so this does not reveal anything to someone without it.
                Error = RetiredError;
                return Page();
            case AuthResult.AuthTryAgainLater:
                Error = UnavailableError;
                return Page();
            default:
                // Bad credentials and a throttled address get the same message, so the lockout is not distinguishable by content.
                await Task.Delay(500);
                Error = GenericError;
                return Page();
        }
    }
}
