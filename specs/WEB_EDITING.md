# Web Editing versus the Live Game (design spec)

> **SUPERSEDED IN PART by the V2 owner decisions (D30); see [ARCHITECTURE_V2.md](ARCHITECTURE_V2.md) section 3 and [OPEN_QUESTIONS_V2.md](OPEN_QUESTIONS_V2.md).**
> `Rev` (optimistic concurrency) is dropped: web and creator edits are last-write-wins and the per-field audit keeps history. The Rev/poll machinery (sections 6, 8, 9 and the matching parts of 13 and 14) is superseded and is removed in V2 phase 6.
> Allow-lists, roles, audit and the owner decisions of section 15 survive as rules enforced in core. Nothing else in this file was rewritten.

Status: APPROVED with owner decisions (section 15). Docs only; no code accompanies this spec.
Based on `hermes` at 599721a. `specs/GAME_LOOP.md` exists only on branch `gameloop-spec`
(not yet merged to `hermes`); where this spec relies on it, it says so (assumption A1).

## 1. Problem

`MUD.Web` (a separate process, no reference to `MUD.Core`) will edit rooms and players directly in
Mongo. `MUD.Main` holds live cached `Room` and `Player` objects in `Repository<T>._modelCache`
and writes them back. Four facts make a naive web editor lose data:

a. Whole-document writes from the cache. `Room.Save()` (MUD.Core/GameObjects/Room.cs) and
   `Player.Save()` (Player.cs) call `Repository.Update`, a whole-document replace/upsert. Any
   stale cached copy written back silently reverts web edits. `Room.SaveState()` is already
   field-level (`$set` of `Items` only) and is safe.
b. Imports avoid this by refreshing the cache in-process (`RoomRepository.RefreshCached`
   -> `Room.ApplyDefinition`; `IRepository.UpsertFields`). A separate web process cannot call that.
c. A player edited on the web while online is overwritten at their next save (autosave every 10
   minutes, write-through on get/drop, quit).
d. The database is a single `mongod`, not a replica set: no multi-document transactions.

Constraints from the owner: passwords stay plaintext (existing decision); the `Password` field is
never displayed, logged, audited or sent to a browser anywhere in this feature.

## 2. Status of decisions

The owner has not chosen an approach. Section 3 is the RECOMMENDED default. Section 12 lists open
questions; this spec does not answer them. Alternatives are summarised in section 11.

## 3. Recommended default (summary)

1. The web writes only field-level `$set` of an explicit allow-list (section 4), never
   `ReplaceOne`, never a field outside the list.
2. Every web write also `$inc`s a per-document `Rev` integer and sets `WebUpdatedAt`, and writes an
   audit record (section 7). The save is conditional on the `Rev` the editor loaded (section 8).
3. The game never writes web-owned fields back. Whole-document replaces either exclude web-owned
   fields (`$set` of everything else) or are converted to field-level writes (section 9).
4. The game polls for changed `Rev` values on a slow tick and applies the changes to live objects
   ON THE GAME THREAD (section 6). Definition fields only for rooms; allowed fields only for
   online players.
5. Mongo is accessed with single-document atomic updates only (section 1 d); no transaction is
   assumed anywhere.

Assumption A1: the game-loop spec (`gameloop-spec`) is accepted: one game thread owns all world
state, DB writes happen on a persistence worker, and a `WorldEvent` inbound item exists. If that
spec is rejected or changes, the requirement in this spec that survives is "apply web changes on
whichever thread currently executes commands, never from a timer or pool thread", because
`Room`/`Player` state is unlocked.

## 4. Allow-lists

Field names are the stored (Bson) names, which equal the C# property names today.

### 4.1 Room

Web-editable:
`ShortDescription` (spoken of as "Short"), `LongDescription` ("Long"), `Area`, `X`, `Y`, `Z`,
`Exits` (list of `{Name, DestinationId}`).

Never web-editable (state or bookkeeping): `_id`, `Items`, `Features`/`ExtendedProperties`
(not on the card's list; deferred, Q3), `Handles`/`Adjectives`/`Plurals` (deferred, Q3), runtime
property state, `Rev`/audit fields (written only by the web write path), `source`/`source_hash`
(importer bookkeeping, ROOM_FILES.md section 9). `Occupants` and `RoomCommandSource` are not
persisted.

Notes: `Short`/`Long` map to `ShortDescription`/`LongDescription` on the stored room. A room is
created by the existing creation path, not by this feature (Q4).

### 4.2 Player

Web-editable: `ShortDescription`, `LongDescription`, `Race`, `Gender`, `Class`,
`CurrentLocationId`, and roles (section 5).

Never web-editable: `Password`, `NameKey`, `PlayerName` (changing it breaks `NameKey`; Q5),
`Inventory`, `WornItems`, `HeldItems`, `_id`, `SelectedTerm`, any other `ExtendedProperties`
element (only the Creator/Admin elements are touched, and only per section 5).

`Password` additionally: excluded from every projection the web reads for editing (use an
explicit inclusion projection, not "everything except"), from audit records, from logs, from
exception messages, and from the model bound to any edit form. A test asserts this (section 14).

## 5. Roles

Roles are `CreatorProperty` / `AdminProperty` entries (with `_t` discriminators) in a player's
`ExtendedProperties` (see MUD.Web/Services/Account.cs and `seed-admin.sh`). Authority is taken
from the signed-in editor's CURRENT stored role at the time of each write, not the cookie role
(the cookie role is fixed at login for 8h, WEB.md); a revoked admin must lose power immediately.

Rules, enforced server-side in the web service layer:
- Only an Admin may grant or revoke Creator or Admin.
- A Creator may edit non-role fields of ordinary Players. Whether a Creator may edit another
  Creator is undecided (Q6). A Creator can never edit an Admin, in any field.
- An Admin may edit anyone, including other Admins. Revoking the LAST Admin is refused (otherwise
  the system has no one who can grant roles; recovery is `seed-admin.sh`).
- Self-edit: an editor may not change their own roles (prevents lock-out and self-escalation).
- Roles are written as a field-level update of `ExtendedProperties` using array operators on the
  discriminator (`$pull` / `$push` of the single role element), not by rewriting the whole array,
  so other properties on the player are untouched. Applied to a live player per section 6.3.
- Admin implies Creator for authorisation (WEB.md). Granting Admin does not need a separate Creator
  element; revoking Creator from an Admin is not meaningful and is refused (Q6).

## 6. Game-side refresh

### 6.1 Change detection

Both collections gain an integer `Rev` (absent = 0). The game polls on a slow tick (proposed every
5 s, config value; Q7) with one cheap query per collection:
`find({Rev > lastSeen})` projecting `_id`, `Rev` only, for ids that are live (cached rooms,
online players). `lastSeen` is tracked per id in memory, initialised from the document when the
entity is loaded. Polling by id list with `Rev != knownRev` avoids needing a global counter and
works with a single mongod. Change streams are not available (need a replica set) and are
rejected for now.

Why an incrementing integer rather than only `UpdatedAt`: clocks differ across processes and
timestamps tie; `Rev` is also the optimistic-lock token (section 8). `WebUpdatedAt` is for humans.

The poll query runs on the persistence/IO worker (A1: no DB on the game thread) and posts a
`WorldEvent(WebChanged, collection, id)` to the game thread.

### 6.2 Rooms

On the game thread, for a changed live room: read the raw document (`GetRawDocument`, which
bypasses the cache), build a `Room` with the definition fields, call the existing
`Room.ApplyDefinition(fresh)`-equivalent restricted to the web allow-list (`Short`, `Long`, `Area`,
`X`, `Y`, `Z`, `Exits`), and call `RebuildExitCommands()`. NEVER touch `Items`, `Occupants`,
`Features`, runtime state. Update `knownRev`. Players in the room stay in it. A room not in the
cache needs no action (it is loaded fresh on next `Get`). A room deleted or absent: log, ignore.

Exit change that points at a missing room: the web validates (section 10) so this should not
happen; the game still guards `MoveToRoom` as it does today.

### 6.3 Online players

For a changed online player, on the game thread, apply only the allow-listed fields from the raw
document: `ShortDescription`, `LongDescription`, `Race`, `Gender`, `Class`, roles, and
`CurrentLocationId` (below). Nothing else is read from the web write, so a stale `Inventory` in
the document can never matter: the game still owns it. Role changes take effect on the live
player's `ExtendedProperties` Creator/Admin elements (replace those elements; other properties are
untouched) and the player is told ("Your status has changed.").

Location change: `CurrentLocationId` is applied by performing a normal move on the game thread
(`Living.MoveToRoom`-style: leave announcement, enter announcement, look) rather than assigning the
id, so occupants lists stay consistent. If the destination does not exist at apply time, the change
is rejected, the stored value is NOT silently rewritten by the game, an error is logged, and the
player stays put (the web will keep showing the bad value; Q8: who repairs it). Moving a player who
is mid-action: the move is taken at the next command boundary on the game thread. Moving an
offline player is just a stored-field write; no game action.

Players who are online and edited receive a short notice for visible changes (descriptions/race);
exact text is an implementation detail.

### 6.4 Write-back rule (the central invariant)

After applying, the game's in-memory value equals the stored value for every web-owned field, so a
later whole-document write cannot revert it. Because that is a race (web edits between the game's
last poll and its next save), the stronger rule in section 9 is required as well: the game's
persistence does not write web-owned fields at all.

## 7. Audit record

Collection `audit` (new, append-only; the web has insert-only use; no update/delete code paths).
One record per changed field per save (so a rollback or review is per field):

    {
      _id: ObjectId,
      At: ISODate (UTC, server clock of the web process),
      ActorId: string,        // editor's player _id
      ActorName: string,      // editor's PlayerName at the time
      ActorRole: "Creator" | "Admin",
      Entity: "room" | "player",
      EntityId: string,       // _id of the target
      EntityName: string,     // PlayerName or room full key, for readability only
      Field: string,          // allow-list name; roles are Field "Role"
      Old: any | null,        // previous value
      New: any | null,        // new value
      RevBefore: int, RevAfter: int,
      Source: "web"
    }

Rules: `Password` is never a `Field` (not on the allow-list) and no code path may serialise a whole
player into `Old`/`New`; values are recorded per allow-listed field only. Long text values are
stored in full (rooms are authored content), capped by the same length limits as validation. Audit
write failure: the edit has already been applied (no transactions); the web logs the failure
(without field values), shows a warning, and a startup/health check lists edits lacking an audit
row by comparing `Rev` (Q9: fail-closed instead, i.e. write audit first and apply second?
Recommended: write the audit record FIRST with `Applied:false`, apply the `$set`, then mark
`Applied:true`; an `Applied:false` row older than a minute denotes a crashed or failed apply).
Retention is an open question (Q10).

## 8. Conflict handling (optimistic concurrency)

An edit form loads the document and carries its `Rev` as a hidden field. Save is one atomic
operation:

    updateOne({_id: id, Rev: <loaded>}, {$set: {<allow-listed fields>, WebUpdatedAt: now},
                                          $inc: {Rev: 1}})

`MatchedCount == 0` means someone else (another web editor, an importer, or an in-game admin tool
that bumps `Rev`) changed it, or it was deleted. The web does not retry or merge. It reloads the
current values and shows: "This <room/player> was changed by someone else while you were editing.
Your changes were not saved. The current values are shown; review and re-apply." with the user's
rejected input preserved beside the current values. A document with no `Rev` is treated as `Rev`
0 and the filter uses `{Rev: {$in: [0, null]}}`/missing-field match.

Only fields actually changed in the form are `$set` (and audited), so two editors changing
different fields still conflict on `Rev`; this is intentional and simple (Q11: per-field Rev?).

The game does NOT bump `Rev` for its own state writes (inventory, location from movement, items)
so ordinary play never causes web conflicts. In-game creator edits to definition fields
(Terraform setshort/setlong/link/unlink via `Room.Save`) SHOULD bump `Rev` and set the
locally-modified flag (section 9, migration step 6).

Game-originated location changes (player walks) are not web edits; if a web editor changed
`CurrentLocationId` of an online player a moment before, the game applies the web move at its next
poll (section 6.3) and the walking player's own write loses nothing because location is not
written by the game (section 9).

## 9. What the game writes (stops writing)

Web-owned fields are those on the allow-lists. The game must not overwrite them from a stale cache.

Rooms:
- `Room.SaveState()` stays as is (`Items` only).
- `Room.Save()` (whole replace) is used by Build/Terraform and as the `SaveState` fallback. Change
  it to: insert when absent; for an existing document `$set` only fields the game is entitled to
  write, or, for creator edits, the specific definition fields that edit changed, and bump `Rev`.
  Never replace the document.
Players:
- `Player.Save()` becomes an `UpdateFields` of game-owned fields only: `Inventory`/`WornItems`/
  `HeldItems`, `SelectedTerm`, `Password` (only when the owner of the account changes it in-game,
  existing `password` command), connection/last-seen type fields, `ExtendedProperties` other than
  the role elements, and `CurrentLocationId` ONLY when it is game-driven. The latter is
  contradictory with web ownership of the same field; see Q12 (recommended: both may write it; the
  game's write is conditional on the stored `Rev` equalling the game's `knownRev`, so a web move
  that the game has not yet applied is never overwritten; if it differs, skip the location field
  and let the next poll apply the web value).
- First save of a new player stays an insert.
- The set of fields to exclude lives in ONE place (a single list shared by the web allow-list
  test and the game's save), so the lists cannot drift.

Importer: `UpsertFields` already `$set`s only definition fields and refreshes live rooms
in-process. With a web editor, the importer MUST NOT silently clobber later web edits (section 10).

## 10. Interaction with the importer and locally-modified (ROOM_FILES.md section 9)

Existing rules stand: the file wins unless a field is flagged locally modified; interim rule 9.4
(warn/require `--force` when `source_hash` diverges) applies until the flag exists.

Web edits count as local edits: a successful web write to a room sets `local_modified` to include
the changed field names (`short`, `long`, `area`, `x`, `y`, `z`, `exits`; ROOM_FILES.md uses file
names, the web maps from stored names), adds nothing for state, and sets `source` per ROOM_FILES.md
section 13 only if the room has none (it does NOT take over a file-owned room's `source`; phase 1.7
collision check must keep working; Q13 confirms). Consequences:
- Default re-import (`--respect-local`) skips those fields and reports "skipped (locally
  modified)" with a diff. `--force`, `--force-field`, `--adopt` behave as in 9.2.
- Until the flag is implemented, section 9.4 is the only protection; the web should not be enabled
  for rooms of imported areas before then, OR must at minimum change `source_hash` handling so the
  importer's divergence warning fires (Q14: sequencing: web editing blocked on locally-modified?
  Recommended yes for room edits; player edits are independent of the importer).
- A web edit bumps `Rev`; the importer, which `$set`s definition fields, also bumps `Rev` when it
  writes a room, so a web form loaded before an import cannot overwrite the import blindly (it
  gets the conflict message in section 8).
- Export (ROOM_FILES.md 9.3 / the creator `export` command) reads current DB values, so web edits
  can be captured to YAML and the flag cleared by the importer's `--adopt`.
- The live-room refresh after an import is in-process today. If the importer ever runs in a
  separate process (CLI against the same database) it takes the same route as the web: bump `Rev`,
  and the game's poll (section 6) refreshes it. In-process `RefreshCached` remains for the
  creator command and keeps working unchanged.

### Validation parity

The web must apply the SAME validation as `AreaImporter`/`AreaParser`, not a second implementation.
Extract the pure validators (key grammar `LocalKeyPattern`/`PrefixPattern`, exit direction names,
duplicate exit direction in a room, destination exists, `Area` prefix grammar, coordinate integers,
text normalisation per ROOM_FILES.md section 11, length limits) into a shared assembly that both
MUD.Web and MUD.Core can reference (MUD.Web does not reference MUD.Core today; a new small project
or `MUD.Data` is the candidate; Q15). Specifically for web saves:
- Exit `DestinationId` must exist (checked against the DB at save, with the race that it can be
  deleted afterwards, as with the importer); no duplicate `Name`; names are valid direction words.
- The importer rejects authored keys that look like generated ObjectIds; the web never edits `_id`.
- Existing web-side limits (RoomCreation.cs: short 100, long 4000, direction 32 letters/spaces)
  are the starting point and must equal the importer's after extraction.
- Player text fields: same length rules as room descriptions; `Race`/`Gender`/`Class` are free
  text today (read via `Living` string properties and compared case-insensitively) so validation
  is length/charset only unless an owner-defined vocabulary exists (Q16).
- Client-side checks are conveniences; the server always revalidates.

## 11. Alternatives considered (for the owner)

1. Recommended: field-level web writes + Rev poll in the game (this spec). No new infrastructure;
   delay up to the poll interval; requires game save changes.
2. Command channel: the web inserts "edit requests" into a Mongo `commands` collection (or calls an
   HTTP/loopback endpoint in MUD.Main); the game applies them on its thread and is the only writer.
   Strongest consistency, instant in-game effect, one writer, but the game must be running for
   web edits to work, and it needs a new surface in MUD.Main (and loopback auth).
3. Web edits only when the world is stopped / offline entities only: trivial and safe, severely
   limited.
4. Change streams: rejected until the deployment is a replica set (Q17).
5. Evict cache on change instead of field apply: simpler but unsafe for rooms with players and
   items (live object identity matters).

## 12. Open questions (unanswered; do not assume)

Q1  Approve the recommended default (section 3) or choose alternative 2/3 (section 11)?
Q2  Is a poll delay of a few seconds acceptable for web edits to reach the game, and what interval?
Q3  Should Features, Handles/Adjectives/Plurals and ExtendedProperties config of rooms ever be
    web-editable? Not on the allow-list; need owner list and their element-merge rules.
Q4  Does the web create/delete rooms and players, or only edit existing ones (the Creator page is
    a stub today)? Deletion rules (occupied rooms, online players)?
Q5  Can PlayerName be changed (would change NameKey and the unique index)? Spec says no.
Q6  May Creators edit other Creators? May a role be granted to oneself? Is revoking Creator from an
    Admin allowed? Recommended in section 5; owner to confirm.
Q7  Poll interval and per-tick budget; is a 5 s delay fine?
Q8  Who repairs a player whose web-set location does not exist when applied?
Q9  Audit failure policy: fail-open with warning, or audit-first (recommended)?
Q10 Audit retention/size and who can read the audit log (Admin only?)
Q11 Conflict granularity: per document Rev (recommended) or per field?
Q12 `CurrentLocationId` is written by both game and web: confirm the conditional-write rule in
    section 9, or make location web-read-only while online.
Q13 Does a web edit of a file-owned room keep `source` as the file area (recommended)?
Q14 Is web room editing blocked until the locally-modified flag (ROOM_FILES.md section 9) exists?
Q15 Where does the shared validation library live (new project vs MUD.Data)?
Q16 Do Race/Gender/Class have an allowed vocabulary?
Q17 Is moving to a replica set (transactions, change streams) planned? It would simplify this.
Q18 Does an in-game edit (Terraform) also bump Rev and set local_modified in this work, or a
    separate card? (Recommended: separate migration step 6 below.)
Q19 HTTPS/session handling of the web admin UI is out of scope here but required before exposing
    edit pages beyond localhost (passwords are plaintext and cross the wire on HTTP).

## 13. Migration steps (game and web code that must change)

Each step is a separate card/PR; order matters where stated.

1. Add `Rev` (int, default 0) and `WebUpdatedAt` to `Room` and `Player` models; ensure
   `BsonIgnoreExtraElements`/defaults keep old documents loading. Add a single shared list of
   web-owned field names (rooms, players) next to the models.
2. Repository: add `UpdateFieldsIfRev(id, expectedRev, set)` (conditional `$set` + `$inc Rev`,
   returns matched), a projection read of `{_id, Rev}` for a set of ids, and a raw-document read
   (exists: `GetRawDocument`). All must not touch the cache.
3. Player persistence: change `Player.Save()` (and `PlayerRepository.Update` usage in `Quit`,
   autosave, write-through, `PromoteCommand`, `PlayerAccounts.TrySave`) from whole-document replace
   to game-owned-field `$set` (section 9), keeping first-insert behaviour and the `Saved/Failed/
   NameTaken` results. Includes conditional `CurrentLocationId` rule (Q12).
4. Room persistence: change `Room.Save()` to insert-or-field-level; keep `SaveState()`. Update
   Build/Terraform callers (they edit definition fields) to `$set` just the changed fields.
5. Game-side applier: on the game thread, room definition apply (allow-list only) and online player
   apply (fields, roles, location via real move) with notices (section 6). Depends on 1, 2, and on
   the game-loop WorldEvent (A1); if the game loop has not landed, apply from the command-queue
   thread and document it.
6. Poller: slow tick comparing `Rev` for live entities; posts WebChanged events. Seeds `knownRev`
   at load/login. Handles deleted/missing documents.
7. In-game creator edits and the importer bump `Rev`; Terraform/creator edits set `local_modified`
   (ROOM_FILES.md section 9 flag and importer `--respect-local`). Blocks web room editing (Q14).
8. Shared validation library extracted from AreaImporter/AreaParser and RoomCreation (section 10);
   importer and web both consume it; characterization tests first.
9. Web write services (MUD.Web): room and player edit services using `UpdateFieldsIfRev`, audit
   insert (audit-first, section 7), role authority checks (section 5), `Password` exclusion,
   conflict page (section 8). Replaces `StubRoomCreationService` only if Q4 says so.
10. Web pages/forms for rooms and players using the services; antiforgery, no `Password` in any
    model; HTTPS gate (Q19).
11. Docs: update WEB.md, RUNTIME_STATE.md (write rules), ROOM_FILES.md section 13, GAME_LOOP.md
    (poll event and persistence worker kinds) once approved.

## 14. Test strategy

No test in this spec was run; nothing was built. Mongo is not available in the worker sandbox, so
Mongo-dependent behaviour below must be verified by someone with a mongod before release.

Unit (no Mongo; fakes for `IRepository`/stores, as `MUD.Tests` and `MUD.Web.Tests` already do):
- Allow-list: setting any non-allow-listed field (`Password`, `NameKey`, `Inventory`, `WornItems`,
  `HeldItems`, `_id`, `Items`) via the web service is rejected and results in no write.
- The `$set` document built by the web service contains exactly the changed allow-listed fields,
  plus `WebUpdatedAt`, and `$inc Rev`; conditional filter contains the loaded `Rev`.
- Password: no web read projection includes it; audit rows never contain it; log capture during
  a full edit flow contains neither the stored nor the submitted value; edit model has no such
  property (reflection test).
- Conflict: fake store returns MatchedCount 0 -> error message shown, user input preserved, no
  audit "applied".
- Roles: matrix of editor role x target role x operation (grant/revoke/edit fields); Creator vs
  Admin refused; last-Admin revoke refused; self role-change refused; role read from store, not
  cookie.
- Validation parity: a table of valid/invalid inputs run through both the importer validator and
  the web validator returns identical verdicts (shared library makes this a single code path).
- Game save changes: `Player.Save()`/`Room.Save()` build a field-level update that excludes
  web-owned fields (assert the document passed to a fake repository), including stale-cache
  scenario: change a web-owned field in the fake store, save from the game, assert the stored
  value is unchanged.
- Applier: game-thread apply of room definition keeps `Items`, occupants; rebuilds exits; player
  location change performs a move with announcements; nonexistent destination leaves the player in
  place and writes nothing; role change updates live `ExtendedProperties` only for role elements.
- Poller: fake store with advancing `Rev` yields one event per change; unchanged `Rev` yields none;
  threading test asserts the applier runs on the game thread (reuses the game-loop test harness).
- Characterization tests, written BEFORE migration steps 3-4, pin current `Player.Save`/`Room.Save`
  behaviour (insert, update, fallback, return values) so the field-level conversion is a deliberate,
  reviewed change.

Integration (requires a real mongod; NOT runnable in the sandbox; needs a manual or CI-with-Mongo
run): concurrent web edit versus game save on one document; conditional `$set` with missing `Rev`;
`$pull`/`$push` of role elements by `_t`; audit-first ordering; index/projection behaviour.

Manual: web edit of a room with players in it appears in game within the poll interval and does
not disturb items; edit an online player's description/location; re-import over a web-edited room
shows "skipped (locally modified)".

## 15. Risks

- Missing a whole-document write path (grep `Update(` / `Save(` call sites during step 3/4; any new
  one reintroduces the stale-overwrite bug). Mitigation: shared field list plus the stale-cache test.
- Dual ownership of `CurrentLocationId` (Q12) is the subtlest case.
- No transactions: partial states (edit applied, audit missing) are handled by audit-first and the
  `Applied` flag; multi-room edits (adding a reverse exit) are two independent writes and can half
  apply; the web edits one room per save and says so.
- Poll delay means in-game players see old text for a few seconds; acceptable only if Q2 says so.
- Plaintext passwords remain; this feature never reads, shows, logs or audits them but they are
  still stored and still cross HTTP; see Q19.

## 15. Owner decisions (supersede sections 3 to 14 where different)

All open questions in section 12 are answered. Where a decision differs from the recommendation, this section wins.

Design
- Q1: approved: allow-list `$set`, per-document `Rev`, audit, and a game-side poll that applies changes on the game thread.
- Q2/Q7: poll interval 5 seconds; one per-tick budget for all changes.
- Q11: one `Rev` per document.
- Q17: no replica set planned.

Rooms
- Q3: Features, Handles/Adjectives/Plurals and ExtendedProperties are not web-editable.
- Q4: the web can create and delete rooms. The default room (StartRoomKey) can never be deleted.
- Deleting a room: before moving anything, verify the destination exists; if not, refuse the delete and move nothing. Everything inside (players, NPCs, floor items) moves to a RANDOM adjacent room (an exit whose destination exists and is not the deleted room); with none, to the default room. The destination is told: "A sudden draft of magical wind deposits <entity list> in the room!" Exits in remaining rooms that point at the deleted room are removed and recorded in the audit. An online player in a deleted room is moved the same way.
- Updating a room: the room is told "Something feels different..."
- Q13/Q14: no locally-modified flag in this work. A later area re-import may overwrite web edits; the audit log (per-field Old/New) is the recovery path. Q18: in-game edits and importer bumping `Rev` is a separate later card.
- Q15: shared validation code lives in MUD.Data.

Players
- Q5: no renames. The web cannot create or delete players.
- Q12: `CurrentLocationId` is NOT web-editable for now (removed from the player allow-list); the game writes it normally. Moving players is the in-game `trans` command (separate card). A login into a missing room, or a room deleted under an online player, moves the player (see above); login into a missing room goes to the default room with a flavourful message.
- Q16: Race, Gender, Class are free text with length limits.
- Retirement: a retired player cannot log in (telnet and web) and sees "This character has been retired and can no longer be played." An Admin can retire any player of lower role; a player can retire themselves; only an Admin can reverse a retirement. An Admin cannot retire another Admin. An online player who is retired is told and disconnected through the normal quit/save path.

Roles and authority (supersedes section 5)
- Roles are inclusive: Admin includes Creator. You cannot grant a role above your own. You may edit only players of strictly lower role (Creator edits Players; Admin edits Creators and Players; nobody edits an Admin through the web). Keep a defensive last-Admin check.

Audit
- Q9: audit-first: write the audit row (`Applied:false`) before applying; if that write fails, refuse the edit. Mark `Applied:true` afterwards.
- Q10: the audit log is readable by Admins only; no automatic retention limit.
