# Admin service (phase 6, card P6-1)

The Admin service is the web front end's whole view of the game. It is served by core on `frontends.sock` only (D22, D66); a call to it on
`modules.sock` is answered 404 by the host. Contract: `contracts/mud/admin.proto`. Logic: `MUD.Core/Administration/AdminApi.cs`; the gRPC layer
is `AdminService.cs` and only maps refusals to status codes. Core is the only process that talks to MongoDB (the Admin service reads and
writes it from core), and everything that changes live game objects runs on the single game thread.

This card builds the service. `MUD.Web` still reads and writes the database itself until cards P6-2 to P6-4 move its pages, and the poll and
`Rev` stay until P6-5.

## Session tokens (D31)

- `WebAuthenticate(name, password, remote_key)` checks the password, the retirement flag and the login throttle, and returns an opaque token.
  The result is `AUTH_OK`, `AUTH_BAD_CREDENTIALS`, `AUTH_RETIRED` (right password, the character cannot be played), `AUTH_THROTTLED` or
  `AUTH_TRY_AGAIN_LATER`. The token is empty unless `AUTH_OK`.
- The throttle is the one telnet uses (D43), keyed by the `remote_key` the web passes in (the browser's address). A right password, even for a
  retired character, clears the count; anything else counts as a failure.
- The token is 256 random bits as URL-safe text. The table is in memory in core and is lost on a restart: every web user signs in again.
- Every call except `WebAuthenticate` and `Logout` carries the token. Core reads the player's CURRENT role on every call, never a role cached
  in the session, so a promotion or demotion applies to the next call with the same token. A session whose player is retired or gone is ended.
- Retiring a player through the service revokes all of that player's sessions at once, as does lowering their role.
- `Logout` always succeeds, also for a token that is already gone.

## Status codes

| Situation | gRPC status |
|---|---|
| Token empty, unknown, revoked, or its player is retired or gone | `Unauthenticated` |
| Token good, current role too low for the call | `PermissionDenied` |
| The game thread did not run the call (stopping, queue full, took too long) | `Unavailable` |
| A rule refused an edit (invalid field, creator editing an admin, default room) | status OK, reason in the reply's `result` |

Messages are fixed sentences. No token, password or stored value is put in a status or a log line.

## Calls and roles

| Call | Role |
|---|---|
| `WebAuthenticate`, `Logout` | none |
| `GetSession`, `ChangePassword` | any signed-in player |
| `ListRooms`, `GetRoom`, `CreateRoom`, `UpdateRoom`, `DeleteRoom`, `ListPlayers`, `GetPlayer`, `UpdatePlayer` | creator or admin |
| `ListAudit` | admin only |

Not in this card: `GetRoomMap` (the map is laid out by the web from `ListRooms`, as `RoomMapLayout` does today), `GetStats` and `Flush` (sketched in
CORE_API 7.2, not needed by any page). Each is a later addition if a page turns out to need it.

## Rules (carried over from `specs/WEB_EDITING.md` section 15)

- Room validation is `MUD.Data/RoomRules.cs`, the same rules the importer applies. Player fields use `RoomRules` for the descriptions; race,
  gender and class are free text, 32 characters, no control characters.
- An editor may edit only players of strictly lower role. Only an admin sets roles or retires, nobody grants above their own role, and the last
  admin cannot be demoted.
- Exits to a room that does not exist need `confirm_dangling`.
- The default room (`StartRoomKey`) can never be deleted. A deleted room's contents go to a random usable neighbour, else the default room; if
  neither exists the delete is refused before anything changes. Exits that led to the deleted room are removed from the other rooms.
- Last write wins (D30): there is no revision number, no conflict result. Two edits in a row both apply; the audit keeps the history.
- Audit is per changed field and audit first: rows are written with `Applied` false, the edit is applied, and the rows are then marked applied or
  rejected. If the rows cannot be written the edit is refused (`ADMIN_RESULT_AUDIT_FAILED`). Only web and creator edits are audited (D34).
- Passwords are plaintext by earlier owner decision. They are never logged, audited, listed or returned; player reads use an inclusion projection
  that does not name the password.

## Live objects

An edit to a room or player that is loaded changes the live object on the game thread after the database write was handed over (room edits go
through the persistence worker like every other room save; player edits are a field-level `$set` of the fields the game's own save leaves
alone, `PlayerFields.NotWrittenByGame`). Online players in the room or the player edited are told, as the poll told them: "Something feels
different...", "Something about you feels different...", "Your status has changed.", and the retired message followed by the normal quit path.

## Not verified here

MongoDB is not available in the sandbox. The Mongo-backed `MongoAdminQueries` and `MongoAuditStore` were not run against a database, and neither
was the persistence-worker path of a real room edit. Everything else is tested over in-memory fakes and over the real Kestrel host on a Unix
socket (`MUD.Tests/AdminApiTests.cs`, `AdminServiceHostTests.cs`).

## Open points for the owner

- Contract version: `ContractVersion.Current` stays 1. The note on that constant says to raise it with every `.proto` change, but the module
  tests use the literal 1 and `admin.proto` adds a service without touching the module wire. Raising it is a one-line change plus those tests.
- A web request and the poll can both still write the `player` and `room` collections until P6-5. Edits made through this service set the live
  object themselves, so a poll that sees the unchanged `Rev` does nothing; a website edit made the old way is still picked up by the poll.
- No token file or `FrontEndHello` handshake guards `frontends.sock` yet. The socket is mode 0600 inside a 0700 directory, which is the same
  protection the module socket had before its token. The front-end token of D66 is card P7-1; the web card (P6-2) decides whether the web
  presents one earlier.
