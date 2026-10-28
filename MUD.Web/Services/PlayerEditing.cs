using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MUD.Data;

namespace MUD.Web.Services;

// Website player editing (specs/WEB_EDITING.md, owner decisions in section 15). MUD.Web never references MUD.Core: it writes
// the `player` collection directly, field by field, and the running game picks the change up by polling `Rev`.
//
// The Password field is never read, shown, bound, audited or logged anywhere in this feature: the store reads through an
// explicit inclusion projection that does not name it, and none of the records below has a place to put it.

// One row of the list page.
public record PlayerSummary(string NameKey, string Name, string? Race, string? Class, string? Gender, string? Location,
    AccountRole Role, bool Retired);

public record PlayerPage(IReadOnlyList<PlayerSummary> Items, int Total, int Page, int PageSize)
{
    public int PageCount => Math.Max(1, (Total + PageSize - 1) / PageSize);
}

// Editable view of one stored player. RolesDocument is the raw stored roles component, used only to rewrite the creator and
// admin flags; it is never rendered.
public record PlayerDetail(string Id, string NameKey, string Name, string? Short, string? Long, string? Race, string? Gender,
    string? Class, bool Retired, AccountRole Role, int Rev, BsonDocument RolesDocument);

// Thin persistence seam. Every method is a single-document operation (the database is not a replica set).
public interface IPlayerEditStore
{
    // Name filter is a case-insensitive substring of PlayerName; pageNumber is 1-based.
    PlayerPage List(string? nameFilter, bool descending, int pageNumber, int pageSize);

    PlayerDetail? Find(string nameKey);

    // Atomic {$set: set, $inc: {Rev: 1}} on the document whose Rev equals expectedRev (a missing Rev counts as 0).
    StoreUpdateOutcome UpdateIfRev(string id, int expectedRev, BsonDocument set);

    int CountAdmins();
}

public record PlayerEdit(string NameKey, string? Short, string? Long, string? Race, string? Gender, string? Class,
    bool? Retired, AccountRole? Role, int ExpectedRev);

public enum PlayerEditStatus { Saved, NoChange, Invalid, Conflict, NotFound, Forbidden, LastAdmin, AuditFailed, Failed }

public record PlayerEditResult(PlayerEditStatus Status, PlayerDetail? Current = null, IReadOnlyList<RoomFieldError>? Errors = null)
{
    public IReadOnlyList<RoomFieldError> FieldErrors => Errors ?? Array.Empty<RoomFieldError>();
}

public interface IPlayerWriter
{
    PlayerEditResult Save(RoomActor actor, PlayerEdit edit);
}

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

public static class PlayerValidator
{
    public record Result(string Short, string? Long, string? Race, string? Gender, string? Class, IReadOnlyList<RoomFieldError> Errors);

    public static Result Validate(string? shortText, string? longText, string? race, string? gender, string? cls)
    {
        var errors = new List<RoomFieldError>();
        var normShort = RoomRules.NormalizeText(shortText ?? string.Empty);
        var normLong = RoomRules.NormalizeText(longText ?? string.Empty);

        var s = RoomRules.CheckShort(normShort);
        if (s != null) { errors.Add(new RoomFieldError("Short", Cap(s))); }
        // A player's long description may be empty (new characters have none); when present it follows the room rules.
        if (normLong.Length > 0)
        {
            var l = RoomRules.CheckLong(normLong);
            if (l != null) { errors.Add(new RoomFieldError("Long", Cap(l))); }
        }

        string? r = Free(errors, "Race", race), g = Free(errors, "Gender", gender), c = Free(errors, "Class", cls);
        return new Result(normShort, normLong.Length == 0 ? null : normLong, r, g, c, errors);
    }

    private static string? Free(List<RoomFieldError> errors, string label, string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        var problem = PlayerRules.CheckFreeText(label, trimmed);
        if (problem != null) { errors.Add(new RoomFieldError(label, problem)); }
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string Cap(string message) => char.ToUpperInvariant(message[0]) + message.Substring(1);
}

public class PlayerWriter : IPlayerWriter
{
    private readonly IPlayerEditStore _store;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;

    public PlayerWriter(IPlayerEditStore store, IAuditLog audit, TimeProvider? time = null, ILogger? log = null)
    {
        _store = store;
        _audit = audit;
        _time = time ?? TimeProvider.System;
        _log = log;
    }

    public PlayerEditResult Save(RoomActor actor, PlayerEdit edit)
    {
        if (actor == null || actor.Role < AccountRole.Creator) { return new(PlayerEditStatus.Forbidden); }
        try
        {
            var current = _store.Find(edit.NameKey);
            if (current == null) { return new(PlayerEditStatus.NotFound); }

            // Defensive last-Admin guard, ahead of the generic rule so it is reported as such.
            if (edit.Role.HasValue && PlayerRules.WouldRemoveLastAdmin(current.Role, edit.Role.Value, _store.CountAdmins()))
            {
                return new(PlayerEditStatus.LastAdmin, current);
            }
            if (!PlayerRules.CanEdit(actor.Role, current.Role)) { return new(PlayerEditStatus.Forbidden, current); }

            AccountRole? roleChange = edit.Role.HasValue && edit.Role.Value != current.Role ? edit.Role : null;
            if (roleChange.HasValue && !PlayerRules.CanSetRole(actor.Role, current.Role, roleChange.Value)) { return new(PlayerEditStatus.Forbidden, current); }
            bool? retiredChange = edit.Retired.HasValue && edit.Retired.Value != current.Retired ? edit.Retired : null;
            if (retiredChange.HasValue && !PlayerRules.CanSetRetired(actor.Role, current.Role)) { return new(PlayerEditStatus.Forbidden, current); }

            var v = PlayerValidator.Validate(edit.Short, edit.Long, edit.Race, edit.Gender, edit.Class);
            if (v.Errors.Count > 0) { return new(PlayerEditStatus.Invalid, current, v.Errors); }

            if (current.Rev != edit.ExpectedRev) { return new(PlayerEditStatus.Conflict, current); }

            var set = new BsonDocument();
            var rows = new List<(string Field, BsonValue Old, BsonValue New)>();
            Diff(rows, set, PlayerFields.ShortDescription, current.Short, v.Short);
            Diff(rows, set, PlayerFields.LongDescription, current.Long, v.Long);
            Diff(rows, set, PlayerFields.Race, current.Race, v.Race);
            Diff(rows, set, PlayerFields.Gender, current.Gender, v.Gender);
            Diff(rows, set, PlayerFields.Class, current.Class, v.Class);
            if (retiredChange.HasValue)
            {
                set[PlayerFields.Retired] = retiredChange.Value;
                rows.Add((PlayerFields.Retired, current.Retired, retiredChange.Value));
            }
            if (roleChange.HasValue)
            {
                set[PlayerFields.Roles] = WithRole(current.RolesDocument, roleChange.Value);
                rows.Add(("Role", current.Role.ToString(), roleChange.Value.ToString()));
            }

            if (rows.Count == 0) { return new(PlayerEditStatus.NoChange, current); }

            var entries = BuildEntries(actor, current, rows);
            if (!TryAudit(entries)) { return new(PlayerEditStatus.AuditFailed, current); }

            set[PlayerFields.WebUpdatedAt] = _time.GetUtcNow().UtcDateTime;
            var outcome = _store.UpdateIfRev(current.Id, edit.ExpectedRev, set);
            if (outcome != StoreUpdateOutcome.Applied)
            {
                Finish(entries, false);
                var latest = _store.Find(edit.NameKey);
                return latest == null ? new(PlayerEditStatus.NotFound) : new(PlayerEditStatus.Conflict, latest);
            }
            Finish(entries, true);
            return new(PlayerEditStatus.Saved, _store.Find(edit.NameKey));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Generic result: no exception text, no values.
            _log?.LogError("Player save failed ({ExceptionType}).", ex.GetType().Name);
            return new(PlayerEditStatus.Failed);
        }
    }

    // The roles component after a role change: creator and admin set from the wanted role (admin implies creator), every
    // other field of the stored document (the game's currentDirectory) kept. Written as the whole "roles" field (D45, G30).
    public static BsonDocument WithRole(BsonDocument existing, AccountRole role)
    {
        var result = existing.DeepClone().AsBsonDocument;
        result["creator"] = role >= AccountRole.Creator;
        result["admin"] = role == AccountRole.Admin;
        return result;
    }

    // null and empty are the same "unset" value.
    private static void Diff(List<(string, BsonValue, BsonValue)> rows, BsonDocument set, string field, string? oldValue, string? newValue)
    {
        var o = string.IsNullOrEmpty(oldValue) ? null : oldValue;
        if (string.Equals(o, newValue, StringComparison.Ordinal)) { return; }
        set[field] = newValue == null ? BsonNull.Value : newValue;
        rows.Add((field, o == null ? BsonNull.Value : o, newValue == null ? BsonNull.Value : newValue));
    }

    private List<AuditEntry> BuildEntries(RoomActor actor, PlayerDetail target, IEnumerable<(string Field, BsonValue Old, BsonValue New)> rows)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return rows.Select(r => new AuditEntry(ObjectId.GenerateNewId().ToString(), now, actor.Id, actor.Name, actor.Role.ToString(),
            "player", target.Id, target.Name, r.Field, r.Old, r.New, target.Rev, target.Rev + 1)).ToList();
    }

    // Audit-first: if the record cannot be written the edit is refused and nothing changes.
    private bool TryAudit(IReadOnlyList<AuditEntry> entries)
    {
        try { _audit.Insert(entries); return true; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogError("Audit write failed ({ExceptionType}); the edit was refused.", ex.GetType().Name);
            return false;
        }
    }

    private void Finish(IReadOnlyList<AuditEntry> entries, bool applied)
    {
        try { _audit.SetOutcome(entries.Select(e => e.Id).ToList(), applied); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The row stays Applied:false, which marks it for review; the edit itself is not undone.
            _log?.LogError("Could not mark audit rows as {Outcome} ({ExceptionType}).", applied ? "applied" : "rejected", ex.GetType().Name);
        }
    }
}
