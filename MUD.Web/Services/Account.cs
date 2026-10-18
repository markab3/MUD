namespace MUD.Web.Services;

public enum AccountRole { Player, Creator, Admin }

public record Account(string PlayerName, AccountRole Role, string? Id = null);

// What the store hands back for a player document. Password is plaintext today (see specs/WEB.md).
public record StoredAccount(string PlayerName, string? Password, AccountRole Role, string? Id = null);

public interface IAccountStore
{
    // Exact lookup by NameKey (already normalised by the caller). Never a substring match.
    StoredAccount? FindByNameKey(string nameKey);

    // Sets only the Password field of the player with this NameKey (plaintext today, see specs/WEB.md).
    // Returns false if no such player exists or the write was not acknowledged.
    bool UpdatePassword(string nameKey, string newPassword);
}

public enum PasswordChangeResult { Changed, WrongCurrent, Mismatch, Same, Invalid, SaveFailed }

public interface IAccountService
{
    Account? Authenticate(string name, string password);
    Account? GetByName(string name);

    // Verifies the current password first (wrong current is reported before any rule is revealed).
    PasswordChangeResult ChangePassword(string name, string currentPassword, string newPassword, string confirmPassword);
}
