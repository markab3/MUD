using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MUD.Contracts;
using MUD.Web.Services;

namespace MUD.Web.Pages;

[Authorize]
public class PasswordModel : PageModel
{
    public const string WrongCurrentError = "Your password could not be changed. Check your current password and try again.";
    public const string MismatchError = "The new passwords did not match.";
    public const string SameError = "The new password must be different from the current one.";
    public const string SaveFailedError = "Your password could not be saved. Please try again later.";
    public const string UnavailableError = "The game is not reachable right now. Please try again later.";

    private readonly IAdminClient _core;
    private readonly LoginThrottle _throttle;

    public PasswordModel(IAdminClient core, LoginThrottle throttle)
    {
        _core = core;
        _throttle = throttle;
    }

    // Never rendered back into the page (the inputs carry no value attribute).
    [BindProperty] public string? Current { get; set; }
    [BindProperty] public string? New { get; set; }
    [BindProperty] public string? Confirm { get; set; }
    public string? Error { get; private set; }
    public bool Succeeded { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Same generic message whether the IP is locked out or the current password is wrong.
        // This page's own small limit stays until core throttles password changes too (see the PR description).
        if (_throttle.IsBlocked(ip))
        {
            Error = WrongCurrentError;
            return Page();
        }

        Request.Cookies.TryGetValue(SessionAuthentication.CookieName, out var token);
        ChangePasswordReply reply;
        try
        {
            reply = await _core.ChangePasswordAsync(token ?? string.Empty, Current ?? string.Empty, New ?? string.Empty, Confirm ?? string.Empty, HttpContext.RequestAborted);
        }
        catch (CoreUnavailableException)
        {
            Error = UnavailableError;
            return Page();
        }

        switch (reply.Outcome)
        {
            case PasswordChangeOutcome.WrongCurrent:
                _throttle.RecordFailure(ip);
                await Task.Delay(500);
                Error = WrongCurrentError;
                break;
            case PasswordChangeOutcome.Mismatch:
                Error = MismatchError;
                break;
            case PasswordChangeOutcome.Same:
                Error = SameError;
                break;
            case PasswordChangeOutcome.Invalid:
                Error = reply.Message;
                break;
            case PasswordChangeOutcome.Changed:
                Succeeded = true; // The session cookie is untouched: the session stays.
                break;
            default:
                Error = SaveFailedError;
                break;
        }
        return Page();
    }
}
