namespace MUD.Web.Services;

// Password rules for a password change. Duplicates MUD.Core PlayerAccounts.ValidatePassword on purpose (the web
// project does not reference MUD.Core); MUD.Web.Tests PasswordRuleParityTests asserts the two agree.
public static class PasswordRules
{
    public const int MinLength = 6;
    public const int MaxLength = 64;

    // Null when acceptable, otherwise a message for the user.
    public static string? Validate(string? password)
    {
        if (password == null || password.Length < MinLength || password.Length > MaxLength)
        {
            return $"Passwords must be {MinLength} to {MaxLength} characters long.";
        }
        if (password.Any(char.IsControl))
        {
            return "Passwords may not contain control characters.";
        }
        return null;
    }
}
