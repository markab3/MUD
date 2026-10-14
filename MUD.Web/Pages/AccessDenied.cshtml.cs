using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MUD.Web.Pages;

[AllowAnonymous]
public class AccessDeniedModel : PageModel
{
    public IActionResult OnGet()
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Page();
    }
}
