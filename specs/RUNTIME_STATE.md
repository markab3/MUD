# Runtime state persistence and repository thread safety

Companion to ROOM_FILES.md (definition vs state fields).

## 1. Policy: write-through to the persistence worker, flushed on clean shutdown

Every mutation of persistent state is handed to the persistence worker
immediately by the code that makes it: the game thread takes a snapshot and
queues it (specs/GAME_LOOP.md section 8). There is no debounce timer. The
worker writes in order and retries failures; on a clean shutdown the queue is
flushed, and anything that cannot be written is spooled to disk and replayed at
next start. Writes are therefore "queued, flushed on clean shutdown": a
process that is killed (kill -9, power loss) can lose the few writes still
queued. (Heartbeat autosave of players every 10 minutes remains as a safety
net only.)

| Mutation                         | Where saved                                   | Write type                       |
|----------------------------------|-----------------------------------------------|----------------------------------|
| get / drop (room Items)          | GetCommand / DropCommand -> Room.SaveState()  | field-level $set of `Items` only |
| get / drop (player Inventory)    | GetCommand / DropCommand -> Player.Save(true) | field-level $set of game-owned player fields (first save of a new player writes the whole document); never the web-owned fields, see WEB.md "Player pages" |
| build / terraform (room edits)   | existing Room.Save() calls                    | whole-document upsert            |
| import                           | Repository.UpsertFields                       | field-level $set / $setOnInsert  |

New code that changes room `Items` (or any other state field) must call
`Room.SaveState()` right after the change. New state fields must be added to
`SaveState()`.

Ordering: get saves player then room; drop saves room then player. A crash
between the two writes can duplicate an item but never destroy one.

## 2. Definition vs state on write

`Room.SaveState()` writes only the state field (`Items`) with `$set`, via
`IRepository.UpdateFields`. It never rewrites definition fields, so a
stale in-memory copy cannot overwrite an import. If the room has never been
stored it falls back to `Save()` (upsert).

`Room.Save()` (used by Build/Terraform, which edit definition fields in game)
is still a whole-document replace. This is deliberate: in-game edits are
definition changes. It cannot clobber an import because
`UpsertFields` refreshes the cached live room (`RoomRepository.RefreshCached`
-> `Room.ApplyDefinition`) after every import, so the live copy that is saved
later already contains the imported definition. Items are state and are never
touched by import.

## 3. Repository thread safety

`Repository<T>` guards the model cache and every collection call with a
single lock (`_sync`):

- Insert / Update / UpdateFields / Delete / Get / GetAll / Search /
  GetRawDocument / GetIdsByField / UpsertFields all run under the lock.
- GetAll and Search materialise the cursor inside the lock (no lazy
  enumeration outside it) and map results onto the cached live instances, so a
  query never returns a second copy of an object that is already live.
- The lock is held across the Mongo round trip. This serialises DB access per
  repository; acceptable at this scale and keeps cache and DB consistent.
- The live model objects (Room, Player) are not locked. Game logic is expected
  to run on the command-queue thread; the telnet/IO threads only enqueue.

The cache stays unbounded and entries are never evicted except on Delete (or
by RefreshCached for repositories that don't override it). Rooms hold
occupants, so eviction would split a room into two objects. A bounded cache
needs an eviction policy that skips occupied rooms; out of scope here.

## 4. Verified / not verified

Unit tests (MUD.Tests/RuntimeStateTests.cs, fake repositories):
SaveState writes only Items and not definition fields; falls back to upsert
when never stored; drop persists room Items and player Inventory; get
persists both; a dropped item survives an importer-style definition update and
a reload from the stored document.

NOT tested: the Mongo-backed Repository itself (locking, UpdateFields against
a real server). No Mongo instance exists.
