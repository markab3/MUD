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
| A rule refused an edit (invalid field, editing an admin, default room) | status OK, reason in the reply's `result` |

Messages are fixed sentences. No token, password or stored value is put in a status or a log line.

## Calls and roles

| Call | Role |
|---|---|
| `WebAuthenticate`, `Logout` | none |
| `GetSession`, `GetOwnPlayer`, `ChangePassword` | any signed-in player |
| `ListRooms`, `GetRoom`, `CreateRoom`, `UpdateRoom`, `DeleteRoom`, `MoveRoom`, `ListModules` | creator or admin |
| `ListPlayers`, `GetPlayer`, `UpdatePlayer`, `ListAudit`, `StartModule`, `StopModule`, `RestartModule` | admin only |

Not in this card: `GetRoomMap` (the map is laid out by the web from `ListRooms`, as `RoomMapLayout` does today), `GetStats` and `Flush` (sketched in
CORE_API 7.2, not needed by any page). Each is a later addition if a page turns out to need it.

## GetOwnPlayer (website player page)

`GetOwnPlayer(TokenRequest) returns (OwnPlayerReply)`: read-only, any signed-in role. The player is the one behind the token; the request has no name, so a caller cannot ask for anyone else (`GetPlayer` is admin only). The reply: `name`, `role` (current), `short_description`, `long_description`, `race`, `gender`, `class`, `location` (current room key). It never carries a password, token, inventory or the retired flag. Token failures are `Unauthenticated` as for every call. Logic: `AdminApi.GetOwnPlayer`; tests in `AdminApiTests`.

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

## Room behaviors and search entries (read-only)

`RoomDetail` carries two read-only lists so the website's room page can show what is attached to a room. Logic: `AdminApi.RoomFrom` (live room and stored document).

- `behaviors = 11`: `RoomBehavior { hook, module, type, priority, config_json }`, one per `BehaviorRef` in the room's stored order. `config_json` is the entry's config as compact JSON text (no whitespace; string escaping is the .NET default, so `"` and non-ASCII appear as `\uXXXX`); `"{}"` when the config is empty or cannot be converted, never empty or null. `schemaVersion` is not exposed.
- `searches = 12`: `RoomSearchEntry { id, target, refresh_ms, reward_short, discovery }`, one per entry of the room's core `search` component in stored order (the sample hall's table search is a component, not a behavior entry). The reward item is exposed only as its short description (empty when there is no reward). `discovery = 5` (additive, `RoomSearchDiscovery { id, found, again }`) is the entry's per-player discovery (flag id and the two messages); it is unset when the entry has none, and a message that was not authored is an empty string (core then uses its default text). An entry may have a reward, a discovery or both. A client that does not know field 5 ignores it; a core that does not send it leaves it unset. The cooldown `nextRewardAt` is runtime state and not exposed.
- Filled by `GetRoom` and `ListRooms`, from the live room when it is loaded and from the stored document otherwise. Authorisation is unchanged (creator or admin).
- **UpdateRoom never touches them.** `RoomEditRequest` has no behaviors or searches field, so a client cannot send them; core diffs only the editable fields, so existing behaviors and search entries survive any edit, and no audit row mentions them (regression test `UpdateRoomIgnoresSentBehaviorsAndSearchesAndKeepsTheStoredOnes`). Replies to updates carry the unchanged lists.
- **Contract version.** The additions are new optional fields, so old and new callers interoperate on the wire; `ContractVersion.Current` stays 1, as it did for `gen`, `template_id` and the earlier additive fields (see the open point below).

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

## Terrain library (`List/Get/Create/Update/Delete` of fragments, feature templates and neighbour phrases)

Backend of the terrain library editor (specs/TERRAIN_GENERATORS.md section 6). Logic: `MUD.Core/Administration/AdminApi.Library.cs`; storage seam
`ILibraryStore` (`MongoLibraryStore` in production, an in-memory fake in tests); the seed file and loader are in `MUD.Core/Generation/Terrains/LibrarySeed.cs`.

**Deviation from TERRAIN_GENERATORS.md section 10.** That section sketched one `ListTerrainLibrary` plus `Save*` (with a `create` flag) and a single
`DeleteLibraryEntity`. This card exposes the explicit set the card asked for instead: for each of `Fragment`, `FeatureTemplate` and `NeighborPhrase` the
rpcs `List<X>s`, `Get<X>`, `Create<X>`, `Update<X>`, `Delete<X>` (15 rpcs). `ResetTerrainLibrary`, the map rpcs and the regen/update rpcs are not part of this card.

- **Roles.** All fifteen calls: creator or admin (token checked per call, the role re-read; status codes as in the table above). Writes run on the game thread together with the token check, like every other mutation.
- **Reads** answer from the stored documents (`ListFragments` etc. also return `library_version`, ordered by id; `Get*` answers `found = false` for an unknown id). If the library store is not available they answer empty/not found; if the database throws, the call answers gRPC `Unavailable` (not an empty list).
- **`terrains` is a list of terrain ids. An empty list means the entity is used by no generator, never "any terrain"** (OD5). The API accepts and stores empty lists; the library snapshot generators read never returns such an entity (`InMemoryLibrary` filter, plus a second filter in `Terrain`).
- **Results** (in `LibraryEditReply.result`; nothing is audited for a refusal or a no-change):

  | result | when |
  |---|---|
  | `OK` | written; `library_version` is the new counter value; the entity as stored is in `fragment` / `template` / `phrase` |
  | `NO_CHANGE` | update where every field equals the stored one (no audit, no version bump) |
  | `INVALID` | field rules broken; `errors` lists `field` + fixed sentence (nothing echoed from the input) |
  | `NOT_FOUND` | update or delete of an unknown id (also when the entity vanished between the check and the write) |
  | `ALREADY_EXISTS` | create with a taken id (also when another writer created it between the check and the write) |
  | `REFUSED` | the library is not available (core started without a store, e.g. the database or the seed failed at start) |
  | `AUDIT_FAILED` | the audit rows could not be written; nothing was changed |
  | `FAILED` | the database write failed after the audit; the audit rows are marked rejected |

- **Field rules** (`LibraryValidation`): `id` required, letters/digits/`-`/`_`/`.`, at most 100; `weight >= 0`; `text` required (neighbour phrase: `{dir}` exactly once, `neighbor_terrain` required); fragment `slot` required; feature `short` and `long` required, `chance` in [0,1]; every terrain/handle/adjective/tag entry a non-empty string; no duplicate terrain id in one entity.
- **Feature template version.** Server-owned; the client value is ignored. Create sets 1. Update sets `stored + 1` when any of `short`, `long`, `room_line`, `detail`, `handles` or `adjectives` changed; edits to `weight`, `chance`, `terrains` and `tags` leave it. (This is the number `templateVersion` of generated features is compared with.)
- **Library version.** `terrain_meta.library.version` is incremented (`$inc`) once per successful create/update/delete. After a write the cached snapshot (`StoreLibrary`) is refreshed so the next generator run sees the edit.
- **Audit first.** One audit row per changed field for an update (`entity` `terrain_fragment`, `terrain_feature` or `terrain_neighbor_phrase`; `entity_id` the id; field = the document field name such as `text` or `terrains`; old and new values). Create writes one row, field `*create*`, new = the whole document; delete one row, field `*delete*`, old = the whole document. Rows are inserted before the write and marked applied (or rejected) after. No row, no write.
- **Collections** (Mongo, written by core only): `terrain_fragments`, `terrain_features`, `terrain_neighbor_phrases` (`_id` = entity id, plus `updatedAt`, `updatedBy`; multikey index on `terrains`) and `terrain_meta` (`_id: "library"`, `version`, `seededAt`).
- **Seeding.** On core start, if `terrain_meta` has no `library` document, the embedded `terrain-library.yaml` (source: `MUD.Core/Generation/Library/terrain-library.yaml`) is validated and loaded; the meta document is written last. If the meta document exists the file is not read again, so admin edits survive restarts. A file that fails validation seeds nothing and is logged; the Admin service still starts (library calls then see an empty library). An unreachable database at start leaves the service without a library (calls answer `REFUSED`).
- **Web.** `IAdminClient` has the same fifteen calls (`ListFragmentsAsync`, `GetFragmentAsync`, `CreateFragmentAsync`, ...); `Get*` returns null for an unknown id; the reply result carries every branch above; exceptions as for rooms (`CoreRefusedException`, `CoreUnavailableException`). `FakeAdminClient` (MUD.Web.Tests) implements them with in-memory dictionaries and the same version rule.

## Not verified here

Terrain library: `MongoLibraryStore` (collections, the multikey indexes, `$inc` of the version, duplicate-key handling) and the real seed-on-start path in `World.CreateAdminApi` were not run against a database; the library tests use an in-memory `ILibraryStore`.

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

## Regen and update of generated rooms (`RegenerateMap`, `RegenerateRooms`, `UpdateRooms`)

Logic: `MUD.Core/Administration/AdminApi.Generation.cs` over `MUD.Core/Generation/GenerationService.cs` (shared with the creator commands `regen` and `update`). Design: specs/TERRAIN_GENERATORS.md section 9.

- **Roles.** Creator or admin; token re-checked per call; run on the game thread (so the repository write that refreshes live rooms is on the right thread).
- **Audit first.** One audit row per changed room (entity `room`, field `*regen*` or `*update*`, a short summary as the new value, not the old text), written before the room, marked applied or rejected after. If the rows cannot be written nothing is written (`AUDIT_FAILED`). `dry_run` writes and audits nothing.
- **Writes** go through the importer (`ReplaceGenerated` for regen; a features-only field write for update). The repository refreshes the cached live room from the stored one, so players in the room see the change at once.
- **Regen** replaces description, ALL features (hand-added too), properties and behaviors with the generator output; exits follow the map; items and occupants stay. Seed: the stored `gen.seed`, or `new_seed`. A room needs its map file (`<content>/maps/<gen.map>.map.yaml`) else `ROOM_GEN_MAP_NOT_FOUND`. A hand-authored room is `ROOM_GEN_NOT_GENERATED`; a room of another map at a map key is `ROOM_GEN_NOT_OWNED`.
- **Update** touches only features carrying a `templateId`: newer template -> rewritten (`FEATURE_UPDATED`); same -> `FEATURE_CURRENT`; template deleted -> `FEATURE_TEMPLATE_DELETED` (left as is); older template -> `FEATURE_NEWER_IN_ROOM`; a rename that would duplicate a short in the room -> `FEATURE_CONFLICT`. Description, provenance, exits, properties, behaviors and unreferenced features are never read for change.
- **Reply counts** `updated_count`, `unchanged_count`, `deleted_template_count`: for regen rooms replaced / unchanged; for update features updated / current / template deleted.
- `RoomDetail` gains `gen` (provenance) and `RoomFeature` gains `template_id`, `template_version` (read-only).
- **`ListTerrainColors(TokenRequest) -> TerrainColorsReply`** (creator or admin, read-only, no audit): legend colours for the website map view. One `TerrainColor {map, terrain, color}` per map file under `<content>/maps/*.map.yaml` and terrain whose legend entry has a valid `color: "#RRGGBB"` (upper case in the reply; the first colour of a terrain wins). The map name is the file name without `.map.yaml`, i.e. what `RoomGen.map` records. Read from the legend only (`MapGenerator.ReadLegendColors`), so a map with other errors still gives its colours; files that are not YAML maps give nothing. Empty when no content directory is configured. The colour never reaches room data.
- Not verified against a real MongoDB (none in the authoring sandbox); tests use in-memory fakes.
