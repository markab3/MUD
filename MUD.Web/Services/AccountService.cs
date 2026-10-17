using System.Security.Cryptography;
using System.Text;

namespace MUD.Web.Services;

// In-process account/role logic. Same matching rules as MUD.Core PlayerAccounts.FindByName/Authenticate.
public class AccountService : IAccountService
{
    private readonly IAccountStore _store;

    public AccountService(IAccountStore store) { _store = store; }

    public static string Normalize(string? name) => (name ?? string.Empty).Trim().ToLowerInvariant();

    public Account? GetByName(string name)
    {
        var stored = Find(name);
        return stored == null ? null : new Account(stored.PlayerName, stored.Role);
    }

    public Account? Authenticate(string name, string password)
    {
        var stored = Find(name);
        if (stored == null || stored.Password == null || !PasswordsEqual(stored.Password, password ?? string.Empty))
        {
            return null;
        }
        return new Account(stored.PlayerName, stored.Role);
    }

    public PasswordChangeResult ChangePassword(string name, string currentPassword, string newPassword, string confirmPassword)
    {
        currentPassword ??= string.Empty;
        newPassword ??= string.Empty;
        var stored = Find(name);
        if (stored == null || stored.Password == null || !PasswordsEqual(stored.Password, currentPassword))
        {
            return PasswordChangeResult.WrongCurrent;
        }
        if (PasswordRules.Validate(newPassword) != null) { return PasswordChangeResult.Invalid; }
        if (newPassword != confirmPassword) { return PasswordChangeResult.Mismatch; }
        if (PasswordsEqual(currentPassword, newPassword)) { return PasswordChangeResult.Same; }
        return _store.UpdatePassword(Normalize(name), newPassword) ? PasswordChangeResult.Changed : PasswordChangeResult.SaveFailed;
    }

    private StoredAccount? Find(string name)
    {
        var key = Normalize(name);
        return key.Length == 0 ? null : _store.FindByNameKey(key);
    }

    // Constant time over the content: both sides are hashed to a fixed length first so length does not leak.
    public static bool PasswordsEqual(string expected, string supplied)
    {
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
