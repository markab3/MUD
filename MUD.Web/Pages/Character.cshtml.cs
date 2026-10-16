using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Security.Claims;

namespace MUD.Web.Pages;

[Authorize]
public class CharacterModel : PageModel
{
    public string PlayerName { get; private set; } = string.Empty;

    public void OnGet()
    {
        PlayerName = User.FindFirstValue(ClaimTypes.Name) ?? string.Empty;
    }
}
