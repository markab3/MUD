# Architecture V2: core / front-end split, composition, process-per-module (design spec)

Status: REVISED to the owner's decisions D1..D39 (see [OPEN_QUESTIONS_V2.md](OPEN_QUESTIONS_V2.md)). Docs only. No code,
scaffolding, `.proto` files or csproj changes accompany it. Revision task: t_17b7802d. Original draft: t_866775c9 (PR #42).
Where the draft and the decisions differed, the decisions won and the draft text was removed or rewritten.

Companion files (read together):

| File | Contents |
|---|---|
| ARCHITECTURE_V2.md (this file) | Requirements, current-state inventory, superseded specs, target architecture, entity and behavior model, commands, front ends, persistence, security, observability, phase summary |
| [CORE_API.md](CORE_API.md) | Event model (Before/After, verdicts, limits), what modules and front ends call, gRPC over two Unix domain sockets (message sketches, .NET 8 notes), the list "Gaps found while revising" |
| [MODULES.md](MODULES.md) | Manifest, launch entries and grants, lifecycle, supervision, deployment on this host, worked examples A (desert) and B (temple), failure modes, testing |
| [V2_PHASES.md](V2_PHASES.md) | The seven-phase plan with deliverables, risks, sizes and the proposed implementation-card split |
| [OPEN_QUESTIONS_V2.md](OPEN_QUESTIONS_V2.md) | (file name kept for link stability; it is now the decisions record) D1..D39 with a pointer to the section implementing each, and the list of items still open or deferred |

Nothing in this design has been built or run. Where a statement about the current code is made, the file is cited
(paths relative to the repo root, on `hermes` at 9a83afc). Where a claim about a library, the OS or performance could not
be verified here, it is marked **(unverified)**.

## 0. How to read this document

The owner is learning IPC and event-driven design as part of this work, so the design prefers explicit, inspectable
mechanisms over frameworks: plain protobuf messages, one event stream per module you can log line by line, one game
thread, a hand-written supervisor. Decisions are numbered D1..D39 (the owner's numbering, in OPEN_QUESTIONS_V2.md).
Things the decisions leave open and that an implementer would have to guess are collected in CORE_API.md section 9,
"Gaps found while revising"; they are referenced here as G1..G31. Nothing in this document closes an item that
OPEN_QUESTIONS_V2.md lists as open or deferred; where a number is needed it is marked **(not set)** or shown as an unmeasured proposal.

## 1. Fixed requirements (owner) and confirmed assumptions

1. Core functionality and the telnet-specific interface are separated. Telnet is a front end, not part of core (D27).
2. Core handles the basic entities (room, player, ...). Core is responsible for ALL entities in the world and ALL
   persistence. It is the only process that talks to MongoDB.
3. Composition over inheritance: core provides basic object types; behaviours come from modules and attach to entities as
   behavior list entries (D5). Behaviours hook pre-defined events and follow a general contract.
4. Modules load and unload as needed. Each module is its own running process. Core starts them (D20).
5. IPC is gRPC over Unix domain sockets (D16, D22). No network service calls.
6. Prefer explicit, inspectable designs; explain trade-offs.

Confirmed assumptions: modules live in this repo as solution projects; every module in the launch entries always runs (no
area-header start trigger); non-required modules start in the background; the `required` flag lives in the launch entry
(core config), not in the manifest (owner decisions, "Confirmed assumptions").

Worked examples A (desert sandstorm) and B (temple lever and traps) are in MODULES.md sections 7 and 8.

## 2. Current-state inventory (grounded in `hermes`)

This section describes the code as it is today and is unchanged by the decisions except where a row is marked. Sections
2.1 to 2.8 below were verified by reading the files cited (draft task) and spot-checked again in this revision.

### 2.1 Projects

| Project | Role | Notes (file evidence) |
|---|---|---|
| `MUD.Main` | Host. `Program.cs` reads `MUD_MONGO_URI`, `MUD_BIND_ADDRESS`, `MUD_PORT`, builds `World`, runs startup import, `EnableWebSync()`, creates the telnet `Server` | `MUD.Main/Program.cs` (lines ~99-293). Also contains the pre-login glue (`LoginSession` wiring, `WorldLoginHandler`) |
| `MUD.Core` | Game logic: `World`, `GameLoop`, game objects, commands, repositories, formatting, YAML import/export, map generator | References `MUD.Telnet` and `MUD.Data` (`MUD.Core/MUD.Core.csproj`). Core therefore **depends on telnet types today** (see 2.6) |
| `MUD.Data` | Mongo plumbing: `Entity`, `Repository<T>`, class maps, `PersistenceWorker`, `IClock`, `RoomRules`, `PlayerFields` | `MUD.Data/*.cs`. Also holds code shared with the web (`RoomRules`, `PlayerFields`) |
| `MUD.Telnet` | Raw-socket telnet `Server`/`Client`, `OutboundQueue`, `LoginSession`, `LoginThrottle`, `TelnetLimits`, `ConnectionTracker` | `MUD.Telnet/*.cs`; no package dependencies. Contains the login state machine and brute-force throttle, not only wire protocol |
| `MUD.PlanarRealms` | Content assembly: one room subclass `TowerOfAellioc_1` (feature setup in constructor, its search lambda commented out) | `MUD.PlanarRealms/Planes/AstralCentrum/Rooms/TowerOfAellioc_1.cs`; loaded by name in `World.LoadTypes()` |
| `MUD.Web` | ASP.NET Core Razor Pages site (login, character, creator room create/edit/map, player list/edit, password change) | Talks to MongoDB directly: `MUD.Web/Program.cs` registers `IMongoClient`, `MongoAccountStore`, `MongoRoomReader`, `MongoRoomEditStore`, `MongoPlayerEditStore`, `MongoAuditLog`. References only `MUD.Data` |
| `MUD.Tests`, `MUD.Web.Tests` | xunit | 28 test files in `MUD.Tests`, 8 in `MUD.Web.Tests` |
| `scripts/` | `deploy.sh` (publishes Main, PlanarRealms, Web), `seed-admin.sh` (mongosh straight into the DB) | `scripts/deploy.sh:100-104`, `scripts/seed-admin.sh` |

Runtime: .NET 8 (`net8.0`; SDK 8.0.128 and runtime 8.0.28 under `/usr/share/dotnet` on this host). Two systemd user
units: `planar-realms.service` and `planar-realms-web.service`.

### 2.2 Object model today (inheritance plus ExtendedProperty)

```
MUD.Data.Entity            string Id (Mongo _id), Clone<T>()               MUD.Data/Entity.cs
 +-- GameObject (abstract) ShortDescription, LongDescription, Plurals,     MUD.Core/GameObjects/GameObject.cs
 |                         Adjectives, Handles, List<ExtendedProperty>,
 |                         matching (IsMatchForString / IsPluralMatchForString), Examine()
 |    +-- Living           Gender, Race, Class, CurrentLocationId, KnownCommands,
 |    |    |               Inventory/WornItems/HeldItems, NextActionAt, MoveToRoom,
 |    |    |               ResolveCommand, MatchObjects, DoHeartbeat      Living.cs
 |    |    +-- Player      PlayerName, NameKey, Password (plaintext), Rev, Retired,
 |    |    |               connection, command queue, prompt handler, Save/Quit/web sync   Player.cs
 |    |    +-- NPC         empty subclass (no AI)                          NPC.cs
 |    +-- Room             Area, X/Y/Z, Rev, Items, Features, Exits, Occupants (not persisted),
 |    |                    RoomCommandSource built from Exits, TellRoom/EnterRoom/ExitRoom, Save/SaveState,
 |    |                    ApplyDefinition, ApplyWebEdit                    Room.cs
 |    +-- InventoryItem    empty                                           InventoryItem.cs
 |    |    +-- Food, Wand  subclasses overriding Examine()                  Food.cs, Wand.cs (not registered: DI lines are commented out in Program.cs)
 |    +-- Feature          RoomLine, DetailDescription, virtual getters     Feature.cs
 +-- Exit (plain class)    Name, DestinationId                             Exit.cs
 +-- ExtendedProperty (abstract, an Entity) objectContext, InitializeProperty(), RemoveProperty()   Properties/ExtendedProperty.cs
      BleedingProperty (IHeartbeat) | AdminProperty | CreatorProperty | BasicSearchProperty (ISearchable)
```

How behaviour is attached today:

- **Inheritance**: `Living` -> `Player`/`NPC`; `GameObject` -> `Room`, `InventoryItem` -> `Food`/`Wand`. Subclass
  behaviour is by `virtual` overrides (`Examine`, `GetRoomLine`, ...). Mongo persists the class through the BSON
  discriminator, with class maps registered by `MongoDBClassMapManager` and objects constructed through DI
  (`World.LoadTypes()` registers every non-abstract `Entity` as a transient).
- **ExtendedProperty list**: a polymorphic list on every `GameObject`. It serves three different jobs at once:
  (a) **roles** (`AdminProperty`, `CreatorProperty`; the website reads them by `_t` discriminator, `Player.StoredRoles`);
  (b) **timed effects** (`BleedingProperty` implements `IHeartbeat`; `Living.DoHeartbeat` iterates
  `ExtendedProperties.OfType<IHeartbeat>()`); (c) **room behaviours** (`BasicSearchProperty`, `ISearchable`).
  `InitializeProperty()`/`RemoveProperty()` exist but nothing in the audited code calls them on add (**not fully
  verified**; grep found no caller outside the class bodies).
- `IHeartbeat` is only invoked for **players** (`World.doHeartbeat` iterates `Players`); NPCs and rooms never get a
  heartbeat today (`MUD.Core/World.cs:633`).
- State inside properties is private and unpersisted (`BasicSearchProperty._nextReward`, ROOM_FILES.md 7.4).

### 2.3 Commands

- Interface `ICommand` (keywords, category, help, `ParseCommand(Living, string) -> object[]`,
  `DoCommand(Living, object[])`) at `MUD.Core/Commands/ICommand.cs`; `ISensitiveInputCommand` marks commands whose
  arguments must be redacted (only `ChpwdCommand`).
- Discovery: Scrutor assembly scan registers every `ICommand` implementation as a singleton
  (`World.LoadTypes()`), then `World` builds `AllCommands`, `DefaultCommands`, `CreatorCommands`, `AdminCommands`
  `CommandSource`s by `CommandCategory`.
- Resolution (`Living.ResolveCommand`): room exit commands (`Room.RoomCommandSource`, built from `Exits` as
  `AnonymousCommand`s) -> the living's `KnownCommands` subset -> (if Admin property) Creator commands (note: the Admin
  branch checks `CreatorCommands`, not `AdminCommands`; `AdminCommands` is only used by the `commands` listing, so
  admin-category commands such as `promote` resolve only through `AllCommands` and `KnownCommands`; **existing
  behaviour that this design does not fix; recommended as a backlog item**) -> (if Creator property) Creator commands -> `DefaultCommands`.
  `CommandSource.GetCommandFromInput` is a prefix match over a dictionary, so the first matching keyword wins and
  ordering depends on dictionary enumeration.
- Execution: `Player.DataReceivedHandler` resolves and parses on the game thread, wraps in `CommandExecution`, and
  `CommandQueue.AddCommand` runs it immediately or queues it when `NextActionAt` is in the future
  (`MUD.Core/Commands/CommandQueue.cs`). Exceptions are caught per command (`CommandExecution.NotifyException`).
- Count: the card says 44 command classes. In the tree there are **40 files** under `Commands/{Player,Creator,Admin}`
  (21 + 17 + 2). 33 contain an `ICommand` implementation; 6 are **empty files** (0 bytes: `ShutdownCommand`, `DestCommand`,
  `EditCommand`, `LSCommand`, `ProtoCommand`, `RMCommand`) and 1 is a helper (`BuildFeatureEditor`). Plus
  `AnonymousCommand`. I could not reproduce 44; the migration table in section 9 uses the 33 real ones. Of those,
  `Wear`, `Remove`, `Wield`, `Lower` (and `ApplyStatus`) have empty `DoCommand` bodies (stubs).
- Output: commands call `Living.ReceiveMessage(string)`. `Player.ReceiveMessage` runs the message through the
  player's selected `ITerminalHandler` (`AnsiTerminal`, `GMudTerminal`, `DefaultTerminal`), which replaces
  `%^Name%^` markup tokens (`EFormatOptions`) with escape sequences, then `Connection.Send`. So **rendering happens
  inside core on the player object**, and `Player` holds an `IClientConnection` from `MUD.Telnet`.

### 2.4 Game loop (specs/GAME_LOOP.md, merged and implemented)

One game thread runs `GameLoop.Run` (`MUD.Core/GameLoop.cs`): `BlockingCollection<InboundItem>` with kinds `Line`,
`Connected`, `Disconnected`, `WorldEvent` (a `Post(Action)` escape hatch), 100 ms tick, heartbeat every 20 ticks,
max 100 inbound items per pass, missed ticks skipped. Socket threads only enqueue. Game code takes no locks
(owner decision 1). DB writes never run on the game thread; the game thread snapshots to a `PersistenceWorker`
(`MUD.Data/Persistence.cs`: `PersistRequest` with `Replace`/`SetFields`, retry, spool to
`~/.local/share/mud/spool` on shutdown failure, completion callbacks posted back as world events). Owner decisions Q1-Q16 are at the end of GAME_LOOP.md.
Reads may run synchronously on the game thread as an interim (Q5): `World.GetRoom` calls `IRoomRepository.Get`.
`IClock` (`MUD.Data/IClock.cs`) makes time deterministic for tests.

### 2.5 Persistence rules (specs/RUNTIME_STATE.md, GAME_LOOP.md 8)

Write-through to the worker immediately on mutation; flushed on clean shutdown; kill -9 can lose queued writes.
Definition vs state: `SaveState()` writes only `Items` via `$set`; `Save()` of a room is a whole-document replace
used by in-game build/terraform; import refreshes the cached live room through `RefreshCached`/`ApplyDefinition`.
Player saves write only game-owned fields (`PlayerFields.NotWrittenByGame` excludes web-owned fields and `Rev`).
`Repository<T>` has one lock per repository, held across the Mongo round trip; the model cache is never evicted.

### 2.6 Area YAML import (specs/ROOM_FILES.md)

Authored YAML (`MUD.Main/Content/*.yaml`) is parsed (`AreaParser`) and validated, then field-level upserted by key
(`AreaImporter`, `RoomRepositoryImportStore`). Room id = area prefix + "." + local key. Definition fields are
overwritten, state (`Items`, runtime property state) is never touched. Section 4.5 and 7 define `properties:
behavior: search` and a behavior registry resolving to `ExtendedProperty` subclasses; section 8 allows a `class:`
room subclass. `AreaExporter` / `export` and `MapGenerator` emit the same YAML.

### 2.7 Website (specs/WEB.md, WEB_EDITING.md)

`MUD.Web` is a separate process that authenticates against the `player` collection and edits `room` and `player`
documents directly with allow-listed `$set`, per-document `Rev` for optimistic concurrency, and an append-only
`audit` collection (audit-first: row with `Applied:false`, then apply, then mark). The game polls Mongo every 5 s for
`Rev` changes and applies them on the game thread (`MUD.Core/RoomWebSync.cs`, `PlayerWebSync.cs`;
`Player.ApplyWebEdit`, `Room.ApplyWebEdit`, `Player.SyncWebFieldsFromStore`). Passwords are plaintext
(WEB.md "Caveat").

### 2.8 Couplings that the split has to cut (all verified in code; "today" facts, several are removed by decisions D30, D32)

| Coupling | Where |
|---|---|
| `MUD.Core` project-references `MUD.Telnet` | `MUD.Core/MUD.Core.csproj` |
| `Player` owns an `IClientConnection`, calls `Connection.Send/Disconnect(true)`, `PromptChannel` | `Player.cs` |
| `World` keys sessions by `IClientConnection`, throttles per connection, does `DoLogin(name, password, IClientConnection)` | `World.cs` |
| Terminal rendering runs in core (`SelectedTerm`, `ITerminalHandler`) | `Player.ReceiveMessage` |
| Login flow (`LoginSession`, `LoginThrottle`) lives in `MUD.Telnet`, wired in `MUD.Main/Program.cs` | `MUD.Telnet/LoginSession.cs` |
| Website bypasses the game and writes Mongo | `MUD.Web/Services/Mongo*Store.cs` |
| Out-of-band tool writes Mongo | `scripts/seed-admin.sh` |
| Game polls Mongo to learn about website writes | `RoomWebSync`, `PlayerWebSync` |


## 3. Which existing specs this design supersedes, invalidates, or keeps

Banners have been added to ROOM_FILES.md and WEB_EDITING.md by this revision (the content below says what is obsolete);
README.md rows are updated.

| Spec | Status under V2 |
|---|---|
| WEB_EDITING.md sections 6 (game-side refresh/poll), 8 (Rev optimistic concurrency), 9 (what game stops writing), 13-14 (migration/tests for polling) | **Superseded** (D30). `Rev` is dropped; web and creator edits are last-write-wins, the per-field audit keeps history. `RoomWebSync`, `PlayerWebSync`, `Player.ApplyWebEdit/SyncWebFieldsFromStore`, `Room.ApplyWebEdit`, `PlayerFields.NotWrittenByGame`, `WithoutWebBookkeeping` go away in phase 6 |
| WEB_EDITING.md sections 4 (allow-lists), 5/15 (roles), 7 (audit), 10 (importer interaction, but see D39), 15 (owner decisions) | **Survive as rules**, enforced in core instead of the web (list below) |
| WEB.md | Routes, cookie auth and the plaintext caveat survive. "Talks to Mongo" is **invalidated**: MUD.Web becomes a core API client (D27, D31). `MongoAccountStore` et al. are removed in phase 6 |
| GAME_LOOP.md | **Survives and is extended.** Single game thread, queues, tick, `NextActionAt`, persistence worker, spool, logout ordering stay. The inbound `Line/Connected/Disconnected` kinds are generalised to front-end session items; `IClientConnection` is replaced by a session id; gRPC handlers become another producer of `WorldEvent`-style items |
| RUNTIME_STATE.md | **Survives** (write-through, definition vs state, repository lock). Its per-mutation table is updated when components land |
| ROOM_FILES.md | YAML format and identity survive as an **optional, manually triggered one-off import/export format** (D39). Sections 4.5 and 7 (properties / behavior registry resolving to `ExtendedProperty`) are replaced by behavior entries (section 5.4 below). Section 6 (re-import semantics) and section 9 (`local_modified`, `source_hash`, `--force/--adopt`) are **obsolete**. Section 8 (`class:` room subclass) is superseded by plain `room` kind plus behavior entries |
| ARCHITECTURE.md, API.md, SPEC.md, DEPLOYMENT.md, CONFIGURATION.md | Describe today's layout; updated phase by phase. DEPLOYMENT.md gains module launch entries and the grants file in phase 4 |
| DESIGN_PRINCIPLES.md | **Survives.** "Everything is persistent" is the constraint that shapes module state (section 5.6). Its YAML-content-pipeline principle is narrowed by D39: the database is the sole source of truth, YAML is an optional one-off format |

Rules that survive unchanged (and where they are enforced in V2):

- **Audit of web and creator edits**: per-field Old/New rows, audit-first (`Applied:false` before apply), Password never an
  audited field, Admin-readable only (WEB_EDITING 7 and 15 Q9/Q10). Core writes these rows (D34). **Module-made changes are
  not durably audited** (D34): logs carrying module, event, targets, trace id and cause id are the record (section 7.6).
- **Role strictness**: inclusive roles (Admin includes Creator), cannot grant above own role, edit only strictly lower roles,
  nobody edits an Admin via web, last-Admin check. Enforced in core's front-end API handlers. Core re-checks the player's current
  role on every web call (D31).
- **Room deletion behaviour**: default room (StartRoomKey) never deletable; verify destination first; contents to a random
  adjacent room else the default room; wind message; dangling exits removed and audited. Becomes a core operation `DeleteRoom`
  not available to modules.
- **Room update message**: "Something feels different..." Core emits it on creator/web edits; module-made changes use the
  module's own `message.send` text.
- **Retirement**: retired characters cannot log in; admin/self rules as in WEB_EDITING 15; retire/demote revokes the web session at once (D31).
- **Plaintext passwords for now** (WEB.md caveat; D31). Never logged or displayed.
- **Single game thread** (GAME_LOOP.md): all entity mutation happens on one thread inside core (D9).
- Persistence principle (DESIGN_PRINCIPLES 1): no gameplay state only in memory (D12, D13).

## 4. Target architecture

### 4.1 Processes

```
                          Internet / LAN                          localhost only
   telnet clients ---TCP:7680---+                         browser ---HTTP:5080---+
                                v                                                v
                      +-------------------+                          +-------------------+
                      |  telnet front end |                          |   web front end   |
                      |  (phase 1: project|                          |   (MUD.Web)       |
                      |   in core process;|                          |   Razor Pages     |
                      |   phase 7: own    |                          |   cookie holds    |
                      |   process)        |                          |   core session    |
                      +---------+---------+                          +---------+---------+
                                |  gRPC / UDS  frontends.sock                  |  gRPC / UDS frontends.sock
                                v                                              v
  +--------------------------------------------------------------------------------------------+
  |                                      CORE (MUD.Main)                                       |
  |   Kestrel: two UDS endpoints: frontends.sock (Session + Admin services)                    |
  |                               modules.sock   (Module service)                              |
  |      |  every request becomes an item for the game thread                                  |
  |      v                                                                                     |
  |   +-----------------------------  GAME THREAD (single)  --------------------------------+  |
  |   | World: rooms, players, NPCs, items, features, components, behavior lists            |  |
  |   | event processing (Before/After chains) | timers | tick | capability + quota checks  |  |
  |   +-----------------------------------------------------------------------------------+  |
  |   persistence worker --> MongoDB (ONLY process with a Mongo connection)                    |
  |   module supervisor (launch entries, grants, tokens, restart/backoff)   in-memory session table |
  +-------+------------------------------+-----------------------------+-----------------------+
          | modules.sock: module DIALS core: one server stream for events,                   |
          | unary calls back (SubmitVerdict, Emit, State, Timer, reads)                      |
          v                              v                             v
   +--------------+              +--------------+               +--------------+
   | module:      |              | module:      |               | module: ...  |
   | desert       |              | temple       |               |              |
   | own process  |              | own process  |               |              |
   +--------------+              +--------------+               +--------------+
        children of core (spawned and supervised by core; C# projects in this repo)
```

In phases 1 to 5 the telnet front end is a project inside the core process and calls the core API as a C# interface;
the `frontends.sock` endpoint only exists from phase 4 and is first used by the web (phase 6) and the telnet process (phase 7).

Responsibilities:

| Process | Owns | Does not do |
|---|---|---|
| core | All entities, components and behavior lists, the game thread, command resolution for built-ins, event processing, timers, capabilities, quotas, persistence (sole Mongo client), optional YAML import/export, module supervision, both API sockets, accounts, authentication and web session tokens, brute-force login throttle | Telnet protocol, terminal escape sequences, HTML, module-specific rules |
| telnet front end | TCP accept, telnet negotiation, line framing and input limits, connection caps, idle timeout, raw connection-rate throttling, markup-to-ANSI conversion, prompt echo-off, the prompted login dialogue | World state, command parsing beyond "this is a line", Mongo |
| web front end | HTTP, cookie session holding the core session token, pages, CSRF, markup-to-HTML conversion | Mongo. Calls the core API for every read and write |
| module | Its behavior code and in-memory working state; handler logic for events delivered to its behaviors; schema of its own components and state document | Mongo, other modules' data, sockets to anything but core, mutating entities other than by events |

What stays **in-process in core** (and why): the game thread and world model (single-writer rule), the persistence worker (it
needs snapshots from the game thread, GAME_LOOP.md 8), built-in commands inseparable from the world model (look, search, get,
drop, movement, say, tell, who, inventory, ... D2), account/authentication, creator/admin tooling, optional YAML import/export
and the supervisor. Splitting any of these into processes would add IPC on the hottest paths without a stated benefit.

### 4.2 Why one core process and not a "services" mesh

Everything that mutates the world must serialise on one thread (GAME_LOOP.md owner decision 1, kept; D9). A multi-process core
would need distributed transactions for no gain at this scale. The extension points are therefore modules (behavior) and
front ends (I/O). Both are clients of core over UDS.

### 4.3 In-process handlers (introduced in phase 3, permanent; D49)

Phase 3 builds the event bus before any IPC exists. During phase 3 the behaviors that exist (search, bleeding) run
in-process as C# handlers registered in the same (Module, Type) dictionary that phase 4 later fills with remote modules. The contract is the
same: a handler gets an immutable snapshot and returns Allow/Deny plus emitted events. The in-process registration is
not a throwaway: the owner decided (D49) that it stays permanently after phase 4 as a second adapter for core-shipped behaviors.
There is one contract and one `(Module, Type)` lookup with two adapters, and the same contract tests run against both. It is not for
example or third-party modules, and an in-process handler is not isolated and cannot be unloaded safely.

## 5. Entity and behavior model

### 5.1 Core object types (kinds)

Core keeps a small, closed set of entity **kinds**. A kind fixes the minimum components an entity must have and which
core-owned operations apply to it.

| Kind | Today | Required components | Notes |
|---|---|---|---|
| `room` | `Room` | `Description`, `Exits`, `Position` (area, X/Y/Z), `Contents` | Occupants derived from location, not stored. Carries behavior list |
| `player` | `Player` | `Description`, `Location`, `Container`, `Account`, `Roles`, `Body` | Carries behavior list. Account holds name, NameKey, password (plaintext for now), Retired, term preference |
| `npc` | `NPC` | `Description`, `Location`, `Container`, `Body` | No Account. Carries behavior list |
| `item` | `InventoryItem`, `Food`, `Wand` | `Description`, `Location` | Carries behavior list. `Food.Servings`, `Wand.CurrentCharges` become components |
| `feature` | `Feature` | `Description`, `FeatureText` | **Inert (D14): no behavior list.** Stays attached to a room (embedded or child entity: not decided, see G14) |

`Living` disappears as a type: "living" becomes a query ("has `Location` and `Body`"). `if (x is Living)` becomes `if (x.Has<Body>())`.
Behaviors attach to rooms, items and livings (player, npc) only (D14). A builder who wants a lever uses an immovable item
(MODULES.md section 8) or a room-level behavior; this is revisited later.

### 5.2 Components

A **component** is a named bag of data attached to an entity, one per name per entity.

Core-owned components (typed in C#, schema fixed by core, persisted by core):

| Component | Replaces |
|---|---|
| `Description` | `ShortDescription`, `LongDescription`, `Plurals`, `Adjectives`, `Handles` |
| `Location` | `Living.CurrentLocationId`, item containment |
| `Container` | `Inventory`, `WornItems`, `HeldItems`, `Room.Items` |
| `Exits` | `Room.Exits` |
| `Position` | `Area`, `X`, `Y`, `Z` |
| `Body` | `Gender`, `Race`, `Class`, `KnownCommands` |
| `Account` | `PlayerName`, `NameKey`, `Password`, `Retired`, `SelectedTerm` |
| `Roles` | `AdminProperty`, `CreatorProperty` (+ `CreatorProperty.CurrentDirectory` stays as creator data) |
| `FeatureText` | `Feature.RoomLine`, `Feature.DetailDescription` |

Module-owned components are schemaless to core, namespaced `<module>.<name>` (examples: `desert.storm` on a room,
`desert.sand_blind` on a player, `temple.trap` on a room, `temple.lever` on an item). Rules for who may write which component are in G13.

Typed versus schemaless: typed (C#) for core components, schemaless (BSON sub-document under `components.<module>.<name>`) for module
components, with an optional JSON Schema fragment in the manifest that core can use to validate writes. A fully schemaless
core would make rendering, matching and import impossible to reason about; a fully typed module system would force a core
release for every module field. (Recommendation carried over from the draft; the owner decisions do not contradict it. Whether
schema validation exists in the first cut is not decided; see G13.)

### 5.3 Events are generic, string-named records (D4)

Everything that changes the world, and everything a behavior reacts to, is an **event**: a generic record with a string name
(`entity.move`, `item.move`, `command.entered`, `heartbeat`, `component.set`, `behavior.add`, `timer.schedule`, `state.set`, ...),
a **phase** field (`Before` or `After`, not separate types), subject entity ids, a payload, a cause chain and a trace id.
Full treatment in CORE_API.md sections 2 to 3. Two names fix the shape:

- `entity.move` covers exit and enter in one event with `from` and `to` roles. The parties are the mover and the two rooms.
  After the move, After notifications go to the mover and to both rooms.
- `item.move` carries a `Reason` (walk, drop, get, give, spawn, ...). The parties are the item, the living or container that holds
  it and the room.

### 5.4 Behaviors: the entity carries a list, core resolves at call time (D5, D6)

An entity holds **no references to module code**. It holds a list of `BehaviorRef` entries:

| Field | Meaning |
|---|---|
| `Hook` | Which hook the entry is for. Initial hooks (D3): heartbeat, command entered, living enters/exits room, item dropped, item taken |
| `Module` | String: the owning module's name |
| `Type` | String: the behavior's name inside that module (example: `DesertTempleTrap`) |
| `Priority` | Integer, informational only (D7: order is not significant) |
| `Config` | JSON, authored per entry (for example `{ "damage": 10, "pit_room": "temple.pit" }`) |
| `SchemaVersion` | Version of the Config shape; a module that cannot read it refuses the behavior (D10) |

How an event reaches code (all on the game thread):

1. The action (a move, a drop, a typed command, a heartbeat) creates a **Before** event.
2. Core gathers the **parties**: the entities involved (for `entity.move`: mover, from-room, to-room). Each party filters
   its own behavior list for the event's hook. An entity with no matching entry costs nothing (fast path, D9).
3. For every matching entry core resolves `(Module, Type)` in a dictionary to a handler: an in-process handler (phase 3) or a
   connected module (phase 4+). Hot swapping a module edits that dictionary only; entities are never scanned or rewritten (D5).
4. Each handler receives an **immutable snapshot** (value types) of the entities it needs plus its own `Config`, and returns a
   **verdict**: `ALLOW` or `DENY(message)`, each with optional **emitted events** (D6). There is no MODIFY. A redirect is
   `DENY` plus a new `entity.move` event, which runs its own chain.
5. Core waits for every handler or the single chain deadline (no short-circuit on the first Deny; D15). Any Deny vetoes (D6).
   Handler order is not significant and handlers may run in any order or in parallel (D7). If several Deny, the player sees
   the first Deny message to arrive (D7).
6. Events emitted by **all** handlers apply whether or not the action is denied (D8). Effects that must depend on the action
   committing belong in the **After** phase.
7. If not denied, core re-validates and commits, then runs the After phase: notifications to the parties.

What the events a behavior emits look like, and the exact rules for each, are in CORE_API.md. The phases of the worked examples
are in MODULES.md sections 7 and 8.

**Dormant behaviors (D10).** When a module is unloaded its entries stay on the entities. An unresolved `(Module, Type)` is
skipped under the hook's fail policy: flavor hooks fail open, rule hooks fail closed; a per-behavior override is bounded by a core
policy file. The skip is counted and logged once per interval. When the module returns, its behaviors resume from the stored
`Config`. A module that cannot read a `SchemaVersion` refuses that behavior and it counts as a miss. Which of the five initial
hooks are flavor and which are rule is not decided (G3).

**Temporary effects (D11).** There is no overlay, undo or TTL structure. An effect such as the sandstorm times itself out:
the module sets a component (and a state document entry), schedules its own timer with `timer.schedule`, and removes the
component idempotently when the timer fires. Core does not know which changes are temporary. Consequence, stated plainly: if
the module never returns, the component stays on the entity until someone removes it by hand (creator command or web
editor) or the module comes back.

### 5.5 Every event is hookable, and the limits that makes necessary (D9)

All world change is an event processed on the core game thread. **Every** event is hookable, including change events such as
`component.set` and `behavior.add`. This requires, and the numbers are **not set** (OPEN_QUESTIONS_V2.md "Still open"):

- a cascade depth limit,
- a per-tick event budget,
- a per-module emission quota,
- a re-entrancy guard: reject an event whose cause chain already contains the same behavior, entity and event name,
- a fast path: an entity with no matching behaviors costs nothing.

### 5.6 Where state lives (D12, D13)

Two places, split by ownership:

| Kind of state | Where | Why | Example |
|---|---|---|---|
| Affects core behavior, is player-visible, or another behavior must read it from a snapshot | A **component on the entity** (`<module>.<name>`) | Persisted with the entity, visible to snapshots and to builders | room component `desert.storm`, player component `desert.sand_blind`, `temple.trap` on a room, `temple.lever` on an item |
| Module-private scheduling detail that no entity or other behavior reads | The module's **module-state document**, a keyed document the module writes with `state.set` events and reads on start | Core persists it (sole DB writer); size limit and write-rate limit per module (numbers not set) | the storm schedule, next spawn time |

Startup reconciliation by the module must be idempotent (D13). Unloading a module never deletes component data or state
documents; purging orphaned module data is deferred (open item).

### 5.7 How this replaces inheritance and ExtendedProperty

| Today | V2 |
|---|---|
| `Living` : `Player`, `NPC` | kinds + components; `Living` = "has Location + Body" |
| `Room` subclass via `class:` | plain `room` kind + behavior entries; ROOM_FILES 8 `class:` is superseded (D39) |
| `InventoryItem` : `Food`, `Wand` | `item` kind + components (`food.servings`, `wand.charges`) and behavior entries |
| `AdminProperty`, `CreatorProperty` | `Roles` core component |
| `BleedingProperty` (heartbeat) | a behavior entry on the living: `Hook = heartbeat`, `Type = "Bleeding"` with a component for its remaining duration; timed effects time themselves out (D11) |
| `BasicSearchProperty` | search stays baked into core like look (D2); its per-room configuration (reward, cooldown) becomes a core component, so ROOM_FILES 7.4's "cooldowns reset on every boot" gap is fixed by moving `_nextReward` into component state. No extension slot is created for search |
| `IHeartbeat` | the `heartbeat` hook (CORE_API 2.3); see G8 for who receives it |
| `ISearchable`, `IExaminable`, `IUpdatable` | not generalised. `look` and `search` remain core built-ins (D2) |
| Virtual `Examine(examiner)` overrides | stay core; how module-set text reaches `look` is G9 |

Persisted-data migration: none (D1). No live data worth keeping (1 player, 3 rooms); rebuild from YAML, re-create the account;
no migration code and no dual-read.

## 6. Event model (summary; full catalogue in CORE_API.md section 2)

Events have two phases. **Before** is synchronous from the acting entity's point of view: the action is held, handlers run,
any Deny vetoes. **After** is a notification once the action has committed. The critical rule of the whole design, carried
over from the draft and kept by D15: **the game thread never blocks waiting for a module.** The acting entity is *parked*
while the chain runs, the loop carries on, replies arrive as inbound items, and a re-validation step precedes commit.
Details and cost: CORE_API.md 2.3 and 6.

## 7. Cross-cutting design

### 7.1 Commands

| Group | Home in V2 | Examples |
|---|---|---|
| Inseparable from world model | core built-ins | look, search, get, drop, inventory, say, tell, chat, who, status, help, commands, save, stop |
| Account/session | core built-ins (session verbs the front end forwards) | quit, chpwd (+ `term` as a front-end-local setting, 7.2) |
| Creator/admin tooling | core built-ins | build, create, clone, cd, navigate, terraform, trans, import, export, genmap, promote |
| Movement | core built-in generated from `Exits` (as today, `Room.RebuildExitCommands`), producing an `entity.move` event | north, n, ... |
| Behavior-handled verbs | a `command.entered` behavior on the living or the room (CORE_API 2.4, G7) | pull (lever) |

Raw line flow (CORE_API 6): front end sends session id + raw line; core, on the game thread, takes the prompt-handler shortcut
if any (passwords), else runs the `command.entered` Before chain for parties that carry such behaviors, else resolves the verb as
today (room exits -> known commands -> role commands -> defaults). Whether prefix matching (`CommandSource.GetCommandFromInput`) is
kept is not decided by the owner; the draft's explicit verb table is left as a recommendation for phase 2 and is **not** part of phase 1.

### 7.2 Output and rendering (D28)

Core output to front ends is the existing `%^Color%^` markup string plus a **channel tag** (say, tell, room, combat, system, chat; D44).
The telnet project converts markup to ANSI (the `ITerminalHandler`s move out of `MUD.Core/Formatting` into it); the web
needs a markup-to-HTML converter with correct escaping. Structured spans are deferred. Modules use the existing channels. The `term`
command becomes a front-end-local setting, with the stored preference still in `Account` (details in V2_PHASES.md phase 1).

### 7.3 Front ends (D27, D29)

Telnet becomes its own project behind a front-end-neutral core API, still in the core process first (phase 1); a separate
telnet process is phase 7. The web front end uses the same core API over gRPC, replacing `MUD.Web`'s direct Mongo access.

Session ownership: core owns the **player** and the **session record** (`session_id`, player, front end). A front end owns the
**connection**. A connection's identity towards core is the `session_id` core returned, never a socket handle. Link-dead grace
period is an open item (OPEN_QUESTIONS_V2.md): until decided, the current behavior stays (a lost connection logs the player out,
`World.handleInbound` Disconnected -> `Player.Quit`, `MUD.Core/World.cs`).

**Core restart (D29):** front ends close connections with a "world is restarting" message and clients reconnect. Characters are
saved on shutdown and the persistence worker is drained (as `ShutdownCoordinator` does today).

**Web identity (D31):** core issues an opaque session token at web authentication. The web keeps it (HttpOnly cookie suggested)
and sends it on every call; core re-checks the player's current role on every call and can revoke sessions (retire/demote is
effective immediately). The session table is in memory in core and is lost on restart. Passwords remain plaintext by earlier owner
decision; never log or display them.

### 7.4 Persistence and consistency

- Core is the sole writer and the sole Mongo client. The `MongoClient` in `MUD.Web` is removed in phase 6.
- Write path unchanged: game thread mutation -> snapshot -> `PersistenceWorker` (GAME_LOOP.md 8). A change made by a module is
  an event processed on the game thread and persisted exactly like a player-caused change.
- **No `Rev` (D30).** Web and creator edits are last-write-wins; the per-field audit keeps history. The Rev/poll machinery is removed.
- **Audit (D34).** Web and creator edits keep the existing per-field audit. Module-made changes are **not** durably audited;
  logs (module, event, targets, trace id, cause id) are the record.
- **Area YAML (D39).** The area YAML is an optional, manually triggered one-off import/export format. The database is the sole
  source of truth; the world is built ad hoc (creator tools, web editor) and then updated. No recurring sync. Import must be
  collision-safe (never silently overwrite). Behavior binding lives only in entity behavior lists. Consequence to resolve: the host currently imports on every start
  (`WorldSettings.ImportOnStartup` defaults to true, `MUD.Core/WorldSettings.cs:15`, `MUD.Main/appsettings.json:10`, `World.ImportContent`
  at `MUD.Core/World.cs:614`); that is a recurring sync and conflicts with D39 (G17).
- **No seed-admin, no core-admin CLI (D32).** `scripts/seed-admin.sh` was a one-off bootstrap and is not a maintained tool;
  the design adds no replacement. First-run seeding by core on an empty database is possible later and is deferred. Because core is the only DB writer,
  any other tool that writes Mongo is outside the design.
- **Backups: deferred (D33).**
- Consistency between core and module: core is authoritative; a module's view is a snapshot that may be stale; core
  re-validates before commit.

### 7.5 Security and trust model (D17, D18, D19)

Facts: all processes run as the same OS user on one host. Socket files are mode 0600 inside a 0700 directory
(`/run/user/1000/mud/`; `/run/user/1000` is `drwx------`, verified in the draft task).

- Fault isolation: a module crash, leak or loop cannot corrupt the game thread. Authority isolation by API: a module can do only what
  the API exposes, checked against its grants; it has no Mongo credentials (modules are spawned with a cleaned environment).
- **Authentication (D17):** random per-spawn token in the child's environment, presented on the first call together with the
  manifest and the contract version. It guards against accidents, not a hostile same-user process; no `SO_PEERCRED`. A dev tool must
  be able to issue a token for a module started by hand.
- **Capabilities (D18):** an owner-edited grants file (`~/.config/mud/module-grants.yaml`, mode 600, read by core only), reloadable by an
  admin command without restarting core. Partial grant: the module starts with the granted subset and a logged warning; a manifest
  capability flagged `required: true` makes core refuse the start when missing. The manifest ships with the module and is sent to core on
  connect (not compiled into core). Launch entries are a separate core config (section 4 of MODULES.md).
- **Trust stance (D19):** same machine only (UDS), owner's own modules only; capability checks are a seatbelt against bugs, not a
  defense against a hostile same-user process. Untrusted third-party modules are out of scope.

### 7.6 Observability and testing

- Correlation: every inbound item gets a `trace_id` (new GUID or from the front end); every event and call carries it plus the
  `cause_id` of the event that produced it (D34's log record). Logs include `trace_id`, `cause_id`, `module`, `event`, `targets`, `session`.
- Logging: today `Console.WriteLine`; use the same sink everywhere (stdout to journald). Module stdout/stderr is captured by core
  and re-logged with a `[module:name]` prefix.
- Metrics (counters/gauges via an admin RPC and logged every minute; no Prometheus dependency to start): inbound queue depth,
  tick duration (already warned in `GameLoop.Tick`), events processed per hook, handler latency, deadline misses, Denies,
  quota rejections, dormant-behavior skips, module restarts.
- Testing core with a fake module host: an in-process test double that scripts verdicts and can delay or drop them, driven by the
  existing `IClock` fake (`GameLoopTests.cs` already steps ticks with a fake clock).
- **No automated contract tests (D26)** (golden files, descriptor diffs, `buf`) until the contract settles after phases 3 to 5. No conformance suite (D25).
- A module author without core: a `ModuleTestHarness` library hosts a module's handler classes against a fake core (phase 4 card).
- Mongo is not available in the sandbox, so Mongo-backed paths stay unverified until the owner's host runs them (RUNTIME_STATE.md 4).

## 8. Phase plan

The seven phases are in scope (D35). The full plan, with deliverables, risks, sizes and the card split, is in
[V2_PHASES.md](V2_PHASES.md). Summary of the order:

1. Front-end-neutral core API; telnet as a project (still one process).
2. Components and behavior lists in core. Phases 1 then 2 are strictly sequential (D36: both touch Player/Room/World/GameObject).
3. In-process event bus.
   *Spike card before phase 4 (D38): UDS round-trip latency, Grpc.Tools codegen in CI, Kestrel on two UDS endpoints, ASP.NET Core in the console host `MUD.Main`; record measurements.*
4. gRPC over UDS and the first trivial out-of-process module.
5. Desert and temple modules.
6. Web front end on the core API (after modules, D37).
7. Telnet as a separate process.

## 9. Command migration table (33 command classes)

| Class | Category | V2 home |
|---|---|---|
| Look, Search, Get, Drop, Inventory, Say, Tell, Chat, Who, Status, Help, Commands, Save, Stop | Default | core built-in (search stays baked in, D2) |
| Quit | Default | core built-in (ends session; front end closes) |
| Chpwd | Default | core built-in (`ISensitiveInputCommand` redaction stays in core; the front end must not echo or log the argument) |
| Term | Default | front-end-local setting (selects the markup converter); the preference stays stored in `Account` |
| Wear, Remove, Wield, Lower | Default | stubs today; core built-ins when implemented (`Body` slots) |
| Build, Create, Clone, CD, Navigate, Terraform, Trans, Import, Export, GenMap | Creator | core built-ins (creator tools; Import/Export keep their optional one-off role, D39) |
| ApplyStatus | Creator | stub; not migrated until a use is specified |
| Promote | Admin | core built-in (role rules) |
| (empty files) Shutdown, Dest, Edit, LS, Proto, RM | - | not implemented; not migrated |

Exit commands (`AnonymousCommand` from `Room.RebuildExitCommands`) become the movement path through `entity.move`.

## 10. Design choices not in the owner's decision list

These were choices in the draft that no decision reverses. They remain the design's assumption but are not owner decisions; any can be reopened.

| # | Choice | Why |
|---|---|---|
| C1 | One core process owns the world on one game thread | Keeps GAME_LOOP.md rule; avoids distributed state; scale is small |
| C2 | Plain correlation-id strings in messages, not OpenTelemetry | More inspectable for a learner; OTel can be added later |
| C3 | Core renders nothing terminal-specific (D28 makes this a decision for output; the rest follows) | Requirement 1 |
| C4 | Plaintext passwords unchanged | Out of scope; separate card (WEB.md caveat) |

Open items that these depend on are in OPEN_QUESTIONS_V2.md ("Still open or deferred"); gaps found while revising are in
CORE_API.md section 9.

## 11. Follow-ups noticed while reading the code (NOT part of this design)

1. `Living.ResolveCommand` checks `CreatorCommands` for Admins instead of `AdminCommands` (`MUD.Core/GameObjects/Living.cs`, Admin branch).
2. `ExtendedProperty.InitializeProperty/RemoveProperty` have no caller outside the property classes (grep in this revision found only overrides and `BleedingProperty`'s own `RemoveProperty()` call).
3. `Food`/`Wand` are not registered for DI (commented-out lines in `MUD.Main/Program.cs` ~156-157, 170-178, 198-199), so they cannot be loaded from Mongo.
4. Six empty command source files (0 bytes: `Admin/ShutdownCommand.cs`, `Creator/{Dest,Edit,LS,Proto,RM}Command.cs`); the original card's "44 command classes" does not match the code (33 real).
5. Heartbeats only reach players, not NPCs or rooms (`World.doHeartbeat`, `MUD.Core/World.cs:633`).
6. `CommandSource.GetCommandFromInput` is a prefix match whose first-match order depends on dictionary enumeration.
7. `.github/workflows/ci.yml` still carries a "hermes has no test project yet" guard although test projects exist.

Backlog cards filed for these by the revision task are listed in the PR description and the card's completion summary.
