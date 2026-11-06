using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MUD.Web.Services;

namespace MUD.Web.Pages;

public class LogoutModel : PageModel
{
    private readonly IAdminClient _core;

    public LogoutModel(IAdminClient core) { _core = core; }

    // GET does nothing; only the antiforgery-protected POST signs out.
    public IActionResult OnGet() => Redirect("/");

    public async Task<IActionResult> OnPostAsync()
    {
        // Core ends the session; the cookie goes either way, so the user is signed out of the browser even if core cannot be told.
        if (Request.Cookies.TryGetValue(SessionAuthentication.CookieName, out var token))
        {
            await _core.LogoutAsync(token, HttpContext.RequestAborted);
        }
        Response.Cookies.Delete(SessionAuthentication.CookieName, SessionAuthentication.CookieOptionsFor(Request));
        return Redirect("/login");
    }
}
