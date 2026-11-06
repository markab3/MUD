# Web site (MUD.Web)

ASP.NET Core (Razor Pages, net8.0) site for logging in with the same character
accounts the telnet game uses. It does not start the World or the telnet
server and does not reference MUD.Core. It is a client of core's Admin service on `frontends.sock` and has no
database access: `MongoDB.Driver` is not referenced by `MUD.Web` and no Mongo setting is read (core is the only process
that talks to MongoDB).

## Configuration

| Setting | Source | Default |
|---|---|---|
| Listen URL | env `MUD_WEB_URL` | `http://127.0.0.1:5080` |
| Core socket | env `MUD_FRONTENDS_SOCKET` (full path), else `MUD_RUN_DIRECTORY`, else `$XDG_RUNTIME_DIR/mud`, else `~/.local/share/mud/run`, plus `frontends.sock` | see left |
| Default room key | no longer read by the web; core reads `WorldSettings:StartRoomKey` for the room delete rule | none; when unset core refuses every room delete |

Run: `dotnet run --project MUD.Web` (core must be running for any page that needs it).

## Deployment

`scripts/deploy.sh` publishes the site into `web/` of each release and the systemd user unit
`planar-realms-web.service` runs it, reading only the optional `~/.config/mud/web.env`
(`MUD_WEB_URL`, `MUD_FRONTENDS_SOCKET`); the unit no longer reads `mongo.env`. Default bind is `127.0.0.1:5080`. Install steps and
rollback behaviour are in [DEPLOYMENT.md](DEPLOYMENT.md).

## Routes

| Route | Access | Behaviour |
|---|---|---|
| `/` | anyone | redirects to `/character` if signed in, else `/login` |
| `/login` | anyone | name + password form, antiforgery protected |
| `/logout` | POST only | antiforgery protected sign-out; GET just redirects |
| `/character` | signed in | `Hello (<PlayerName>)!`, HTML-encoded |
| `/account/password` | signed in | change-password form (current, new, confirm), antiforgery protected; see below |
| `/creator/players` | `CreatorOrAdmin` | player list (see "Player pages") |
| `/creator/players/{nameKey}` | `CreatorOrAdmin` | player edit page (see "Player pages") |

The nav shows `/creator`, `/creator/map` and `/creator/players` links only when the `CreatorOrAdmin` policy passes.

## Creator page (room creation)

`/creator` is protected by the `CreatorOrAdmin` policy (admin implies creator). Anonymous users are
redirected to `/login`; signed-in players get a 403 page at `/access-denied` (no redirect loop).

- The form has fields named after `specs/ROOM_FILES.md`: `Key` (local room key), `Short`, `Long`,
  `Area` (area prefix), optional `X`/`Y`/`Z` (blank = unplaced, stored as null, never 0), up to 4
  exit rows `Dir` + `To` (target room key) and a "save exits to rooms that do not exist" checkbox.
- The stored room key is `<Area>.<Key>`. POST validates server side with antiforgery, then calls
  `IRoomCreationService.CreateAsync`. `RoomCreationService` checks the local key and area prefix with
  `MUD.Data.RoomRules`, then sends `CreateRoom` on the Admin service with the session token from the cookie. Core
  re-checks the caller's current role, validates the whole room with the same rules the area importer uses, audits it
  and inserts it; it never overwrites an existing room (`ALREADY_EXISTS`). On success the browser goes to
  `/creator/rooms/{key}`. Failures show fixed messages only; no submitted value or exception text is echoed. When core
  cannot be reached the page says so.

## Room edit page (`/creator/rooms/{key}`)

Implements `specs/WEB_EDITING.md` for rooms. Policy `CreatorOrAdmin`; reached by clicking a room on the map.

- Edits `Short`, `Long`, `Area`, `X`, `Y`, `Z` and the exit list. The key is read-only and comes from the route only
  (it is not a bound form field). Blank X/Y/Z is saved as null (unplaced), never 0. Exit rows are added and removed
  with buttons (server round trip, no JavaScript); reverse-exit creation is not included.
- Validation is `MUD.Data.RoomRules`, shared with the importer (`MUD.Core` consumes it too): key and area patterns,
  short 100 / long 4000 characters, no control characters, direction letters and spaces (max 32), no duplicate
  direction. Exits must point at an existing room; otherwise the page asks for the confirmation checkbox
  (dangling exit). At most 32 exit rows.
- Save (card P6-3, `specs/ADMIN_API.md`): the page loads the room with `GetRoom` and saves with `UpdateRoom`, sending the
  whole form and the session token. Core re-checks the caller's current role, validates, diffs the form against the
  live room, writes one audit row per changed field (audit first) and applies the change to the live room on the game
  thread. The reply carries the room as it is now, which the page shows. The web holds no `Rev`, no allow-list and no
  Mongo access for rooms.
- Last write wins (D30): there is no revision, no hidden `Rev` field and no conflict page. Two edits in a row both
  apply; the audit rows hold the previous value of every changed field.
- Core's answers map to fixed page messages: `OK` (saved), `NO_CHANGE`, `INVALID` (core's field errors are shown and the
  user's input stays in the form), `NOT_FOUND` (404 page), `AUDIT_FAILED`, `FORBIDDEN`, anything else a generic
  failure. A token core no longer accepts, or a role that was lowered since the request was authorised, shows the
  "not allowed" message (403 page for a read). When core cannot be reached the page says so (503 for a read).
- Delete: the page has a delete button (`DeleteRoom`). Core refuses to delete the default room
  (`WorldSettings:StartRoomKey`, which core reads from its own configuration; the web no longer reads it) and refuses when
  the room has no usable exit and the default room does not exist; the page shows core's fixed sentence. Exits in other
  rooms that point at the deleted room are removed by core and audited, and core moves everything inside the room.
- Re-import caveat: `local_modified` is NOT built (owner decision). A later area re-import can overwrite web
  edits of an imported room. The audit rows hold the previous values of every field, so a lost edit can be
  recovered from the audit log.

### Game side

Room edits made on this page change the live room in core at once (`AdminApi`). There is no poll: `RoomWebSync`,
`Room.ApplyWebEdit` and `Room.Rev` were removed in card P6-5, and so was the stored `Rev` read (D30).

### Not run

No MongoDB in the build sandbox: nothing here was run against a mongod, and no live browser session against a running
core was done. The pages are tested over a fake `IAdminClient`, the real client over a Unix socket against a stand-in
Admin service, and (in `MUD.Tests`, card P6-1) core's `AdminApi` over in-memory fakes.

## Player pages (`/creator/players`, `/creator/players/{nameKey}`)

Implements `specs/WEB_EDITING.md` section 15 for players. Policy `CreatorOrAdmin` (anonymous -> `/login`, plain player
-> `/access-denied`). The web cannot create, delete or rename players. Both pages call core's Admin service (`ListPlayers`,
`GetPlayer`, `UpdatePlayer`, see `specs/ADMIN_API.md`) with the cookie token; the web reads and writes no player or audit data itself.

- List: name, race, class, gender, current location, role (Player/Creator/Admin), online and status (Active/Retired).
  Sortable by name (A-Z / Z-A), case-insensitive substring filter on the name (cut to 64 characters here), 50 per page; core filters,
  sorts and pages. "Online" always reads `unknown`: the list reply carries no online flag.
- Edit: `ShortDescription`, `LongDescription`, `Race`, `Gender`, `Class` (free text, max 32 characters, no control
  characters, no fixed vocabulary; descriptions follow the room rules), `Retired`, and the role. `CurrentLocationId` is NOT
  editable (owner decision Q12). The name key comes from the route only. There is no password, name, inventory or
  equipment field in the contract's player messages or on the page model.
- Authority is core's: it re-reads the editor's CURRENT role on every call (D31). You may edit only players whose role is
  strictly lower than yours: a Creator edits Players; an Admin edits Creators and Players; nobody edits an Admin through the web.
  Only an Admin may change roles (nobody grants a role above their own) or retire/un-retire another player. A last-Admin check
  refuses lowering the only Admin. The page only hides controls the editor may not use (`PlayerRules`); core re-checks every change
  and answers with a result (`Ok`, `NoChange`, `Invalid` with field messages, `Forbidden`, `LastAdmin`, `AuditFailed`, `NotFound`, `Failed`).
- Last write wins (D30): no revision number, no conflict page. Core writes one audit row per changed field, audit first, and refuses
  the edit if the rows cannot be written (D34: web and creator edits are audited, module changes are not).
- Retirement: a retired character cannot log in on the web or on telnet. An Admin can retire any player of lower role and only an
  Admin can reverse it. Retiring ends that player's web sessions and an online player is told and disconnected by core.
  A player retiring themselves has no control yet, neither on the web nor in-game; that is a separate backlog card.
- Core down: the pages show a fixed "not reachable" message (503). A refused token or role shows a fixed "not allowed" message (403).

### Not run (players)

No MongoDB in the build sandbox: core's Mongo-backed player list, field update and audit store were not exercised against a mongod,
and no signed-in browser render against a running core was done. The pages are tested over a fake `IAdminClient`; core's rules over
in-memory fakes (`MUD.Tests/AdminApiTests.cs`).

## Room map (`/creator/map`)

Read-only map of the rooms, protected by `CreatorOrAdmin` (anonymous -> `/login`, plain
player -> `/access-denied`). Rooms come from `ListRooms` on the Admin service (via `IAdminClient`, with the session token),
mapped to `RoomRecord`; only definition fields are used (short description, area, `X`, `Y`, `Z`, exits). A coordinate
absent in the message is unplaced, never 0. Nothing is written, and the page does not reference MUD.Core. If core
cannot be reached the page shows a fixed message instead of a map; a token core refuses goes to `/access-denied`.
Query string: `?area=<prefix>&z=<level>` (defaults to the first area and level 0), driven by the Area and Level selectors.

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
  (`PlayerAccounts.ValidatePassword` in core; the web's duplicate `PasswordRules` and its parity test were removed).
- The change is made by core (`ChangePassword` on the Admin service, sent with the session token). Core checks the
  current password first. Wrong current shows one generic error and counts toward the page's own limit (`LoginThrottle`,
  per IP, plus the 500 ms delay); a locked-out IP sees the same error and core is not asked.
- Core stores the new password through the player. Still plaintext (see the caveat below). The session stays signed in; a
  confirmation is shown.
- Game side: a player online in the game holds a cached `Player`. `Player.Save` and `chpwd` re-read the stored
  `Password` first and adopt it if it differs, so a later game save never writes back an older password.

## Auth model

Accounts and sign-in go through core's Admin service on `frontends.sock` (card P6-2; contract
`contracts/mud/admin.proto`, behaviour in [ADMIN_API.md](ADMIN_API.md)). The web no longer reads the `player`
collection for sign-in and holds no password or role of its own.

- `IAdminClient` (`MUD.Web/Services/AdminClient.cs`; `GrpcAdminClient` in production, a fake in tests) wraps the
  calls the pages need: `WebAuthenticate`, `Logout`, `GetSession`, `ChangePassword`, and for rooms `ListRooms`,
  `GetRoom`, `CreateRoom`, `UpdateRoom`, `DeleteRoom`. Room calls throw `CoreRefusedException` when core answers
  Unauthenticated or PermissionDenied and `CoreUnavailableException` when core cannot answer. The socket is
  `$MUD_RUN_DIRECTORY/frontends.sock`, else `$XDG_RUNTIME_DIR/mud/frontends.sock`, else
  `~/.local/share/mud/run/frontends.sock` (the same rule as core); env `MUD_FRONTENDS_SOCKET` overrides the full path.
- Login posts name, password and the browser address to core. Core checks the password and the retirement flag
  and applies the login throttle shared with telnet (D43), then returns an opaque session token. The page
  shows one generic message for bad credentials and for a throttled address, the retired message for a retired
  character (only after the password was right), and a "not reachable" message when core cannot be asked.
- The cookie `mud.session` holds only that token: HttpOnly, SameSite=Lax, Secure when the request is HTTPS, no
  expiry (a browser-session cookie). Core ends the session on logout, retire, demote and restart (D31), so a
  stale cookie is simply anonymous and is removed.
- On every request `SessionAuthenticationHandler` asks core `GetSession` for the player name and CURRENT role and
  builds the claims from the answer (`Name`, `Role` = Player/Creator/Admin). A demotion or retirement therefore
  applies on the next request. If core cannot be reached the request is anonymous and the cookie is kept.
- `IAccountService.GetByName` answers from those claims for the signed-in player only; the room, map and player pages call
  core with the token instead.
- Policy `CreatorOrAdmin` requires role Creator or Admin.
- Throttling of sign-in is core's (D43). The password page keeps its own small per-IP limit
  (5 failures in 5 minutes, 500 ms delay; in memory) in the web until core throttles password changes too.
- No token or handshake guards `frontends.sock` yet (see ADMIN_API.md, open points; the front-end token is P7-1).
- No page shows game output yet, so the markup-to-HTML converter of D28 is not built in this card.

## Caveat: plaintext passwords

Player passwords are stored and compared in plaintext by the game today, and the web login
inherits that. Over plain HTTP the password also crosses the network unencrypted, so keep the
site bound to localhost until HTTPS/a reverse proxy exists. Password hashing needs a migration that
also changes the telnet game; it is out of scope here.

## Tests

`MUD.Web.Tests` (xunit) uses a fake `IAdminClient` for the account, room, map and player pages (plus tests of the real client
over a Unix socket); no Mongo needed, and a test checks that the web assembly references no MongoDB assembly. Not run here: a browser
sign-in against a live core (needs MongoDB). CI builds and tests `MUD.sln`, which includes both projects.

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
