namespace MUD.Web.Services;

public enum AccountRole { Player, Creator, Admin }

public record Account(string PlayerName, AccountRole Role);

// What the store hands back for a player document. Password is plaintext today (see specs/WEB.md).
public record StoredAccount(string PlayerName, string? Password, AccountRole Role);

public interface IAccountStore
{
    // Exact lookup by NameKey (already normalised by the caller). Never a substring match.
    StoredAccount? FindByNameKey(string nameKey);
}

public interface IAccountService
{
    Account? Authenticate(string name, string password);
    Account? GetByName(string name);
}
