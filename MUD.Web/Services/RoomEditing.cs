using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MUD.Data;

namespace MUD.Web.Services;

// Website room editing (specs/WEB_EDITING.md). MUD.Web never references MUD.Core: it writes the `room` collection
// directly, field by field, and the running game picks the change up by polling `Rev`.

public record RoomExitData(string Name, string To);

// Definition fields of one stored room plus its Rev. Nothing here is state (no Items, no features).
public record RoomDetail(string Key, string Short, string Long, string? Area, int? X, int? Y, int? Z,
    IReadOnlyList<RoomExitData> Exits, int Rev);

// Who is writing. Id and Role come from the CURRENT stored account (not the cookie), see RoomActors.
public record RoomActor(string Id, string Name, AccountRole Role);

public enum StoreUpdateOutcome { Applied, Conflict, Missing }

// Thin persistence seam. Every method is a single-document atomic operation (the database is not a replica set).
public interface IRoomEditStore
{
    RoomDetail? Find(string key);

    bool Exists(string key);

    // Atomic {$set: set, $inc: {Rev: 1}} on the document whose Rev equals expectedRev (a missing Rev counts as 0).
    // The set document is built by the writer; the store adds nothing to it.
    StoreUpdateOutcome UpdateIfRev(string key, int expectedRev, BsonDocument set);

    // $setOnInsert upsert: false when a room with this key already exists (nothing is written then).
    bool InsertIfAbsent(string key, BsonDocument document);

    // Deletes the room only if its Rev still equals expectedRev.
    StoreUpdateOutcome DeleteIfRev(string key, int expectedRev);

    // Rooms that have an exit leading to key.
    IReadOnlyList<RoomDetail> FindReferencing(string key);
}

public record AuditEntry(string Id, DateTime At, string ActorId, string ActorName, string ActorRole, string Entity,
    string EntityId, string EntityName, string Field, BsonValue Old, BsonValue New, int RevBefore, int RevAfter);

// Append-only audit trail (specs/WEB_EDITING.md section 7, audit-first). Insert throws when it cannot write.
public interface IAuditLog
{
    void Insert(IReadOnlyList<AuditEntry> entries);

    // Marks rows written by Insert as applied, or as rejected when the write they announced did not happen.
    void SetOutcome(IReadOnlyList<string> ids, bool applied);
}

public record RoomFieldError(string Field, string Message);

public record RoomEdit(string Key, string? Short, string? Long, string? Area, int? X, int? Y, int? Z,
    IReadOnlyList<RoomExitData> Exits, int ExpectedRev, bool ConfirmDangling);

public enum RoomSaveStatus
{
    Saved, NoChange, Created, Deleted, Invalid, Conflict, NotFound, KeyExists, Forbidden, Refused, AuditFailed, Failed
}

public record RoomSaveResult(RoomSaveStatus Status, RoomDetail? Current = null,
    IReadOnlyList<RoomFieldError>? Errors = null, string? Message = null)
{
    public IReadOnlyList<RoomFieldError> FieldErrors => Errors ?? Array.Empty<RoomFieldError>();
}

public interface IRoomWriter
{
    RoomSaveResult Save(RoomActor actor, RoomEdit edit);

    // key is the FULL key (area.local). Same validation as Save; fails if the key exists.
    RoomSaveResult Create(RoomActor actor, RoomEdit create);

    RoomSaveResult Delete(RoomActor actor, string key, int expectedRev);
}

// The one place the website validates a room. Every verdict comes from MUD.Data.RoomRules, the same rules the
// importer applies, so the two cannot disagree.
public static class RoomValidator
{
    // Web-only guard on form size; the importer has no such limit.
    public const int MaxExits = 32;

    public record Result(string Short, string Long, string Area, IReadOnlyList<RoomExitData> Exits,
        IReadOnlyList<RoomFieldError> Errors, IReadOnlyList<string> Dangling);

    // exists answers "is there a stored room with this key"; keys equal to selfKey always count as existing.
    public static Result Validate(string key, string? shortText, string? longText, string? area,
        IEnumerable<RoomExitData> exits, Func<string, bool> exists)
    {
        var errors = new List<RoomFieldError>();
        var normShort = RoomRules.NormalizeText(shortText ?? string.Empty);
        var normLong = RoomRules.NormalizeText(longText ?? string.Empty);
        var normArea = (area ?? string.Empty).Trim();

        var s = RoomRules.CheckShort(normShort);
        if (s != null) { errors.Add(new RoomFieldError("Short", Cap(s))); }
        var l = RoomRules.CheckLong(normLong);
        if (l != null) { errors.Add(new RoomFieldError("Long", Cap(l))); }

        if (RoomRules.LooksLikeObjectId(key) && key.IndexOf('.') >= 0)
        {
            errors.Add(new RoomFieldError("Key", "Authored keys must not look like generated ids."));
        }
        if (normArea.Length > 0 || key.IndexOf('.') >= 0)
        {
            var a = RoomRules.CheckAreaPrefix(normArea);
            if (a != null) { errors.Add(new RoomFieldError("Area", Cap(a))); }
            else
            {
                var m = RoomRules.CheckKeyMatchesArea(key, normArea);
                if (m != null) { errors.Add(new RoomFieldError("Area", Cap(m))); }
            }
        }

        var cleaned = new List<RoomExitData>();
        var all = exits.ToList();
        if (all.Count > MaxExits) { errors.Add(new RoomFieldError("Exits", $"At most {MaxExits} exits are supported.")); }
        for (var i = 0; i < all.Count && i < MaxExits; i++)
        {
            var dir = (all[i]?.Name ?? string.Empty).Trim();
            var to = (all[i]?.To ?? string.Empty).Trim();
            if (dir.Length == 0 && to.Length == 0) { continue; }
            var d = RoomRules.CheckDirection(dir);
            if (d != null) { errors.Add(new RoomFieldError($"Exits[{i}].Dir", Cap(d))); }
            var t = RoomRules.CheckRoomKeyReference(to);
            if (t != null) { errors.Add(new RoomFieldError($"Exits[{i}].To", Cap(t))); }
            cleaned.Add(new RoomExitData(dir, to));
        }
        foreach (var dup in RoomRules.DuplicateDirections(cleaned.Select(e => e.Name)))
        {
            errors.Add(new RoomFieldError("Exits", "Two exits use the same direction."));
            break;
        }

        var dangling = new List<string>();
        if (errors.Count == 0)
        {
            foreach (var e in cleaned)
            {
                if (e.To != key && !exists(e.To) && !dangling.Contains(e.To)) { dangling.Add(e.To); }
            }
        }
        return new Result(normShort, normLong, normArea, cleaned, errors, dangling);
    }

    // RoomRules messages start lowercase and name the field; make them read as sentences.
    private static string Cap(string message) => char.ToUpperInvariant(message[0]) + message.Substring(1);
}

public class RoomWriter : IRoomWriter
{
    // The ONLY fields a web write may put in a $set (specs/WEB_EDITING.md 4.1) plus bookkeeping.
    public static readonly string[] AllowedFields = { "ShortDescription", "LongDescription", "Area", "X", "Y", "Z", "Exits" };
    public const string UpdatedAtField = "WebUpdatedAt";

    private readonly IRoomEditStore _store;
    private readonly IAuditLog _audit;
    private readonly string? _startRoomKey;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;

    public RoomWriter(IRoomEditStore store, IAuditLog audit, string? startRoomKey, TimeProvider? time = null, ILogger? log = null)
    {
        _store = store;
        _audit = audit;
        _startRoomKey = string.IsNullOrWhiteSpace(startRoomKey) ? null : startRoomKey;
        _time = time ?? TimeProvider.System;
        _log = log;
    }

    public RoomSaveResult Save(RoomActor actor, RoomEdit edit)
    {
        if (!MayEdit(actor)) { return new(RoomSaveStatus.Forbidden); }
        try
        {
            var current = _store.Find(edit.Key);
            if (current == null) { return new(RoomSaveStatus.NotFound); }

            var v = RoomValidator.Validate(edit.Key, edit.Short, edit.Long, edit.Area, edit.Exits, _store.Exists);
            if (v.Errors.Count > 0) { return new(RoomSaveStatus.Invalid, current, v.Errors); }
            var danglingError = DanglingError(v, edit.ConfirmDangling);
            if (danglingError != null) { return new(RoomSaveStatus.Invalid, current, new[] { danglingError }); }

            if (current.Rev != edit.ExpectedRev) { return new(RoomSaveStatus.Conflict, current); }

            var set = new BsonDocument();
            var rows = new List<(string Field, BsonValue Old, BsonValue New)>();
            Diff(rows, set, "ShortDescription", current.Short, v.Short);
            Diff(rows, set, "LongDescription", current.Long, v.Long);
            Diff(rows, set, "Area", current.Area ?? string.Empty, v.Area);
            DiffInt(rows, set, "X", current.X, edit.X);
            DiffInt(rows, set, "Y", current.Y, edit.Y);
            DiffInt(rows, set, "Z", current.Z, edit.Z);
            var oldExits = ExitsToBson(current.Exits);
            var newExits = ExitsToBson(v.Exits);
            if (!oldExits.Equals(newExits)) { set["Exits"] = newExits; rows.Add(("Exits", oldExits, newExits)); }

            if (rows.Count == 0) { return new(RoomSaveStatus.NoChange, current); }

            var entries = BuildEntries(actor, current.Key, rows, current.Rev, current.Rev + 1);
            if (!TryAudit(entries)) { return new(RoomSaveStatus.AuditFailed, current); }

            set[UpdatedAtField] = _time.GetUtcNow().UtcDateTime;
            var outcome = _store.UpdateIfRev(edit.Key, edit.ExpectedRev, set);
            if (outcome != StoreUpdateOutcome.Applied)
            {
                Finish(entries, false);
                var latest = _store.Find(edit.Key);
                return latest == null ? new(RoomSaveStatus.NotFound) : new(RoomSaveStatus.Conflict, latest);
            }
            Finish(entries, true);
            return new(RoomSaveStatus.Saved, _store.Find(edit.Key));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Failed("save", edit.Key, ex);
        }
    }

    public RoomSaveResult Create(RoomActor actor, RoomEdit create)
    {
        if (!MayEdit(actor)) { return new(RoomSaveStatus.Forbidden); }
        try
        {
            var v = RoomValidator.Validate(create.Key, create.Short, create.Long, create.Area, create.Exits, _store.Exists);
            var errors = v.Errors.ToList();
            if (create.Key.IndexOf('.') < 0)
            {
                // New rooms are always authored: '<area prefix>.<local key>'.
                errors.Add(new RoomFieldError("Key", "A room key must look like area.room."));
            }
            if (errors.Count > 0) { return new(RoomSaveStatus.Invalid, null, errors); }
            var danglingError = DanglingError(v, create.ConfirmDangling);
            if (danglingError != null) { return new(RoomSaveStatus.Invalid, null, new[] { danglingError }); }

            if (_store.Exists(create.Key)) { return new(RoomSaveStatus.KeyExists); }

            // Same shape the importer inserts: every definition field, empty state.
            var doc = new BsonDocument
            {
                { "ShortDescription", v.Short },
                { "LongDescription", v.Long },
                { "Handles", new BsonArray() },
                { "Adjectives", new BsonArray() },
                { "Plurals", new BsonArray() },
                { "Exits", ExitsToBson(v.Exits) },
                { "Features", new BsonArray() },
                { "ExtendedProperties", new BsonArray() },
                { "Area", v.Area },
                { "X", ToBson(create.X) },
                { "Y", ToBson(create.Y) },
                { "Z", ToBson(create.Z) },
                { "Items", new BsonArray() },
                { "Rev", 0 },
                { UpdatedAtField, _time.GetUtcNow().UtcDateTime },
            };
            var rows = new List<(string, BsonValue, BsonValue)>();
            foreach (var f in new[] { "ShortDescription", "LongDescription", "Area", "X", "Y", "Z", "Exits" })
            {
                rows.Add((f, BsonNull.Value, doc[f]));
            }
            var entries = BuildEntries(actor, create.Key, rows, 0, 0);
            if (!TryAudit(entries)) { return new(RoomSaveStatus.AuditFailed); }

            if (!_store.InsertIfAbsent(create.Key, doc))
            {
                Finish(entries, false);
                return new(RoomSaveStatus.KeyExists);
            }
            Finish(entries, true);
            return new(RoomSaveStatus.Created, _store.Find(create.Key));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Failed("create", create.Key, ex);
        }
    }

    public RoomSaveResult Delete(RoomActor actor, string key, int expectedRev)
    {
        if (!MayEdit(actor)) { return new(RoomSaveStatus.Forbidden); }
        try
        {
            if (_startRoomKey == null)
            {
                return new(RoomSaveStatus.Refused, null, null, "The default room is not configured for the website, so rooms cannot be deleted.");
            }
            if (string.Equals(key, _startRoomKey, StringComparison.Ordinal))
            {
                return new(RoomSaveStatus.Refused, null, null, "The default room can never be deleted.");
            }
            var current = _store.Find(key);
            if (current == null) { return new(RoomSaveStatus.NotFound); }
            if (current.Rev != expectedRev) { return new(RoomSaveStatus.Conflict, current); }

            // Everything inside is moved by the game to a random usable neighbour, else to the default room. If neither
            // exists nothing could be moved, so refuse before changing anything.
            var hasNeighbour = current.Exits.Any(e => e.To != key && _store.Exists(e.To));
            if (!hasNeighbour && !_store.Exists(_startRoomKey))
            {
                return new(RoomSaveStatus.Refused, current, null,
                    "This room has no usable exit and the default room does not exist, so its contents could not be moved. Nothing was deleted.");
            }

            var referencing = _store.FindReferencing(key).Where(r => r.Key != key).ToList();

            var entries = new List<AuditEntry>();
            var rows = new List<(string, BsonValue, BsonValue)>
            {
                ("ShortDescription", current.Short, BsonNull.Value),
                ("LongDescription", current.Long, BsonNull.Value),
                ("Area", current.Area ?? string.Empty, BsonNull.Value),
                ("X", ToBson(current.X), BsonNull.Value),
                ("Y", ToBson(current.Y), BsonNull.Value),
                ("Z", ToBson(current.Z), BsonNull.Value),
                ("Exits", ExitsToBson(current.Exits), BsonNull.Value),
            };
            entries.AddRange(BuildEntries(actor, key, rows, current.Rev, current.Rev));
            foreach (var r in referencing)
            {
                var kept = r.Exits.Where(e => e.To != key).ToList();
                entries.AddRange(BuildEntries(actor, r.Key,
                    new[] { ("Exits", (BsonValue)ExitsToBson(r.Exits), (BsonValue)ExitsToBson(kept)) }, r.Rev, r.Rev + 1));
            }
            if (!TryAudit(entries)) { return new(RoomSaveStatus.AuditFailed, current); }

            // The delete is the commit point. Exits that pointed at the room are removed afterwards, one document at a
            // time (no transactions); a failure there leaves a dangling exit, which the game already guards against.
            var deleted = _store.DeleteIfRev(key, expectedRev);
            if (deleted != StoreUpdateOutcome.Applied)
            {
                Finish(entries, false);
                var latest = _store.Find(key);
                return latest == null ? new(RoomSaveStatus.NotFound) : new(RoomSaveStatus.Conflict, latest);
            }

            var rejected = new List<string>();
            foreach (var r in referencing)
            {
                if (!StripExitsTo(r, key))
                {
                    rejected.AddRange(entries.Where(e => e.EntityId == r.Key).Select(e => e.Id));
                }
            }
            var appliedIds = entries.Select(e => e.Id).Except(rejected).ToList();
            TrySetOutcome(appliedIds, true);
            TrySetOutcome(rejected, false);
            return rejected.Count == 0
                ? new(RoomSaveStatus.Deleted)
                : new(RoomSaveStatus.Deleted, null, null, "The room was deleted, but some exits that led to it could not be removed. They are harmless and can be removed by editing those rooms.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Failed("delete", key, ex);
        }
    }

    private bool StripExitsTo(RoomDetail referencing, string deletedKey)
    {
        var room = referencing;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var kept = room.Exits.Where(e => e.To != deletedKey).ToList();
            if (kept.Count == room.Exits.Count) { return true; }
            var set = new BsonDocument { { "Exits", ExitsToBson(kept) }, { UpdatedAtField, _time.GetUtcNow().UtcDateTime } };
            var outcome = _store.UpdateIfRev(room.Key, room.Rev, set);
            if (outcome == StoreUpdateOutcome.Applied) { return true; }
            if (outcome == StoreUpdateOutcome.Missing) { return true; }
            var fresh = _store.Find(room.Key);
            if (fresh == null) { return true; }
            room = fresh;
        }
        return false;
    }

    private static bool MayEdit(RoomActor? actor) => actor != null && actor.Role >= AccountRole.Creator;

    private RoomFieldError? DanglingError(RoomValidator.Result v, bool confirm) =>
        v.Dangling.Count > 0 && !confirm
            ? new RoomFieldError("ConfirmDangling",
                "Some exits lead to rooms that do not exist (" + string.Join(", ", v.Dangling) + "). Tick the confirmation box to save them anyway.")
            : null;

    private void Diff(List<(string, BsonValue, BsonValue)> rows, BsonDocument set, string field, string oldValue, string newValue)
    {
        if (string.Equals(oldValue, newValue, StringComparison.Ordinal)) { return; }
        set[field] = newValue;
        rows.Add((field, oldValue, newValue));
    }

    // Blank is null (unplaced), never 0.
    private void DiffInt(List<(string, BsonValue, BsonValue)> rows, BsonDocument set, string field, int? oldValue, int? newValue)
    {
        if (oldValue == newValue) { return; }
        set[field] = ToBson(newValue);
        rows.Add((field, ToBson(oldValue), ToBson(newValue)));
    }

    private static BsonValue ToBson(int? value) => value.HasValue ? new BsonInt32(value.Value) : BsonNull.Value;

    public static BsonArray ExitsToBson(IEnumerable<RoomExitData> exits) =>
        new(exits.Select(e => new BsonDocument { { "Name", e.Name }, { "DestinationId", e.To } }));

    private List<AuditEntry> BuildEntries(RoomActor actor, string roomKey, IEnumerable<(string Field, BsonValue Old, BsonValue New)> rows,
        int revBefore, int revAfter)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return rows.Select(r => new AuditEntry(ObjectId.GenerateNewId().ToString(), now, actor.Id, actor.Name, actor.Role.ToString(),
            "room", roomKey, roomKey, r.Field, r.Old, r.New, revBefore, revAfter)).ToList();
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

    private void Finish(IReadOnlyList<AuditEntry> entries, bool applied) => TrySetOutcome(entries.Select(e => e.Id).ToList(), applied);

    private void TrySetOutcome(IReadOnlyList<string> ids, bool applied)
    {
        if (ids.Count == 0) { return; }
        try { _audit.SetOutcome(ids, applied); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The row stays Applied:false, which marks it for review; the edit itself is not undone.
            _log?.LogError("Could not mark audit rows as {Outcome} ({ExceptionType}).", applied ? "applied" : "rejected", ex.GetType().Name);
        }
    }

    // Generic result: no exception text, no values.
    private RoomSaveResult Failed(string what, string key, Exception ex)
    {
        _log?.LogError("Room {What} failed for {Key} ({ExceptionType}).", what, key, ex.GetType().Name);
        return new(RoomSaveStatus.Failed);
    }
}

// Resolves the signed-in user to an actor using the CURRENT stored account, so a revoked creator loses power at once.
public static class RoomActors
{
    public static RoomActor? Resolve(IAccountService accounts, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) { return null; }
        var account = accounts.GetByName(name);
        if (account == null || account.Retired || account.Role < AccountRole.Creator) { return null; }
        return new RoomActor(account.Id ?? AccountService.Normalize(account.PlayerName), account.PlayerName, account.Role);
    }
}
