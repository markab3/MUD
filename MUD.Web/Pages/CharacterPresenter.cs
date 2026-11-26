using MUD.Contracts;
using MUD.Web.Services;

namespace MUD.Web.Pages;

// The player page: the signed-in player's OWN record. Core picks the player from the session token (GetOwnPlayer takes no name), so
// no other player can be asked for. The reply carries no password, token or inventory; nothing from it or from an exception is
// echoed beyond the fixed sentences below.
public sealed class CharacterPresenter
{
    private readonly IAdminClient _core;
    private readonly string _token;

    public CharacterPresenter(IAdminClient core, string token)
    {
        _core = core;
        _token = token;
    }

    public OwnPlayerReply? Own { get; private set; }

    // Set when the record could not be loaded: the core-down sentence, or a sign-in-again sentence when core refused the session.
    public string? Error { get; private set; }

    public async Task LoadAsync(CancellationToken cancellation = default)
    {
        Own = null;
        Error = null;
        try { Own = await _core.GetOwnPlayerAsync(_token, cancellation); }
        catch (CoreUnavailableException) { Error = Messages.Unavailable; }
        catch (CoreRefusedException) { Error = Messages.CharacterRefused; }
    }

    public static string RoleText(AdminRole role) => PlayerListPresenter.RoleText(role);

    // Empty text is shown as a dash so a definition list never has a blank value.
    public static string OrDash(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;
}
