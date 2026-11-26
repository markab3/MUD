using MUD.Contracts;
using MUD.Web.Services;

namespace MUD.Web.Pages;

public enum PlayerEditOutcome { Ok, NoChange, Invalid, Failed, Missing }
public enum PlayerEditMissing { None, NotFound, Unavailable, Forbidden }

// The player edit logic (moved from PlayerEditModel), free of HttpContext, ModelState and IActionResult (WEB_BLAZOR.md 4.1).
// Core applies the rules, audits every changed field and updates the live player; edits are last write wins (D30). The name key comes
// from the route only and has no setter. There is no name, inventory, equipment, location or credential member. All messages are fixed
// strings; nothing from the request or from an exception is echoed.
public sealed class PlayerEditPresenter
{
    private readonly IAdminClient _core;
    private readonly string _token;
    private readonly AccountRole _editor;

    public PlayerEditPresenter(IAdminClient core, string token, AccountRole editorRole, string nameKey)
    {
        _core = core;
        _token = token;
        _editor = editorRole;
        NameKey = nameKey;
    }

    public string NameKey { get; }

    // Never edited: the player as core last reported it.
    public PlayerDetail? Current { get; private set; }

    // What the signed-in editor may do to this player; decides which controls the page shows. Core re-checks every change.
    public bool CanEdit { get; private set; }
    public bool CanSetRole { get; private set; }
    public bool CanSetRetired { get; private set; }

    public string? Short { get; set; }
    public string? Long { get; set; }
    public string? Race { get; set; }
    public string? Gender { get; set; }
    public string? Class { get; set; }
    public bool Retired { get; set; }
    public AccountRole Role { get; set; }

    public string? Error { get; private set; }
    public string? Notice { get; set; }
    public PlayerEditMissing Missing { get; private set; }
    public bool IsMissing => Missing != PlayerEditMissing.None;

    // Core's field messages after an Invalid reply; the user's input stays in the form.
    public List<string> Problems { get; } = new();

    // True when saving would newly retire the character: the page asks for confirmation first.
    public bool WouldRetire => CanSetRetired && Retired && Current != null && !Current.Retired;

    public async Task<PlayerEditOutcome> LoadAsync(CancellationToken cancellation = default)
    {
        Error = null;
        Problems.Clear();
        try
        {
            var player = await _core.GetPlayerAsync(_token, NameKey, cancellation);
            if (player == null) { return Gone(PlayerEditMissing.NotFound, Messages.PlayerNotFound); }
            Load(player);
            return PlayerEditOutcome.Ok;
        }
        catch (CoreUnavailableException) { return Gone(PlayerEditMissing.Unavailable, Messages.Unavailable); }
        catch (CoreRefusedException) { return Gone(PlayerEditMissing.Forbidden, Messages.PlayerForbidden); }
    }

    public async Task<PlayerEditOutcome> SaveAsync(CancellationToken cancellation = default)
    {
        Error = null;
        Notice = null;
        Problems.Clear();
        try
        {
            var request = new UpdatePlayerRequest
            {
                NameKey = NameKey,
                ShortDescription = Short ?? string.Empty,
                LongDescription = Long ?? string.Empty,
                Race = Race ?? string.Empty,
                Gender = Gender ?? string.Empty,
                Class = Class ?? string.Empty,
                Retired = Retired,
                Role = ToAdminRole(Role),
            };
            var reply = await _core.UpdatePlayerAsync(_token, request, cancellation);
            switch (reply.Result)
            {
                case AdminResult.Ok:
                    if (reply.Player != null) { Load(reply.Player); }
                    Notice = Messages.PlayerSaved;
                    return PlayerEditOutcome.Ok;
                case AdminResult.NoChange:
                    if (reply.Player != null) { Load(reply.Player); }
                    Notice = Messages.PlayerNoChange;
                    return PlayerEditOutcome.NoChange;
                case AdminResult.Invalid:
                    // The user's input stays in the form.
                    Current = reply.Player;
                    Describe(Current);
                    foreach (var e in reply.Errors) { Problems.Add(e.Message); }
                    return PlayerEditOutcome.Invalid;
                case AdminResult.NotFound:
                    return Gone(PlayerEditMissing.NotFound, Messages.PlayerNotFound);
                case AdminResult.AuditFailed:
                    return await Reload(Messages.PlayerAuditFailed, reply.Player, cancellation);
                case AdminResult.LastAdmin:
                    return await Reload(Messages.PlayerLastAdmin, reply.Player, cancellation);
                case AdminResult.Forbidden:
                    return await Reload(Messages.PlayerForbidden, reply.Player, cancellation);
                default:
                    return await Reload(Messages.PlayerSaveFailed, reply.Player, cancellation);
            }
        }
        catch (CoreUnavailableException) { return Gone(PlayerEditMissing.Unavailable, Messages.Unavailable); }
        catch (CoreRefusedException) { return Gone(PlayerEditMissing.Forbidden, Messages.PlayerForbidden); }
    }

    // Shows the error beside the stored values of the player, as core last reported them or else as core reports them now.
    // The typed input stays in the form fields.
    private async Task<PlayerEditOutcome> Reload(string error, PlayerDetail? known, CancellationToken cancellation)
    {
        Error = error;
        Current = known ?? await _core.GetPlayerAsync(_token, NameKey, cancellation);
        if (Current == null) { return Gone(PlayerEditMissing.NotFound, Messages.PlayerNotFound); }
        Describe(Current);
        return PlayerEditOutcome.Failed;
    }

    private PlayerEditOutcome Gone(PlayerEditMissing why, string error)
    {
        Missing = why;
        Error = error;
        return PlayerEditOutcome.Missing;
    }

    private void Load(PlayerDetail player)
    {
        Missing = PlayerEditMissing.None;
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
        var target = player == null ? AccountRole.Admin : SessionAuthentication.RoleFrom(player.Role);
        CanEdit = player != null && PlayerRules.CanEdit(_editor, target);
        CanSetRole = player != null && PlayerRules.CanSetRole(_editor, target, AccountRole.Creator);
        CanSetRetired = player != null && PlayerRules.CanSetRetired(_editor, target);
    }

    private static AdminRole ToAdminRole(AccountRole role) => role switch
    {
        AccountRole.Admin => AdminRole.Admin,
        AccountRole.Creator => AdminRole.Creator,
        _ => AdminRole.Player,
    };
}
