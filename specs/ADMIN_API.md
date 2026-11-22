# Admin service (phase 6, card P6-1)

The Admin service is the web front end's whole view of the game. It is served by core on `frontends.sock` only (D22, D66); a call to it on
`modules.sock` is answered 404 by the host. Contract: `contracts/mud/admin.proto`. Logic: `MUD.Core/Administration/AdminApi.cs`; the gRPC layer
is `AdminService.cs` and only maps refusals to status codes. Core is the only process that talks to MongoDB (the Admin service reads and
writes it from core), and everything that changes live game objects runs on the single game thread.

This card built the service. Cards P6-2 to P6-4 moved every `MUD.Web` page onto it, and card P6-5 removed the poll, `Rev` and the web's Mongo
reference, so the web no longer reads or writes the database itself.

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
| `ListRooms`, `GetRoom`, `CreateRoom`, `UpdateRoom`, `DeleteRoom`, `MoveRoom`, `ListPlayers`, `GetPlayer`, `UpdatePlayer`, `ListModules` | creator or admin |
| `ListAudit`, `StartModule`, `StopModule`, `RestartModule` | admin only |

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
- Last write wins (D30): there is no revision number, no conflict result (this holds for room Features too). Two edits in a row both apply; the audit keeps the history.
- Audit is per changed field and audit first: rows are written with `Applied` false, the edit is applied, and the rows are then marked applied or
  rejected. If the rows cannot be written the edit is refused (`ADMIN_RESULT_AUDIT_FAILED`). Only web and creator edits are audited (D34).
- Passwords are plaintext by earlier owner decision. They are never logged, audited, listed or returned; player reads use an inclusion projection
  that does not name the password.

## Room features

Owner decision (supersedes `specs/WEB_EDITING.md` Q3): a creator can add, edit, remove and reorder a room's Features. Rules are
`specs/ROOM_FILES.md` section 4.4 and are enforced by `MUD.Data/RoomRules.cs` (`CheckFeature*`, `DuplicateFeatureShorts`) through
`AdminFeatureValidator`, the importer's rules.

- Contract (`contracts/mud/admin.proto`): `RoomFeature { short, long, room_line, detail, handles, adjectives, plurals }`. `RoomDetail.features`
  carries the room's Features in stored order (`GetRoom`, `ListRooms` and every reply). `RoomEditRequest.features` is the FULL replacement list.
- **`features_present` flag.** A caller that predates the field sends nothing, which proto3 cannot tell from an empty list. So the list is applied
  only when `RoomEditRequest.features_present` is true; false leaves every Feature exactly as it is (and writes no `Features` audit row). True
  with an empty list removes all Features. The web always sets it true.
- Identity is `short`, case-insensitive. Order of the list is the stored order, so reordering is sending the list in the new order. `CreateRoom`
  ignores the fields (a new room has no Features).
- Validation (field names in `FieldError`: `Features[i].Short|Long|RoomLine|Detail|Handles|Adjectives|Plurals`, `Features`): text is normalised per
  ROOM_FILES.md section 11 (trailing whitespace trimmed, LF); short required, one line, 100 characters; long required, 4000; room_line and detail
  optional, 4000, empty stored as null; two Features with the same short (any case) are refused. The importer has no handle requirement and no
  pattern for handles, adjectives and plurals, so neither does the web: a Feature may have no handles, and each word only has to be non-empty, at most
  64 characters and free of control characters. Guards the importer lacks: at most 64 Features per room and 32 words per list.
- Audit: one `Features` row per edit (entity `room`), old and new value both the whole list as an array of `{Short, Long, RoomLine, Detail,
  Handles, Adjectives, Plurals}`. Audit first, then persist, then the live room, as for other room fields. An identical list is `NO_CHANGE`.
- Revision check: there is none (D30, last write wins), so features follow the same rule and there is no Rev conflict result. The audit row
  holds the previous list.
- Behavior and component config: a Feature can carry `components` (for example a module's storm Feature) that the web cannot represent. A requested
  Feature that matches a stored one by short keeps that Feature object and stored document: only short, long, room_line, detail, handles,
  adjectives and plurals are overwritten, so its id, components and any other field survive. A Feature absent from the list is removed with
  everything on it; a new short creates a new Feature with id `<room key>#<short lowercased>`, as the importer does. The audit shows only the
  web-representable fields.
- Never touched: `Items`, occupants. The live change is applied with the other room fields on the game thread; occupants get the usual
  "Something feels different..." message.

## Live objects

An edit to a room or player that is loaded changes the live object on the game thread after the database write was handed over (room edits go
through the persistence worker like every other room save; player edits are a field-level `$set` of the edited fields, and the live
object is set afterwards; the game's own save writes the whole live player, since core is the only writer). Online players in the room or the player edited are told, as the poll told them: "Something feels
different...", "Something about you feels different...", "Your status has changed.", and the retired message followed by the normal quit path.

## Modules (`ListModules`, `StartModule`, `StopModule`, `RestartModule`)

Backend of the website Modules page. All logic stays in `ModuleSupervisor` (D67, MODULES section 3); `AdminApi` reaches it through the small
`IModuleControl` seam (`SupervisorModuleControl` in production, a fake in tests).

- `ListModules` (creator or admin) returns every module the supervisor knows from its directory scan: stopped, never-started, failed and
  invalid ones included, ordered by name. Each `ModuleInfo` has `name`, `version` (from `--describe`; empty if it could not be described),
  `state` (STOPPED, STARTING, CONNECTED, READY, BACKOFF, FAILED, REFUSED, STOPPING), `running` (derived: CONNECTED or READY), `required`,
  `last_failure` (the scan problem, else the refusal reason, else the supervisor's last detail) and `folder_missing`. The supervisor has no
  QUARANTINED state; in the code the spec's FAILED/QUARANTINED are FAILED and REFUSED. With no supervisor the list is empty.
- `StartModule`, `StopModule` and `RestartModule` (admin only; the role is re-read per call and checked on the game thread together with the call) queue the request
  on the supervisor with `RequestStart` / `RequestStop` / `RequestRestart`, which do the process work on the supervisor's own thread, never the game thread. `OK`
  therefore means "queued"; `ListModules` shows the outcome. Start clears FAILED, REFUSED and the crash history, exactly like `modules start`.
- Results: `OK` queued; `NO_CHANGE` already running or starting (start), already stopped or stopping (stop), with a message; `NOT_FOUND` unknown
  module; `REFUSED` a module that cannot start (its scan problem is in the message), a start or restart while it is stopping, or no supervisor
  (the "unavailable" message); `AUDIT_FAILED`. Nothing is audited for a refusal or a no-change. The messages for the UI are: requested
  (`OK`), unknown module (`NOT_FOUND`) and unavailable (`REFUSED`, no supervisor).
- Audit first: one row per action (`entity` `module`, `entity_id` the module name, field `Action`, new value `start`, `stop` or `restart`, source `web`, actor
  from the session), inserted before the request is queued and marked applied once queued. No row, no request.
- `RestartModule` calls `RequestRestart` (the in-game `modules restart`). A Ready module is replaced by an overlap: the new instance starts
  first and the old one ends only after the new one is Ready (MODULES 5.3a); a failed restart leaves the old instance running. Restart of
  a stopped module is a start: it is audited and answered as a start (`start` row, start message). A restart of a module that cannot start
  (scan problem, folder gone) is refused like a start, and a restart while it is stopping is refused. `rescan` is not exposed.
- Required modules: every module, required ones included, can be started, stopped and restarted. `ListModules` still returns `required`
  so the UI can mark them. D21/D70 gate only start-up (`ModuleSupervisor.WaitForRequiredModules`, called once from `Program.cs`): after the
  game is open nothing watches the required flag, so stopping a required module does not make core exit. The game keeps running without
  the module and whatever the module provided (its commands, rooms behaviour and timers) is simply absent until it is started again. Core
  restart is the only place a missing required module is fatal.

## Not verified here

Room features: the Mongo write of the `Features` field through the persistence worker was not run against a database; the in-memory fake
stores the same `$set` document.

MongoDB is not available in the sandbox. The Mongo-backed `MongoAdminQueries` and `MongoAuditStore` were not run against a database, and neither
was the persistence-worker path of a real room edit. Everything else is tested over in-memory fakes and over the real Kestrel host on a Unix
socket (`MUD.Tests/AdminApiTests.cs`, `AdminServiceHostTests.cs`).

## Open points for the owner

- Contract version: `ContractVersion.Current` stays 1. The note on that constant says to raise it with every `.proto` change, but the module
  tests use the literal 1 and `admin.proto` adds a service without touching the module wire. Raising it is a one-line change plus those tests.
- The poll is gone (card P6-5). A direct database edit made outside core is no longer noticed by a running core: a live room or player keeps
  its in-memory values until core restarts. This follows from D30 and from core being the only writer.
- No token file or `FrontEndHello` handshake guards `frontends.sock` yet. The socket is mode 0600 inside a 0700 directory, which is the same
  protection the module socket had before its token. The front-end token of D66 is card P7-1; the web card (P6-2) decides whether the web
  presents one earlier.
