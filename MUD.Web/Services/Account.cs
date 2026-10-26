namespace MUD.Web.Services;

public enum AccountRole { Player, Creator, Admin }

// Retired is true for a character that was retired (specs/WEB_EDITING.md section 15). Authenticate still returns such an
// account after the password is verified so the login page can say why; callers must not sign a retired account in.
public record Account(string PlayerName, AccountRole Role, string? Id = null, bool Retired = false);

// What the store hands back for a player document. Password is plaintext today (see specs/WEB.md).
public record StoredAccount(string PlayerName, string? Password, AccountRole Role, string? Id = null, bool Retired = false);

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
