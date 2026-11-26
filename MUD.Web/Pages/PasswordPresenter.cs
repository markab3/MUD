using MUD.Contracts;
using MUD.Web.Services;

namespace MUD.Web.Pages;

public enum PasswordOutcome { Changed, Failed }

// The change-password logic (moved verbatim from PasswordModel.OnPostAsync), free of HttpContext. The token and the caller's
// address come from the circuit's user. Nothing submitted is ever kept: the component clears its fields after each submit.
public sealed class PasswordPresenter
{
    private readonly IAdminClient _core;
    private readonly LoginThrottle _throttle;

    public PasswordPresenter(IAdminClient core, LoginThrottle throttle)
    {
        _core = core;
        _throttle = throttle;
    }

    public string? Error { get; private set; }
    public bool Succeeded { get; private set; }

    public async Task<PasswordOutcome> ChangeAsync(string token, string ip, string? current, string? replacement, string? confirm, CancellationToken cancellation = default)
    {
        Error = null;
        Succeeded = false;

        // Same generic message whether the IP is locked out or the current password is wrong.
        // This page's own small limit stays until core throttles password changes too.
        if (_throttle.IsBlocked(ip))
        {
            Error = Messages.PasswordWrongCurrent;
            return PasswordOutcome.Failed;
        }

        ChangePasswordReply reply;
        try
        {
            reply = await _core.ChangePasswordAsync(token, current ?? string.Empty, replacement ?? string.Empty, confirm ?? string.Empty, cancellation);
        }
        catch (CoreUnavailableException)
        {
            Error = Messages.Unavailable;
            return PasswordOutcome.Failed;
        }

        switch (reply.Outcome)
        {
            case PasswordChangeOutcome.WrongCurrent:
                _throttle.RecordFailure(ip);
                await Task.Delay(500, CancellationToken.None);
                Error = Messages.PasswordWrongCurrent;
                return PasswordOutcome.Failed;
            case PasswordChangeOutcome.Mismatch:
                Error = Messages.PasswordMismatch;
                return PasswordOutcome.Failed;
            case PasswordChangeOutcome.Same:
                Error = Messages.PasswordSame;
                return PasswordOutcome.Failed;
            case PasswordChangeOutcome.Invalid:
                Error = reply.Message;
                return PasswordOutcome.Failed;
            case PasswordChangeOutcome.Changed:
                Succeeded = true; // The session cookie is untouched: the session stays.
                return PasswordOutcome.Changed;
            default:
                Error = Messages.PasswordSaveFailed;
                return PasswordOutcome.Failed;
        }
    }
}
