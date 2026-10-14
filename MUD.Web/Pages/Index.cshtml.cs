using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MUD.Web.Pages;

public class IndexModel : PageModel
{
    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? Redirect("/character") : Redirect("/login");
}
