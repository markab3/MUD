using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MUD.Web.Services;

namespace MUD.Web.Pages;

// /creator/players/{nameKey}: edit one player's allow-listed fields (specs/WEB_EDITING.md section 15). The name key comes from
// the route only. The model has no name, inventory, equipment, location or credential property. All messages are
// fixed strings; nothing from the request or from an exception is echoed, and Razor encodes every value it renders.
[Authorize(Policy = WebPolicies.CreatorOrAdmin)]
public class PlayerEditModel : PageModel
{
    public const string ConflictError = "This player was changed by someone else while you were editing. Your changes were not saved. The current values are shown below; review them and save again if you still want your changes.";
    public const string NotFoundError = "That player does not exist.";
    public const string FailedError = "The player could not be saved. Please try again later.";
    public const string AuditFailedError = "The change could not be recorded, so nothing was saved. Please try again later.";
    public const string ForbiddenError = "You are not allowed to make that change to this player.";
    public const string LastAdminError = "The last Admin cannot lose the Admin role.";

    private readonly IPlayerWriter _writer;
    private readonly IPlayerEditStore _store;
    private readonly IAccountService _accounts;

    public PlayerEditModel(IPlayerWriter writer, IPlayerEditStore store, IAccountService accounts)
    {
        _writer = writer;
        _store = store;
        _accounts = accounts;
    }

    public static string PlayerUrl(string nameKey) => "/creator/players/" + Uri.EscapeDataString(nameKey);

    public string NameKey { get; private set; } = string.Empty;

    // Never bound: the player comes from the route only.
    public PlayerDetail? Current { get; private set; }

    // What the signed-in editor may do to this player; decides which controls the page shows.
    public bool CanEdit { get; private set; }
    public bool CanSetRole { get; private set; }
    public bool CanSetRetired { get; private set; }

    [BindProperty] public int Rev { get; set; }
    [BindProperty] public string? Short { get; set; }
    [BindProperty] public string? Long { get; set; }
    [BindProperty] public string? Race { get; set; }
    [BindProperty] public string? Gender { get; set; }
    [BindProperty] public string? Class { get; set; }
    [BindProperty] public bool Retired { get; set; }
    [BindProperty] public AccountRole Role { get; set; }

    public string? Error { get; private set; }
    public string? Notice { get; private set; }
    public bool Conflict { get; private set; }
    public bool Missing { get; private set; }

    public IActionResult OnGet(string nameKey)
    {
        NameKey = nameKey;
        var player = _store.Find(nameKey);
        if (player == null) { return NotFoundPage(); }
        Load(player);
        return Page();
    }

    public IActionResult OnPost(string nameKey)
    {
        NameKey = nameKey;
        var actor = Actor();
        if (actor == null) { Error = ForbiddenError; return Reload(nameKey); }

        var edit = new PlayerEdit(nameKey, Short, Long, Race, Gender, Class,
            Retired, Role, Rev);
        var result = _writer.Save(actor, edit);
        switch (result.Status)
        {
            case PlayerEditStatus.Saved:
                Load(result.Current!);
                Notice = "Saved. The running game picks the change up within a few seconds.";
                ModelState.Clear();
                return Page();
            case PlayerEditStatus.NoChange:
                Load(result.Current!);
                Notice = "Nothing was changed.";
                ModelState.Clear();
                return Page();
            case PlayerEditStatus.Invalid:
                Current = result.Current;
                Describe(actor, result.Current);
                foreach (var e in result.FieldErrors) { ModelState.AddModelError(string.Empty, e.Message); }
                return Page();
            case PlayerEditStatus.Conflict:
                Conflict = true;
                Error = ConflictError;
                Current = result.Current;
                Describe(actor, result.Current);
                Rev = result.Current?.Rev ?? Rev; // the user's input stays in the form; saving again is deliberate
                ModelState.Remove(nameof(Rev));
                return Page();
            case PlayerEditStatus.NotFound:
                return NotFoundPage();
            case PlayerEditStatus.AuditFailed:
                Error = AuditFailedError;
                return Reload(nameKey, result.Current);
            case PlayerEditStatus.LastAdmin:
                Error = LastAdminError;
                return Reload(nameKey, result.Current);
            case PlayerEditStatus.Forbidden:
                Error = ForbiddenError;
                return Reload(nameKey, result.Current);
            default:
                Error = FailedError;
                return Reload(nameKey, result.Current);
        }
    }

    private IActionResult Reload(string nameKey, PlayerDetail? known = null)
    {
        Current = known ?? _store.Find(nameKey);
        if (Current == null) { return NotFoundPage(); }
        Describe(Actor(), Current);
        return Page();
    }

    private RoomActor? Actor() => RoomActors.Resolve(_accounts, User.FindFirstValue(ClaimTypes.Name));

    private IActionResult NotFoundPage()
    {
        Missing = true;
        Error = NotFoundError;
        Response.StatusCode = StatusCodes.Status404NotFound;
        return Page();
    }

    private void Load(PlayerDetail player)
    {
        Current = player;
        Rev = player.Rev;
        Short = player.Short;
        Long = player.Long;
        Race = player.Race;
        Gender = player.Gender;
        Class = player.Class;
        Retired = player.Retired;
        Role = player.Role;
        Describe(Actor(), player);
    }

    private void Describe(RoomActor? actor, PlayerDetail? player)
    {
        var role = actor?.Role ?? AccountRole.Player;
        var target = player?.Role ?? AccountRole.Admin;
        CanEdit = actor != null && PlayerRules.CanEdit(role, target);
        CanSetRole = actor != null && PlayerRules.CanSetRole(role, target, AccountRole.Creator);
        CanSetRetired = actor != null && PlayerRules.CanSetRetired(role, target);
    }
}
