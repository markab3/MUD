using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MUD.Web.Services;

namespace MUD.Web.Pages;

[Authorize]
public class PasswordModel : PageModel
{
    public const string WrongCurrentError = "Your password could not be changed. Check your current password and try again.";
    public const string MismatchError = "The new passwords did not match.";
    public const string SameError = "The new password must be different from the current one.";
    public const string SaveFailedError = "Your password could not be saved. Please try again later.";

    private readonly IAccountService _accounts;
    private readonly LoginThrottle _throttle;

    public PasswordModel(IAccountService accounts, LoginThrottle throttle)
    {
        _accounts = accounts;
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
        var name = User.FindFirstValue(ClaimTypes.Name) ?? string.Empty;
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Same generic message whether the IP is locked out or the current password is wrong.
        if (_throttle.IsBlocked(ip))
        {
            Error = WrongCurrentError;
            return Page();
        }

        var result = _accounts.ChangePassword(name, Current ?? string.Empty, New ?? string.Empty, Confirm ?? string.Empty);
        switch (result)
        {
            case PasswordChangeResult.WrongCurrent:
                _throttle.RecordFailure(ip);
                await Task.Delay(500);
                Error = WrongCurrentError;
                break;
            case PasswordChangeResult.Mismatch:
                Error = MismatchError;
                break;
            case PasswordChangeResult.Same:
                Error = SameError;
                break;
            case PasswordChangeResult.Invalid:
                Error = PasswordRules.Validate(New);
                break;
            case PasswordChangeResult.SaveFailed:
                Error = SaveFailedError;
                break;
            default:
                Succeeded = true; // The sign-in cookie is untouched: the session stays.
                break;
        }
        return Page();
    }
}
