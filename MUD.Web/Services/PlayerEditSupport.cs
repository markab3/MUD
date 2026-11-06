using MongoDB.Bson;

namespace MUD.Web.Services;

// Types the player pages still use. They were part of the room editor; the room pages now go through core's Admin service and no
// longer need them. Card P6-4 moves the player pages onto the Admin service and deletes this file.

// Who is writing. Id and Role come from the CURRENT account of the request, see RoomActors.
public record RoomActor(string Id, string Name, AccountRole Role);

public record RoomFieldError(string Field, string Message);

public enum StoreUpdateOutcome { Applied, Conflict, Missing }

public record AuditEntry(string Id, DateTime At, string ActorId, string ActorName, string ActorRole, string Entity,
    string EntityId, string EntityName, string Field, BsonValue Old, BsonValue New, int RevBefore, int RevAfter);

// Append-only audit trail (specs/WEB_EDITING.md section 7, audit-first). Insert throws when it cannot write.
public interface IAuditLog
{
    void Insert(IReadOnlyList<AuditEntry> entries);

    // Marks rows written by Insert as applied, or as rejected when the write they announced did not happen.
    void SetOutcome(IReadOnlyList<string> ids, bool applied);
}

// Resolves the signed-in user to an actor using the account of the current request (role read from core, D31).
public static class RoomActors
{
    public static RoomActor? Resolve(IAccountService accounts, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) { return null; }
        var account = accounts.GetByName(name);
        if (account == null || account.Retired || account.Role < AccountRole.Creator) { return null; }
        return new RoomActor(account.Id ?? AccountNames.Normalize(account.PlayerName), account.PlayerName, account.Role);
    }
}
