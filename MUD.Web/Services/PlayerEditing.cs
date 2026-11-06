namespace MUD.Web.Services;

// Website player editing: core's Admin service owns every rule and every write (D27, D30, D34); the pages call it with the session
// token. PlayerRules below only decides which controls the page SHOWS to the signed-in editor; core re-checks every change.

// What an editor may do to a target, from the owner's decisions: you may edit only players of strictly LOWER role than your own
// (Creator edits Players; Admin edits Creators and Players; nobody edits an Admin), you cannot grant a role above your own, and
// retiring or un-retiring another player is an Admin power.
public static class PlayerRules
{
    public const int MaxFreeText = 32;

    public static bool CanEdit(AccountRole editor, AccountRole target) => editor >= AccountRole.Creator && editor > target;

    public static bool CanSetRole(AccountRole editor, AccountRole target, AccountRole newRole) =>
        editor == AccountRole.Admin && CanEdit(editor, target) && newRole <= editor;

    public static bool CanSetRetired(AccountRole editor, AccountRole target) =>
        editor == AccountRole.Admin && CanEdit(editor, target);

    // Defensive: lowering the role of an Admin when no other Admin exists would leave nobody able to grant roles.
    public static bool WouldRemoveLastAdmin(AccountRole target, AccountRole newRole, int adminCount) =>
        target == AccountRole.Admin && newRole < AccountRole.Admin && adminCount <= 1;

    // Free text (Race, Gender, Class): length-limited, no control characters, no fixed vocabulary.
    public static string? CheckFreeText(string label, string value)
    {
        if (value.Length > MaxFreeText) { return label + " must be at most " + MaxFreeText + " characters."; }
        if (value.Any(char.IsControl)) { return label + " must not contain control characters."; }
        return null;
    }
}
