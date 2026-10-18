# Web site (MUD.Web)

ASP.NET Core (Razor Pages, net8.0) site for logging in with the same character
accounts the telnet game uses. It does not start the World or the telnet
server and does not reference MUD.Core; it reads the `player` collection
directly through the Mongo driver.

## Configuration

| Setting | Source | Default |
|---|---|---|
| Mongo connection string | env `MUD_MONGO_URI` (required; the process exits with a message if unset; never logged) | none |
| Database name | config `WorldSettings:DatabaseName` | `testmud` |
| Listen URL | env `MUD_WEB_URL` | `http://127.0.0.1:5080` |
| Default room key | config `WorldSettings:StartRoomKey` (env `WorldSettings__StartRoomKey`) | none; when unset every room delete is refused |

Run: `MUD_MONGO_URI=... dotnet run --project MUD.Web`.

## Deployment

`scripts/deploy.sh` publishes the site into `web/` of each release and the systemd user unit
`planar-realms-web.service` runs it, reading `~/.config/mud/mongo.env` and the optional
`~/.config/mud/web.env` (`MUD_WEB_URL`). Default bind is `127.0.0.1:5080`. Install steps and
rollback behaviour are in [DEPLOYMENT.md](DEPLOYMENT.md).

## Routes

| Route | Access | Behaviour |
|---|---|---|
| `/` | anyone | redirects to `/character` if signed in, else `/login` |
| `/login` | anyone | name + password form, antiforgery protected |
| `/logout` | POST only | antiforgery protected sign-out; GET just redirects |
| `/character` | signed in | `Hello (<PlayerName>)!`, HTML-encoded |
| `/account/password` | signed in | change-password form (current, new, confirm), antiforgery protected; see below |

The nav shows a `/creator` link only when the `CreatorOrAdmin` policy passes.

## Creator page (room creation)

`/creator` is protected by the `CreatorOrAdmin` policy (admin implies creator). Anonymous users are
redirected to `/login`; signed-in players get a 403 page at `/access-denied` (no redirect loop).

- The form has fields named after `specs/ROOM_FILES.md`: `Key` (local room key), `Short`, `Long`,
  `Area` (area prefix), optional `X`/`Y`/`Z` (blank = unplaced, stored as null, never 0), up to 4
  exit rows `Dir` + `To` (target room key) and a "save exits to rooms that do not exist" checkbox.
- The stored room key is `<Area>.<Key>`. POST validates server side with antiforgery, then calls
  `IRoomCreationService.Create`. `RoomCreationService` resolves the signed-in user's CURRENT stored role (not
  the cookie), validates with `MUD.Data.RoomRules` (the same rules the area importer uses) and inserts through
  `IRoomWriter.Create`: a `$setOnInsert` upsert that fails if the key exists, with empty `Items`, `Rev` 0 and an
  audit record. On success the browser goes to `/creator/rooms/{key}`. Failures show fixed messages only; no
  submitted value or exception text is echoed.

## Room edit page (`/creator/rooms/{key}`)

Implements `specs/WEB_EDITING.md` for rooms. Policy `CreatorOrAdmin`; reached by clicking a room on the map.

- Edits `Short`, `Long`, `Area`, `X`, `Y`, `Z` and the exit list. The key is read-only and comes from the route only
  (it is not a bound form field). Blank X/Y/Z is saved as null (unplaced), never 0. Exit rows are added and removed
  with buttons (server round trip, no JavaScript); reverse-exit creation is not included.
- Validation is `MUD.Data.RoomRules`, shared with the importer (`MUD.Core` consumes it too): key and area patterns,
  short 100 / long 4000 characters, no control characters, direction letters and spaces (max 32), no duplicate
  direction. Exits must point at an existing room; otherwise the page asks for the confirmation checkbox
  (dangling exit). At most 32 exit rows.
- `IRoomWriter.Save` builds a field-level update: `$set` of only the changed allow-listed fields
  (`ShortDescription`, `LongDescription`, `Area`, `X`, `Y`, `Z`, `Exits`) plus `WebUpdatedAt`, and `$inc` of `Rev`,
  conditional on the `Rev` the form loaded (hidden field). It never replaces the document and never contains
  `Items`, `Features`, `ExtendedProperties`, handles or any state.
- Conflict: if `Rev` changed, nothing is written; the page says the room was changed by someone else, keeps the
  user's input in the form and lists the current stored values beside it.
- Audit (collection `audit`, Admin-readable only; no reader page yet): audit-first. One row per changed field
  (`Old`, `New`, `RevBefore`, `RevAfter`, actor id/name/role, `Source: "web"`) is written with `Applied:false`
  BEFORE the change; if that write fails the edit is refused and nothing changes. After the change the rows
  are marked `Applied:true` (or `Rejected` if the conditional write lost a race).
- Delete: the page has a delete button. The default room (`WorldSettings:StartRoomKey`, read by the website
  from the same configuration) can never be deleted, and the delete is refused when the room has no usable exit
  and the default room does not exist. Exits in other rooms that point at the deleted room are removed, and
  those removals are audited. The game then moves everything inside (see below).
- Re-import caveat: `local_modified` is NOT built (owner decision). A later area re-import can overwrite web
  edits of an imported room. The audit rows hold the previous values of every field, so a lost edit can be
  recovered from the audit log.

### Game side (MUD.Main)

`RoomWebSync` polls every 5 seconds: one query for the stored `Rev` of the live (cached) rooms, on a pool thread,
then applies the result on the game thread. A room whose `Rev` differs gets the web allow-list applied
(`Room.ApplyWebEdit`: Short, Long, Area, X, Y, Z, Exits, then `RebuildExitCommands`; `Items`, occupants, features
and handles are untouched) and everyone in it is told "Something feels different...". A live room whose
document has disappeared is treated as deleted: its players, NPCs and floor items move to a random adjacent room
(an exit to a room that exists and is not itself), else to the default room, and that room is told "A sudden
draft of magical wind deposits <list> in the room!". If neither exists nothing moves. `Room.Save` is now a field-level `$set`
that leaves `Rev` and `WebUpdatedAt` alone (it still inserts a room that was never stored), so an in-game save
cannot roll a web edit back. Not done here (separate card): in-game Terraform/Build and the importer bumping `Rev`.

### Not run

No MongoDB in the build sandbox: the Mongo store (`MongoRoomEditStore`, `MongoAuditLog`, `Repository.GetRevisions`),
the conditional `Rev` filter on a missing field, `$setOnInsert` upsert behaviour and the real poll have not been
exercised against a mongod. Everything else is covered by fakes.

## Room map (`/creator/map`)

Read-only map of the `room` collection, protected by `CreatorOrAdmin` (anonymous -> `/login`, plain
player -> `/access-denied`). Rooms come from `IRoomReader` (`MongoRoomReader` in production, a fake in
tests); only definition fields are read (`ShortDescription`, `Area`, `X`, `Y`, `Z`, `Exits`). Nothing is
written, and the page does not reference MUD.Core. Query string: `?area=<prefix>&z=<level>` (defaults to
the first area and level 0), driven by the Area and Level selectors.

- Layout lives in `RoomMapLayout` (pure; returns rects, polylines with arrowhead flags, stubs, badges,
  mismatches and the unplaced list). The page renders it as inline SVG, no JavaScript or external requests.
- Orientation per ROOM_FILES.md section 10: +x east, +y north, +z up. NORTH IS UP: screen y = -Y.
- Each placed room (X and Y set, Z defaults to level 0) is a square showing its short description and key.
  Clicking it goes to `/creator/rooms/{key}` (the room edit page, below).
- Compass exits (`north/n`, `northeast/ne`, `east/e`, `southeast/se`, `south/s`, `southwest/sw`,
  `west/w`, `northwest/nw`) are lines from the matching side or corner, with an arrowhead at the
  destination. An exit A->B plus a compass exit B->A in the opposite direction is one two-way line
  (arrowheads at both ends); otherwise it is a one-way line with one arrowhead.
- If the destination's coordinates disagree with the exit direction, the line is dashed in the warning
  colour and listed under the map as a mismatch. Coordinates never create movement; exits are the truth.
- `up`/`down` and non-compass exits (e.g. `enter hut`) cannot be placed on the grid: they appear as badges
  on the square (up/down arrow or the exit name) with the destination key.
- A compass exit to a missing room, another area, another level or an unplaced room is a short stub with
  the target key as a label (red when the room does not exist).
- Rooms in the area with null X or Y are listed in the Unplaced panel and are still clickable.
- The nav shows a `/creator/map` link under the same policy as the `/creator` link.

## Password change (`/account/password`)

- Anonymous users are redirected to `/login`. The nav shows a "Change password" link when signed in.
- POST (antiforgery) fields: `Current`, `New`, `Confirm`. Rules match the game's `chpwd`: 6 to 64
  characters, no control characters, new must equal confirm and differ from current
  (`PasswordRules` here, `PlayerAccounts.ValidatePassword` in MUD.Core; `PasswordRuleParityTests` keeps them equal).
- The current password is checked first, with the same constant-time compare as login. Wrong current shows one
  generic error and counts toward the login throttle (shared `LoginThrottle`, per IP, plus the 500 ms delay);
  a locked-out IP sees the same error.
- The new password is stored through `IAccountStore.UpdatePassword(nameKey, newPassword)`, a `$set` of
  `Password` only. Still plaintext (see the caveat below). The session stays signed in; a confirmation is shown.
- Game side: a player online in the game holds a cached `Player`. `Player.Save` and `chpwd` re-read the stored
  `Password` first and adopt it if it differs, so a later game save never writes back an older password.

## Auth model

- `IAccountService` (`Authenticate`, `GetByName`) lives in `MUD.Web/Services`, backed by
  `IAccountStore` (`MongoAccountStore` in production, fakes in tests), so it can be split out later.
- Lookup is an exact match on `NameKey = name.Trim().ToLowerInvariant()`, then the password is
  compared in constant time (both sides SHA-256 hashed, then `FixedTimeEquals`). Same rules as
  `PlayerAccounts.FindByName/Authenticate`.
- Role comes from the `_t` discriminators in `ExtendedProperties`: `AdminProperty` > `CreatorProperty` > Player.
- Cookie auth: `mud.auth`, HttpOnly, SameSite=Lax, Secure policy SameAsRequest (plain HTTP locally), 8h expiry.
  Claims: `Name` = PlayerName, `Role` = Player/Creator/Admin. The role is fixed at login time.
- Policy `CreatorOrAdmin` requires role Creator or Admin.
- Failed logins show one generic message. Throttling: 500 ms delay per failure and, per client IP,
  a lockout after 5 failures in 5 minutes (in memory; behind a proxy all clients share the proxy IP).

## Caveat: plaintext passwords

Player passwords are stored and compared in plaintext by the game today, and the web login
inherits that. Over plain HTTP the password also crosses the network unencrypted, so keep the
site bound to localhost until HTTPS/a reverse proxy exists. Password hashing needs a migration that
also changes the telnet game; it is out of scope here.

## Tests

`MUD.Web.Tests` (xunit) uses a fake `IAccountStore`; no Mongo needed. CI builds and tests
`MUD.sln`, which includes both projects.

## Seeding

`scripts/seed-admin.sh` creates (or repairs) an admin character in the local MongoDB so the same
login works for telnet and the web. Defaults: name `maurlorn`, password `moocow`.

    scripts/seed-admin.sh
    MUD_SEED_NAME=someone MUD_SEED_PASSWORD=secret scripts/seed-admin.sh

- Connection: `MUD_MONGO_URI` from the environment, else from `~/.config/mud/mongo.env`. It is never
  printed. Uses `mongosh` if installed, otherwise the legacy `mongo` shell.
- Other overrides: `MUD_SEED_DATABASE` (default `testmud`), `MUD_SEED_START_ROOM` (default
  `WorldSettings:StartRoomKey` from `MUD.Main/appsettings.json`; only used for a new character).
- Idempotent. If no player with that `NameKey` exists, a full `Player` document is inserted (string `_id`,
  `PlayerName`, `NameKey` = lowercase name, `Password`, `Race` human, `CurrentLocationId`, `ExtendedProperties`
  with `CreatorProperty` and `AdminProperty` and their `_t` discriminators). If it exists, only the password
  and any missing Creator/Admin property are ensured; nothing else is reset.
- It ensures the unique index `NameKey_unique` on `player` (same definition as the game's
  `CreateUniqueStringIndex`: unique, partial on string `NameKey`).
- MongoDB is schemaless: the `player` collection and that index are all the website needs. No other
  collections are required at this stage.
- The password is stored in plaintext because the game stores and compares plaintext today.
