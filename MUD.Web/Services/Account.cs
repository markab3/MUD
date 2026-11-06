namespace MUD.Web.Services;

public enum AccountRole { Player, Creator, Admin }

// The signed-in player as core reports it for the current request. The role is read from core on every request (D31), never kept in
// the cookie. Id is only set by tests and callers that know it; Retired stays on the record for the pages that show it.
public record Account(string PlayerName, AccountRole Role, string? Id = null, bool Retired = false);

public static class AccountNames
{
    // Same normalisation core uses for the player name key (trim, lower case).
    public static string Normalize(string? name) => (name ?? string.Empty).Trim().ToLowerInvariant();
}

// Who is signed in on the current request. Backed by the session the authentication handler resolved through core.
public interface IAccountService
{
    // The signed-in account when the name is the signed-in player's name, otherwise null.
    Account? GetByName(string name);
}
