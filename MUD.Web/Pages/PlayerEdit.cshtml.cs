using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MUD.Contracts;
using MUD.Web.Services;

namespace MUD.Web.Pages;

// /creator/players/{nameKey}: edit one player's allow-listed fields through core's Admin service (specs/WEB_EDITING.md section 15).
// Core applies the rules, audits every changed field and updates the live player; edits are last write wins (D30). The name key comes
// from the route only. The model has no name, inventory, equipment, location or credential property. All messages are fixed strings;
// nothing from the request or from an exception is echoed, and Razor encodes every value it renders.
[Authorize(Policy = WebPolicies.CreatorOrAdmin)]
public class PlayerEditModel : PageModel
{
    public const string NotFoundError = "That player does not exist.";
    public const string FailedError = "The player could not be saved. Please try again later.";
    public const string AuditFailedError = "The change could not be recorded, so nothing was saved. Please try again later.";
    public const string ForbiddenError = "You are not allowed to make that change to this player.";
    public const string LastAdminError = "The last Admin cannot lose the Admin role.";
    public const string UnavailableError = "The game is not reachable right now. Please try again later.";

    private readonly IAdminClient _core;

    public PlayerEditModel(IAdminClient core)
    {
        _core = core;
    }

    public static string PlayerUrl(string nameKey) => "/creator/players/" + Uri.EscapeDataString(nameKey);

    public string NameKey { get; private set; } = string.Empty;

    // Never bound: the player comes from the route only.
    public PlayerDetail? Current { get; private set; }

    // What the signed-in editor may do to this player; decides which controls the page shows. Core re-checks every change.
    public bool CanEdit { get; private set; }
    public bool CanSetRole { get; private set; }
    public bool CanSetRetired { get; private set; }

    [BindProperty] public string? Short { get; set; }
    [BindProperty] public string? Long { get; set; }
    [BindProperty] public string? Race { get; set; }
    [BindProperty] public string? Gender { get; set; }
    [BindProperty] public string? Class { get; set; }
    [BindProperty] public bool Retired { get; set; }
    [BindProperty] public AccountRole Role { get; set; }

    public string? Error { get; private set; }
    public string? Notice { get; private set; }
    public bool Missing { get; private set; }

    private string Token => SessionAuthentication.TokenFrom(Request);

    public async Task<IActionResult> OnGetAsync(string nameKey)
    {
        NameKey = nameKey;
        try
        {
            var player = await _core.GetPlayerAsync(Token, nameKey, HttpContext.RequestAborted);
            if (player == null) { return NotFoundPage(); }
            Load(player);
            return Page();
        }
        catch (CoreUnavailableException) { return UnavailablePage(); }
        catch (CoreRefusedException) { return ForbiddenPage(); }
    }

    public async Task<IActionResult> OnPostAsync(string nameKey)
    {
        NameKey = nameKey;
        try
        {
            var request = new UpdatePlayerRequest
            {
                NameKey = nameKey,
                ShortDescription = Short ?? string.Empty,
                LongDescription = Long ?? string.Empty,
                Race = Race ?? string.Empty,
                Gender = Gender ?? string.Empty,
                Class = Class ?? string.Empty,
                Retired = Retired,
                Role = ToAdminRole(Role),
            };
            var reply = await _core.UpdatePlayerAsync(Token, request, HttpContext.RequestAborted);
            switch (reply.Result)
            {
                case AdminResult.Ok:
                    if (reply.Player != null) { Load(reply.Player); }
                    Notice = "Saved. The running game has the change now.";
                    ModelState.Clear();
                    return Page();
                case AdminResult.NoChange:
                    if (reply.Player != null) { Load(reply.Player); }
                    Notice = "Nothing was changed.";
                    ModelState.Clear();
                    return Page();
                case AdminResult.Invalid:
                    // The user's input stays in the form.
                    Current = reply.Player;
                    Describe(Current);
                    foreach (var e in reply.Errors) { ModelState.AddModelError(string.Empty, e.Message); }
                    return Page();
                case AdminResult.NotFound:
                    return NotFoundPage();
                case AdminResult.AuditFailed:
                    return await Reload(AuditFailedError, nameKey, reply.Player);
                case AdminResult.LastAdmin:
                    return await Reload(LastAdminError, nameKey, reply.Player);
                case AdminResult.Forbidden:
                    return await Reload(ForbiddenError, nameKey, reply.Player);
                default:
                    return await Reload(FailedError, nameKey, reply.Player);
            }
        }
        catch (CoreUnavailableException) { return UnavailablePage(); }
        catch (CoreRefusedException) { return ForbiddenPage(); }
    }

    // Shows the error beside the stored values of the player, as core last reported them or else as core reports them now.
    private async Task<IActionResult> Reload(string error, string nameKey, PlayerDetail? known)
    {
        Error = error;
        Current = known ?? await _core.GetPlayerAsync(Token, nameKey, HttpContext.RequestAborted);
        if (Current == null) { return NotFoundPage(); }
        Describe(Current);
        return Page();
    }

    private IActionResult NotFoundPage()
    {
        Missing = true;
        Error = NotFoundError;
        Response.StatusCode = StatusCodes.Status404NotFound;
        return Page();
    }

    private IActionResult UnavailablePage()
    {
        Missing = true;
        Error = UnavailableError;
        Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return Page();
    }

    private IActionResult ForbiddenPage()
    {
        Missing = true;
        Error = ForbiddenError;
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Page();
    }

    private void Load(PlayerDetail player)
    {
        Current = player;
        Short = player.ShortDescription;
        Long = player.LongDescription;
        Race = player.Race;
        Gender = player.Gender;
        Class = player.Class;
        Retired = player.Retired;
        Role = SessionAuthentication.RoleFrom(player.Role);
        Describe(player);
    }

    private void Describe(PlayerDetail? player)
    {
        var editor = Enum.TryParse<AccountRole>(User.FindFirstValue(ClaimTypes.Role), out var role) ? role : AccountRole.Player;
        var target = player == null ? AccountRole.Admin : SessionAuthentication.RoleFrom(player.Role);
        CanEdit = player != null && PlayerRules.CanEdit(editor, target);
        CanSetRole = player != null && PlayerRules.CanSetRole(editor, target, AccountRole.Creator);
        CanSetRetired = player != null && PlayerRules.CanSetRetired(editor, target);
    }

    private static AdminRole ToAdminRole(AccountRole role) => role switch
    {
        AccountRole.Admin => AdminRole.Admin,
        AccountRole.Creator => AdminRole.Creator,
        _ => AdminRole.Player,
    };
}
