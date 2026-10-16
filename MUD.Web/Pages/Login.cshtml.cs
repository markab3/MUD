using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MUD.Web.Services;

namespace MUD.Web.Pages;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private const string GenericError = "Invalid name or password.";

    private readonly IAccountService _accounts;
    private readonly LoginThrottle _throttle;

    public LoginModel(IAccountService accounts, LoginThrottle throttle)
    {
        _accounts = accounts;
        _throttle = throttle;
    }

    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? Password { get; set; }
    public string? Error { get; private set; }

    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? Redirect("/character") : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (_throttle.IsBlocked(ip))
        {
            // Same message as a failed login so the lockout is not distinguishable by content.
            Error = GenericError;
            return Page();
        }

        var account = _accounts.Authenticate(Name ?? string.Empty, Password ?? string.Empty);
        if (account == null)
        {
            _throttle.RecordFailure(ip);
            await Task.Delay(500);
            Error = GenericError;
            return Page();
        }

        _throttle.Reset(ip);
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, account.PlayerName),
            new Claim(ClaimTypes.Role, account.Role.ToString()),
        }, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return Redirect("/character");
    }
}
