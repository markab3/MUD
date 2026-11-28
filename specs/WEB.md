# Web site (MUD.Web)

> **Cutover done (card t_2375c78f):** the site is a Blazor Server UI (.NET 8, interactive server rendering, Fluent UI) and the Razor Pages are gone.
> Design and per-page notes are in [WEB_BLAZOR.md](WEB_BLAZOR.md). The URLs below are unchanged. Behaviour differences, stated: a missing room, a refused role
> and an unreachable core are in-page views with HTTP 200 (not 404/403/503); notices no longer survive a hard reload (they were TempData); `/` and
> `/login` are static pages, everything else runs in one SignalR circuit. Where the text below names `Pages/*.cshtml` or a `*Model`, read the
> component in `Components/Pages/*.razor` and the `*Presenter` in `Pages/`.

ASP.NET Core (Blazor Server, net8.0) site for logging in with the same character
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
| `/character` | signed in | the signed-in player's own details (name, role, race, gender, class, location, descriptions) from `GetOwnPlayer`; never another player, never a credential; core down shows the "not reachable" message |
| `/account/password` | signed in | change-password form (current, new, confirm), antiforgery protected; see below |
| `/creator/players` | `AdminOnly` | player list (see "Player pages") |
| `/creator/players/{nameKey}` | `AdminOnly` | player edit page (see "Player pages") |
| `/creator/modules` | list: `CreatorOrAdmin`; start/stop/restart buttons: admin only | module list with state (see "Modules page") |
| `/creator/terrain?area=&terrain=&tab=` | `CreatorOrAdmin` | terrain library by area and terrain type: dropdowns, terrain types, tabs, Delete (see "Terrain library pages") |
| `/creator/terrain/edit?kind=&id=&area=&terrain=` | `CreatorOrAdmin` | create or edit one library entity (see "Terrain library pages") |
| `/creator/areas` | `CreatorOrAdmin` | the areas hub: list, create, edit, delete, and what each area contains (see "Areas page") |
| `/creator/items` | `CreatorOrAdmin` | item templates grouped by area, filter, delete (see "Item templates pages") |
| `/creator/items/edit?id=&area=` | `CreatorOrAdmin` | create or edit one item template (see "Item templates pages") |

The pull-out menu has three labelled sections, in this order, and a section with no visible links is not rendered: **Player** (every signed-in user): Change password, Character; **Creator** (the `CreatorOrAdmin` policy): Areas, Create room (`/creator`), Items, Map, Modules, NPC templates, Terrain; **Admin** (admins only): Players. Within each section the links are sorted by visible label, case-insensitive ordinal, in code (`NavMenu.Sorted`), so a new link lands in order by itself; the sections keep the order Player, Creator, Admin. Log out is a POST form after the sections. The Players link is shown to admins only, and the player pages and their core rpcs are admin only too (policy `AdminOnly`), so a creator who opens `/creator/players` by URL gets the access-denied page.

## Create room page (`/creator`, room creation)

`/creator` is protected by the `CreatorOrAdmin` policy (admin implies creator). Anonymous users are
redirected to `/login`; signed-in players get a 403 page at `/access-denied` (no redirect loop).

- The form has fields named after `specs/ROOM_FILES.md`: `Key` (local room key), `Short`, `Long`,
  `Area` (area prefix), optional `X`/`Y`/`Z` (blank = unplaced, stored as null, never 0), up to 4
  exit rows `Dir` + `To` (target room key) and a "save exits to rooms that do not exist" checkbox. The Area field is a real
  `<select id="cr-area">` with a label, listing the area records (`ListAreas`, "prefix - Name"; none when core cannot answer) and shown BEFORE
  the room key. Picking an area shows its prefix as fixed text (`sample_area.`, `#cr-prefix`, read with the field through `aria-describedby`)
  in front of the local key input `#cr-key`, so the key prefix and the area cannot disagree. A free-typed area is no longer possible; new
  areas are made on the Areas page.
- The stored room key is `<Area>.<Key>`. POST validates server side with antiforgery, then calls
  `IRoomCreationService.CreateAsync`. `RoomCreationService` checks the local key and area prefix with
  `MUD.Data.RoomRules`, then sends `CreateRoom` on the Admin service with the session token from the cookie. Core
  re-checks the caller's current role, validates the whole room with the same rules the area importer uses, audits it
  and inserts it; it never overwrites an existing room (`ALREADY_EXISTS`). On success the browser goes to
  `/creator/rooms/{key}`. Failures show fixed messages only; no submitted value or exception text is echoed. When core
  cannot be reached the page says so.

## Room key autocomplete (card t_6241b5a2)

Every FULL room key input on the website (the exit target on Create room and Room edit, and the new room's key on Create room) is the shared `RoomKeyInput`:
type one character and a list of at most 8 matching rooms appears (match on any part of the key or on the short description, key-prefix matches first; each row shows key, short description and area). Arrow keys, Enter, Escape and a tap select; a typed full key plus Enter works without choosing. Exit targets must be existing rooms: "No room has this key." shows on leaving the field or pressing Enter (core still validates on save; the "Save exits to rooms that do not exist" checkbox is the explicit override). The Create key is a new key: matching rooms in the chosen area are only a warning, "A room with this key already exists." shows for a taken key, and nothing is forced. The Move room local key stays a plain input with the area prefix beside it. The list comes from `ListRooms`, loaded once per circuit and again when the page loads (no rpc per keystroke). Design and numbers: `specs/WEB_BLAZOR.md` Appendix H.

## Room edit page (`/creator/rooms/{key}`)

Implements `specs/WEB_EDITING.md` for rooms. Policy `CreatorOrAdmin`; reached by clicking a room on the map.

- Edits `Short`, `Long`, `X`, `Y`, `Z` and the exit list. `Area` is shown read-only: it must equal the key's prefix, and keys cannot be renamed. The key is read-only and comes from the route only
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
- Move: a second form on the page gives the room a new key (`<area prefix>.<local key>`; the area is a real `<select id="re-newarea">` of the area records, preselected to the room's current one, and its prefix is shown as fixed text `#re-newprefix` before the local key input `#re-newlocal`; the room's own area `#re-area` above is plain text) through the Admin
  `MoveRoom` call. Core inserts the room under the new key, deletes the old one, repoints exits in other rooms and players'
  stored locations, keeps occupants inside, and audits a `Key` change. Refused for the default room and for a key that
  already exists.
- Features: a Features fieldset below Exits (card "Website: features editor"). Per feature: short, look text (the `long` field,
  textarea, labelled "Look text (shown when a player looks at it)" with a helper line saying the room line is the separate sentence added to
  the room description; there is no Detail field any more, and an old saved draft that still has one is ignored), room line, handles, adjectives, plurals (each one text field, words separated by commas or spaces, stated on the page), and Move up,
  Move down and Remove buttons; Add feature adds an empty row. These are form round trips (handlers `AddFeature`, `RemoveFeature`,
  `MoveFeatureUp`, `MoveFeatureDown`, no write, no JavaScript); at most 64 rows. The same Save sends the whole list in the same `UpdateRoom`
  call with `features_present` true (an empty list removes all Features). Core's field errors (`Features[i].*`, duplicate short) are shown
  in the page's error summary, prefixed "Feature N:" (1-based). Behavior and component config is not in the contract: core keeps it for a
  feature whose short is unchanged (`specs/ADMIN_API.md`), so the page shows one general note that renaming or removing a feature drops it
  (it cannot mark individual rows, because `RoomFeature` does not say which have behavior).
- Link Adjacent Rooms (card "Website: 'Link Adjacent Rooms' button on the room edit page"): a button in the Exits section. A click
  ADDS exit rows to the form and writes nothing: no confirm, no new rpc, no `UpdateRoom`. The user reviews the rows (each is marked
  "New, not saved yet") and presses Save room; the normal `UpdateRoom` path validates and audits them then. The rule: rooms in the same
  area (null area = ""), placed, at the same Z (null = 0), one step away in one of the **8 compass directions** (north = +Y, east = +X, as
  `RoomMapLayout`), for each direction this room has no exit with in the current form (any spelling the map knows, so `n` blocks
  `north`; includes rows typed or added but not saved). **Up and down are intentionally excluded**, whatever rooms lie above or below. An
  existing row is never changed, whatever it points at. Two rooms on the same coordinates add nothing for that direction and show a
  warning naming them. One-way: neighbours are never edited. Rooms come from the `RoomDirectory` list the key inputs already use (no
  per-click rpc beyond a first read if it is not loaded). The status line ("Added N exits to the form: north -> key, ... Save to apply."
  or "No adjacent rooms to link.") is `aria-live`. Unsaved added rows are kept in the browser draft like any other exit edit and are lost
  on reload unless the draft is restored. The 32-row form limit applies; extra exits are reported, not added.
- Save and Back at the bottom (card "Website: room edit Save at the bottom ..."): the page ends with one action row (`.mud-bottom-actions`, no
  longer sticky): the primary `Save room` button (`#re-save`, full width, 44px, disabled while a write runs) and, directly below it, a
  full-width `Back to the map` link (`#re-back`). The form has `id="re-form"` and holds only Basics, Exits and Features; the Save button is
  outside it (after the Delete section) and joins it with `form="re-form"` (no script; Enter in a form text field still submits through it).
  Back goes to `WebRoutes.MapFor(<area>, <Z>, center: <room key>)` with the area and Z the room has as loaded or last saved (not unsaved form
  edits; a null Z is level 0, as `RoomMapLayout.LevelOf`); a room with no area goes to the bare map. Centering on the room is an addition so
  the user lands where they were. Leaving with unsaved changes just leaves (the browser draft keeps them). The link in the room-not-found view
  is unchanged (bare map).
- Save, then the map: when Save succeeds (including "Nothing was changed.") the draft is removed, the saved text is set as a notice and the
  page goes to the map of the room's saved area and level (the Z may have just changed), centred on it, as the Create room flow does but
  without the word "Created" (the map shows the notice once through `NoticeService`). A problem keeps the user on the page with the sections
  opened as before. Move, Delete, Regen and Update behave as before.
- The room's own area (`#re-area`) is plain text ("prefix - Name"), not an input. No text box chooses an existing area anywhere; the only
  free-text area field is the new area's prefix on the Areas page.
- Collapsible sections (card "Website: collapsible sections on the room edit page"): every section (Basics, Exits, Features, Behaviors,
  Generated room when present, Move room, Delete) is a native `<details>` whose `<summary>` holds the section's `<h2>` (44px target, chevron,
  focus ring, palette variables only; no script). Basics starts open, all others closed. Collapsing is display only: the fields stay in the page
  and the whole form is still sent on Save. A save, move, delete, regen or update that fails opens the section that holds the problem (by core's
  field name) and Basics, and the section of the action that failed.
- Behaviors (read-only, card "Website: list a room's behaviors"): a "Behaviors" section after the form, from `RoomDetail.behaviors` and
  `searches` (`specs/ADMIN_API.md`). Each room-level behavior in stored order is a card with hook, module, type, priority and a collapsed
  "Config" block with the config as indented JSON. A "Search" subsection appears only when the
  room has search entries. Each entry shows a one-line summary first (e.g. "Typing `search floor` records the discovery 'hidden_button' for the player (once per player)."), then id, target, refresh in readable units (10 minutes; the raw ms in the title attribute and a muted note), the Reward row only when there is an item reward (short description), and, when the entry has a discovery, its id, found message and already-found message ("(default text)" when empty). Read-only; search entries are not listed elsewhere (the room list has no natural place for them). Empty: "No behaviors attached." A core that sends neither (older) gives the empty state and the page works as
  before. A help line says behaviors are managed with the in-game `behavior` creator command (`behavior here list|add|remove`, see
  `BehaviorCommand`) or the `behaviors:` list of the area file. No controls: Save, Regen, Update, Move and Delete never send them
  (`RoomEditRequest` has no such field; core ignores them on update).
- Last write wins (D30): there is no revision, no hidden `Rev` field and no conflict page. Two edits in a row both
  apply; the audit rows hold the previous value of every changed field.
- Core's answers map to fixed page messages: `OK` (saved), `NO_CHANGE`, `INVALID` (core's field errors are shown and the
  user's input stays in the form), `NOT_FOUND` (404 page), `AUDIT_FAILED`, `FORBIDDEN`, anything else a generic
  failure. A token core no longer accepts, or a role that was lowered since the request was authorised, shows the
  "not allowed" message (403 page for a read). When core cannot be reached the page says so (503 for a read).
- Regen and Update (generated rooms only): when `RoomDetail.gen` is set the page shows a "Generated room" block (map and terrain) with
  two more POST forms, each with an antiforgery token (so the page has five forms: save, move, regen, update, delete; the last four have
  an `action`). Handlers `Regen` (`RegenerateRooms`) and `Update` (`UpdateRooms`), each for the route's one room key only; nothing is bound
  from the form, and provenance is read-only (`GenMap`, `GenTerrain` are not bound). Regen's form asks `confirm(...)` in the browser first
  (`RoomEditModel.RegenWarning`: it replaces description, ALL features including hand-added ones, and properties; hand edits are lost; exits
  and occupants stay). There is no lock. Update touches only features made from a library template whose template is newer, never the
  description. The result is a notice with core's counts: regen "N room replaced, M unchanged"; update "N features updated, M already current,
  K whose template was deleted (left as they are)". A room that is not generated shows a fixed error; `REFUSED` (no library), `AUDIT_FAILED`,
  `FORBIDDEN` and failures show fixed messages (core's text and errors are never echoed); core down is 503, a refused token 403. The page then
  reloads the room from core. The confirm is a browser nicety; core is the authority.
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

Implements `specs/WEB_EDITING.md` section 15 for players. Policy `AdminOnly` (`WebPolicies.AdminOnly`: authenticated and role claim Admin; anonymous -> `/login`, plain player
or creator -> `/access-denied`). Owner decision: creators have no access to the player pages or to the player rpcs. The web cannot create, delete or rename players. Both pages call core's Admin service (`ListPlayers`,
`GetPlayer`, `UpdatePlayer`, see `specs/ADMIN_API.md`) with the cookie token; the web reads and writes no player or audit data itself.

- List: name, race, class, gender, current location, role (Player/Creator/Admin), online and status (Active/Retired).
  Sortable by name (A-Z / Z-A), case-insensitive substring filter on the name (cut to 64 characters here), 50 per page; core filters,
  sorts and pages. The name filter is live (see WEB_BLAZOR.md "Live filters"): it applies as you type, one core call per settled value, and keeps the sort. "Online" always reads `unknown`: the list reply carries no online flag.
- Edit: `ShortDescription`, `LongDescription`, `Race`, `Gender`, `Class` (free text, max 32 characters, no control
  characters, no fixed vocabulary; descriptions follow the room rules), `Retired`, and the role. `CurrentLocationId` is NOT
  editable (owner decision Q12). The name key comes from the route only. There is no password, name, inventory or
  equipment field in the contract's player messages or on the page model.
- Authority is core's: it re-reads the editor's CURRENT role on every call (D31). You may edit only players whose role is
  strictly lower than yours; since the rpcs are admin only, in practice an Admin edits Creators and Players and nobody edits an Admin through the web.
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

Map of the rooms, protected by `CreatorOrAdmin` (anonymous -> `/login`, plain
player -> `/access-denied`). Rooms come from `ListRooms` on the Admin service (via `IAdminClient`, with the session token),
mapped to `RoomRecord`; only definition fields are used (short description, area, `X`, `Y`, `Z`, exits). A coordinate
absent in the message is unplaced, never 0. The page does not reference MUD.Core. **The page is no longer purely read-only:** the line
end clicks and the gray link arrow (below) write one exit through the audited `UpdateRoom`, so they need the same authorisation as editing
a room (`CreatorOrAdmin`) and run on the interactive circuit like the other edit pages (antiforgery and circuit rules unchanged). Every
other control on the map only navigates.

**Line ends** (`MapLine.Ends`, revised by the map-lines card; the black x of the earlier arrows card is retired). Each line between two
placed rooms has two ends, one at each room, and each end acts on the exit that leads TO the room at that end:
- End with an arrowhead (that exit exists): a click REMOVES it. Example: study (top) and hall (bottom) joined by a two-way line: clicking
  the arrowhead at the study end removes hall -> study and keeps study -> hall; the line then has an arrowhead only at the hall end.
- End without an arrowhead (that exit does not exist): a click ADDS it, from the other room, named by the direction from the other room to
  this one (the full-word opposite of the existing exit's direction). Clicking the plain end at the study restores hall -> study.
- Both removed: the line disappears and the gray arrows appear in that direction on both rooms.
- The two halves are separate click zones split at the midpoint (butt ends, so they never overlap); they sit under the arrows and room
  squares, which win any overlap. The zone is an invisible stroke at least 44 CSS px wide across the line at any zoom (`--map-hit-w`, set
  by `roommap.js`); along the line it is short when zoomed out, which is accepted. Each zone is `role="button"`, focusable, with
  `aria-label` "Remove exit <dir> from <short> to <short>" or "Add exit ...", a visible focus ring, Enter/Space. A drag, pan or pinch that
  starts on a zone never fires its click (`roommap.js`: 6px threshold, `dragMoved`, a click right after a drag is swallowed).
- Dashed warning (mismatch) lines have no zones: removing or adding there is ambiguous (use the edit page). Stubs are not removable
  from the map either (use the edit page).
- **Removal just removes**: no confirm dialog, no undo, and whatever other properties an exit may have in future go with it. The page shows
  the fixed notice "Removed exit <name> from <room> to <room>" (adds: "Added exit ..."). The audit rows core writes through `UpdateRoom`
  are the record; nothing else is added.
- Write path (`CreatorMapPresenter.ApplyAsync`, one path for add and remove, line ends and gray link arrows): `GetRoom` the room that OWNS
  the exit just before writing; remove exactly one exit (match on name AND target) or add exactly one; send the whole form through
  `UpdateRoom` so core diffs and audits only that exit. One exit per click; the room at the other end is never touched. Fixed messages: exit
  gone or room gone, or core `Refused`/`NotFound`/`NoChange` -> "The exit or the room changed in the meantime..."; add where the direction
  (under any alias) now has an exit -> "That direction is no longer free..." and nothing is overwritten; `Invalid` -> fixed text (core's text
  is not echoed); `Forbidden` or a refused session -> "You are not allowed to edit rooms."; `AuditFailed`; core unavailable. The map is then
  redrawn from core. While a write is in flight the page passes `Busy` to the map: zones and arrows are disabled (`aria-disabled`,
  `pointer-events: none`, dimmed), so a double tap cannot send two writes.

**Expansion arrows** (`MapArrow`, built by `RoomMapLayout`; palette variables only: black = `--mud-text`, gray = `--mud-muted`):
- Every room gets eight compass arrows (north, northeast, east, southeast, south, southwest, west, northwest) in the gap just outside its
  side or corner, shifted along the edge so the line or stub stays visible. They are SVG triangles (`RoomMapLayout.ArrowPath`, rotated per
  direction), as big as a line's arrowhead (`ArrowSize` = 16 SVG units across; the `#arrow` marker is derived from the same constant), not
  font glyphs. Each has an `aria-label` and a visible focus ring; the hit area is an invisible circle kept at least 44 CSS px across by
  `roommap.js` at any zoom.
- **No duplicate arrows**: a compass exit that is drawn as a line between two placed rooms gets NO arrow; the line's arrowheads are the
  only arrows for linked rooms (mismatch lines too).
- **Black arrow**: only for a stub exit (target off the grid, on another level or area, unplaced, or missing): the way to navigate there.
  It goes to the target's edit page when the target is on this level (or unplaced), else to the map of the target's area and level
  (`&center=<key>`); a target room that does not exist leaves the arrow unlinked.
- **Gray arrow, room there (link)**: no exit that way, and a placed room of the same area and level at the adjacent coordinates. A click
  ADDS one exit from this room to that room (direction name = the arrow's direction, same write path as the line ends); the other end is
  then a plain line end that makes it two-way. `aria-label` "Link <dir> to <short>"; still gray (only the label/tooltip differ).
- **Gray arrow, no room there**: links to `/creator?area=&x=&y=&z=` with the origin's area, X/Y plus the direction offset (north = +Y,
  east = +X) and the same Z. No exit is prefilled and nothing is linked back; nothing is created until the form is saved. `aria-label`
  "Add room <dir> of <short>".
- A room elsewhere (off the grid, other level or area) with a one-way exit into this room is a stub on its own map; nothing is offered on
  this side. (The old black x rule (b) is gone.)
- **Up and down** arrows sit inside the box (up top-left, down bottom-right; black when the room has such an exit), the same 16-unit triangles (up points up, down points down); the top edge of the up triangle may touch the first title line by about 2 units, nothing else is covered. They only switch the
  map level, never create or write an exit: black = level (and area) of the target room, gray = Z+1 / Z-1 of the same area. The link carries
  `gx`/`gy` (the origin X,Y); if the destination level has no room there, a dashed ghost square "No room here" with a "Create room here"
  link (prefilled area, X, Y, Z) is drawn; the ghost is display only. The Level dropdown includes a level reached this way even with no
  rooms. These replace the old up/down badges; named non-compass exits keep their badge.
- **After Create room** saves (every creation) the browser goes to `/creator/map?area=&z=&center=<key>&created=1`: the new room is centred
  by `roommap.js` and outlined until the next touch or key press, with the notice "Created <key>" and an "Edit room" link. A room without
  X and Y has no level in the link; the notice says it is not on the grid and is in the Unplaced list.
- Create room reads `area`, `x`, `y`, `z` from the query string (area only if it is an existing area). If core
cannot be reached the page shows a fixed message instead of a map; a token core refuses goes to `/access-denied`.
Query string: `?area=<prefix>&z=<level>` (defaults to the first area and level 0), driven by the Area and Level selectors. The Area select
(`#map-area`) lists the area records (`ListAreas`, "prefix - Name") plus any room area that has no record, so an area with no rooms yet
appears (its map is empty); when core cannot list the records the rooms alone give the list.

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
- Non-compass exits (e.g. `enter hut`) cannot be placed on the grid: they appear as badges
  on the square (the exit name) with the destination key. `up`/`down` are the arrows inside the square (above).
- A compass exit to a missing room, another area, another level or an unplaced room is a short stub with
  the target key as a label (red when the room does not exist).
- Rooms in the area with null X or Y are listed in the Unplaced panel and are still clickable.
- The nav shows a `/creator/map` link under the same policy as the `/creator` link.
- **Export YAML.** A button beside the area and level dropdowns (a plain link, so it works without JavaScript; 44px high, palette variables
  only; under the dropdowns on a phone, beside them from 600px). It downloads `<area>.yaml` for the selected area's WHOLE state (every level,
  every room including unplaced ones; rooms only, no floor items or creatures). Its tooltip shows the area's room count from the area record.
  Help line under it: "Exports every room of this area as an area file (rooms, exits, features, searches). Items on the floor and creatures
  are not included." (A search reward is part of a room's definition and is exported.) The link goes to the plain GET endpoint
  `/creator/map/export?area=<prefix>`: `CreatorOrAdmin` policy with the session cookie (anonymous -> login redirect, plain player -> 403), calls
  core's `ExportArea` (read-only, so no antiforgery), answers `text/yaml; charset=utf-8` with `Content-Disposition: attachment` and
  `Cache-Control: no-store`. The file name is the area prefix and nothing else: anything that does not match the area prefix grammar is refused
  before core is called. On exporter errors, an unknown or empty area, core unavailable or refused, nothing is downloaded: the endpoint redirects
  to `/creator/map?area=<prefix>&export=<kind>&errors=<n>&e=<line>...` and the page shows fixed text for the kind plus at most the first 5 error
  lines and the count. Generated-room notes are not shown after a download (the browser leaves the page as it is); the core note is in the reply.
- **Generated rooms are coloured by terrain.** A room with provenance (`RoomDetail.gen`: map and terrain) is filled with the `color` of its
  terrain from the legend of its map file (`MapRect` carries `GenMap`/`GenTerrain`; `CreatorMapModel.FillFor` picks the fill). The colours
  come from core's `ListTerrainColors` (creator or admin; reads `<content>/maps/*.map.yaml` legends through `MapGenerator.ReadLegendColors`;
  nothing is stored). They are asked for only when the level shows a generated room. A colour is used only when it is exactly `#RRGGBB`
  (the value ends up in an SVG attribute). Generated room with no colour for its terrain, or colours unavailable (core down, refused):
  neutral grey `#d6d3d1`; the map still renders. Rooms without provenance keep the plain `#f5f5f4` fill, so authored maps look as before.
  The terrain shows in the room's hover title. Colour is a hint, never the only signal: the terrain is also in the title text.

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

## Modules page (`/creator/modules`)

Built (`Components/Pages/Modules.razor`, `ModulesPresenter`). Policy `CreatorOrAdmin` (there is no `/admin` area). Its backend is in
`specs/ADMIN_API.md` ("Modules"); the web reaches it only through `IAdminClient` (`ListModulesAsync`, `StartModuleAsync`, `StopModuleAsync`,
`RestartModuleAsync`).

- Table columns: name (with "(folder missing)" when core says so), version ("unknown" if empty), status text (the run state in words),
  Running / Not running (words with a `[+]`/`[-]` prefix, never colour alone), Required / Optional, and core's last-failure text.
- Admins also get an Actions column: **Stop** when the module is running, **Start** when it is not, and **Restart** always. Required modules
  get all of them too; the Required column tells the admin which are required. Creators see the list only. The buttons are three
  POST forms per row (handlers `Start`, `Stop`, `Restart`), each with an antiforgery token and the module name in a hidden field. Core
  re-checks the role on every call, so a forged POST from a creator is refused (403 page).
- The calls only queue the request on core's supervisor. A handler stores core's message (or a fixed default) in `TempData["Notice"]` and
  redirects back to the list; the GET reads it null-safely. Restart's default notice says it starts a new instance before it stops the old
  one. Result branches: `OK`, `NO_CHANGE`, `NOT_FOUND`, `REFUSED` show the notice (core's wording for a refusal); `FORBIDDEN` and other
  results show a fixed message.
- Core unavailable shows the usual "game is not reachable" message with a 503; a refused session shows a 403 message.
- Manual refresh only (a Refresh link); no auto refresh, no JavaScript, no CDN. All text is Razor-encoded.
- Tests: `MUD.Web.Tests/ModulesPageTests.cs`, over `FakeAdminClient` (`AddModule`, `ModuleStarts`, `ModuleStops`, `ModuleRestarts`).

## Areas page (`/creator/areas`)

Built (`Components/Pages/AreaList.razor`, `AreaListPresenter`). Policy `CreatorOrAdmin`; Creator menu section, after Terrain. Everything goes
through `IAdminClient` (`ListAreas`, `CreateArea`, `UpdateArea`, `DeleteArea`; backend in `specs/ADMIN_API.md` "Areas"). An area is an
organisational label for the back end that usually lines up with a region of the game: prefix (its id), name, short description.

- **Hub**: the page lists what an area contains TODAY, as links with counts: rooms (`ListAreas` room count, links to `/creator/map?area=<prefix>`), terrain types
  (`ListTerrainTypes`, links to `/creator/terrain?area=<prefix>`) and item templates (`ListItemTemplates`, links to `/creator/items?area=<prefix>`, which lists only
  that area's group and has a "Show all areas" link). No new rpc: the three lists are already available. A count that could not be loaded shows "?" instead of a
  wrong 0. NPC templates (`ListNpcTemplates`) are a fourth link with a count; it goes to `/creator/npcs` (the placeholder list of every area; no area filter yet).
- **List**: one row per area (prefix, name, description, room count, the contents links), sorted by prefix, as a table from 600px and the same rows as stacked cards
  below (CSS shows one of them). Help text under the heading says the prefix is fixed once created and that renaming an area means moving its rooms.
- **New area / Edit**: one inline form (prefix typed for a new area, `#ar-id`; name `#ar-name`, required, 100 characters; short description
  `#ar-desc`, optional, 200 characters). On edit the prefix is read-only and the presenter writes the original id, so only name and description
  can change. Core validates and answers per field; the page shows core's messages and keeps the input.
- **Delete**: Delete asks first in `ConfirmDialog` ("Delete this area? Rooms must be moved out of it first. (prefix)"). Core refuses while rooms use
  the area; the page shows core's message, which names the count, and keeps the row.
- Palette variables only, 44px targets (shared `.mud-btn` / `.mud-field` rules), no horizontal scroll at 360px (cards under 600px).
- Tests: `MUD.Web.Tests/AreaWebTests.cs` (gRPC client over a socket, fake client, presenter branches, rendered pages).

## Item templates pages (`/creator/items`, `/creator/items/edit`)

Built (`Components/Pages/ItemTemplateList.razor`, `ItemTemplateEdit.razor`, `ItemTemplateListPresenter`, `ItemTemplateEditPresenter`). Policy
`CreatorOrAdmin`; Creator menu section, "Items", after Areas. Everything goes through `IAdminClient` (backend in `specs/ADMIN_API.md` "Item templates",
the entity in `specs/ITEM_TEMPLATES.md`).

- **List** (`/creator/items`): one native `<details>` per area, in prefix order, collapsed by default (open while a filter is applied). The summary
  (44px) shows the area name, its prefix and a count of the templates; inside are the area's description, one card per template (short, id, handles,
  version, Edit, Delete), and "New item in <prefix>" (opens the edit page with that area chosen). An area without templates is listed with
  count 0. A template whose area has no record gets a group named by its prefix. `#it-filter` is a live filter, applied as you type with no rpc (id, short, handles and adjectives,
  case-insensitive); areas with no match are hidden. Delete asks first in `ConfirmDialog`; core's refusal message is shown and the row stays.
- **Edit** (`/creator/items/edit`): new: `#it-area` dropdown of the area records (required), then the fixed prefix (`#it-prefix`, `.mud-keyrow`) and
  the local key `#it-local`; the full id is built from the two. Existing: the id and area are read-only and the page shows the version and what
  saving does to it. Fields: short `#it-short`, long `#it-long`, handles / adjectives / plurals (one text box each, comma or space separated, a
  null-safe list on the wire), component data `#it-components` (a JSON object, optional) and a Behaviors section (native `<details>`, one fieldset per
  entry: hook with a datalist of the known hooks, module, type, priority, config JSON; Add and Remove buttons). JSON that does not parse is reported
  by the page and nothing is sent; every other rule is core's and its messages are shown as they come, with the input kept. Core owns the version.
  The terrain editor has no list input component (its handles and adjectives are plain text boxes), so the same comma-or-space text box is reused.
- **Used by** (`#it-refs`, an existing template only, read-only): a summary (`#it-refs-summary`, "N search rewards use this template; M hold an
  older version."), then one card per stored copy (`.mud-refs`): the room (linked to its edit page), the search entry id and the version copied,
  with "older than the template's version V" on a stale one. It explains that copies are made at import and that refreshing them is not available
  yet (re-import the area file). No button or input. If the read fails the section shows `#it-refs-error` and the rest of the page still works.
  Delete on the list shows core's refusal ("still used by N search rewards") and keeps the row.
- **Preview** (`#it-preview`, updates as you type): the short description, the long description, and "A player can type: ..." built from the handles
  alone and each adjective in front of each handle (at most 12, then "..."); with no handle it says a player cannot name the item.
- Palette variables only, 44px targets (shared rules), no horizontal scroll at 360px (cards, wrapping text, `min-width: 0`). `.mud-itemgroup*` and
  `.mud-code` and `.mud-refs` in `mobile.css`.
- Tests: `MUD.Web.Tests/ItemTemplateWebTests.cs` (gRPC client over a socket, fake client, both presenters, rendered pages). Not verified: the layout
  at 360px in a real browser.

## NPC templates page (`/creator/npcs`)

A placeholder (`Components/Pages/NpcTemplateList.razor`, `NpcTemplateListPresenter`). Policy `CreatorOrAdmin` (a plain player gets the access-denied page,
an anonymous visitor the login page); Creator menu section, "NPC templates", between Modules and Terrain (sorted by code). Entity, deliberate gaps and open
questions: `specs/NPC_TEMPLATES.md`; backend: `specs/ADMIN_API.md` "NPC templates".

- Heading "NPC templates"; the notice "NPC templates are not available yet. This page is a placeholder." (`#npc-notice`).
- Read-only list from `ListAreas` and `ListNpcTemplates`, so stored templates are visible: a count (`#npc-count`, "N NPC templates"), an empty state
  ("There are no NPC templates yet.") and one native `<details>` per area, in prefix order, collapsed, with the area name, prefix and count in the summary
  (44px), the area description, and one card per template (short, id, version). An area without templates says so; a template whose area has no record gets
  a group named by its prefix.
- NO create, edit or delete controls, no filter and no edit route: the rpcs exist and are tested; the editor is a later card.
- A load failure shows the fixed error (unavailable, forbidden, failed) and still shows the notice.
- Palette variables only (the shared `.mud-itemgroup*`, `.mud-cardlist`, `.mud-facts` rules), no horizontal scroll at 360px.
- Tests: `MUD.Web.Tests/NpcTemplateWebTests.cs`. Not verified: the layout at 360px in a real browser.

## Terrain library pages (`/creator/terrain`, `/creator/terrain/edit`)

Built (`Components/Pages/TerrainList.razor`, `TerrainEdit.razor`, `TerrainListPresenter`, `TerrainEditPresenter`, helpers in `Pages/TerrainLibrary.cs`). Policy `CreatorOrAdmin`. Everything goes through
`IAdminClient` (the Create/Update/Delete/List/Get calls for fragments, feature templates and neighbour phrases; backend in
`specs/ADMIN_API.md` and `specs/TERRAIN_GENERATORS.md` sections 6 and 11). Core validates every write and owns the feature version.

- **List** (`?area=&terrain=&tab=&q=&tag=`, the query string is the only state, so links and the Back button work): at the top an **Area** dropdown (`#tl-area`,
  native select, from `ListAreas`) and BENEATH it a **Terrain type** dropdown (`#tl-terrain`) that lists only the chosen area's terrain types (`ListTerrainTypes(area)`).
  Choosing another area drops the terrain, so the page picks the first terrain type of that area (or none); a terrain that is not in the chosen area is ignored.
  With no area chosen only the area dropdown is shown (the last choice is NOT remembered). Below, tabs Fragments / Feature templates / Neighbour phrases with a count
  each (links from 600px, one select below) show only the chosen terrain's entries (`ListFragments` etc. with the terrain filter), a table from 600px and stacked cards
  below, with live filters (`#tl-text` on id and the entry's own words, `#tl-tag` on tags; fragments and features only; no submit; the old free-text terrain filter is gone).
  "New fragment / feature template / neighbour phrase" links carry the area, terrain and tab to the edit page. Empty states: no areas, no area chosen,
  "No terrain types yet" (an area without terrain types, with the create form available), "No <kind> for this terrain type." per tab.
- **Terrain types** (same page, section "Terrain types of <area>"): New terrain type (id local part `#tt-local` typed, the area is the chosen one, so the id sent is `<area>.<local>`;
  name `#tt-name`; description `#tt-desc`), edit of the chosen terrain type (name and description only; the id is read-only and the presenter writes the chosen id), and Delete
  with `ConfirmDialog`. Core's refusal message (it names how many entries still use the type) is shown verbatim. The local-part box has a suggestion list of the legend template
  names of the area's map file that are not terrain types yet (`ListTerrainColors`, map name = area prefix; any failure just means no suggestions).
- **Edit/create**: `kind` (`fragment`, `feature`, `phrase`) and `id` come from the query string only; no id means create. The form never
  binds the kind or the stored id, so a form cannot redirect a write to another entity. The form shows an **Area** dropdown (`#te-area`) and beneath it the
  **Terrain type** dropdown (`#te-terrain`, required) of that area; for a new entry both are pre-filled from `?area=&terrain=` (the list page's choice), for an existing entry
  from the stored terrain. Changing the terrain moves the entry; choosing another area moves to its first terrain type and drops a phrase's neighbour that is not in the area.
  A phrase's **Neighbour terrain** (`#te-neighbor`) is a dropdown of the SAME area's terrain types. A stored terrain (or neighbour) whose terrain type no longer exists
  is shown as "(missing: <id>)"; saving is refused (nothing is sent) until a valid one is chosen. There is no multi-select, no empty-list warning and no "empty = unused" text.
  Lists (tags, handles, adjectives) are comma or space separated.
- Feature templates show the stored version and "saving a change ... raises the version from N to N+1" (core decides; weight, chance,
  terrain and tags edits do not bump it). The template form has one look-text field, `Long` (`#te-long`, labelled "Look text (shown when a player
  looks at it)"); the former Detail field is gone.
- **Preview**: a phrase is previewed with `{dir}` filled in ("north"); a feature shows its room line (or short). This is a text preview of the
  entered values only. **No sample-room render for a chosen terrain**: the admin API has no such call (a dry-run generate of one cell would need a
  new rpc), so it is not built.
- Result handling: `OK` redirects to the edit page with `TempData["Notice"] = "Saved."` (read null-safely); `NO_CHANGE`, `INVALID` (core's
  field errors in the validation summary, form values kept), `ALREADY_EXISTS`, `NOT_FOUND` (404), `REFUSED`, `AUDIT_FAILED`, `FORBIDDEN` and
  other failures show fixed sentences. Core unavailable is a 503 message and a refused session a 403 message.
- Tests: `MUD.Web.Tests/BlazorTerrainTests.cs` (presenters) and `TerrainHierarchyPageTests.cs` (rendered pages, the grep that no trace of the retired terrains multi-select remains), over `FakeAdminClient`.

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
  `PlayerName`, `NameKey` = lowercase name, `Password`, `Race` human, `CurrentLocationId`, the embedded
  `roles` document `{ creator: true, admin: true, currentDirectory: null }`, D45). If it exists, only the password
  and `roles.creator` / `roles.admin` are ensured (`$set`); nothing else is reset.
- It ensures the unique index `NameKey_unique` on `player` (same definition as the game's
  `CreateUniqueStringIndex`: unique, partial on string `NameKey`).
- MongoDB is schemaless: the `player` collection and that index are all the website needs. No other
  collections are required at this stage.
- The password is stored in plaintext because the game stores and compares plaintext today.
