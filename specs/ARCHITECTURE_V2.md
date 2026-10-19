# Architecture V2: core / front-end split, composition, process-per-module (design spec)

Status: DRAFT for owner review. Docs only. No code, scaffolding, .proto files or csproj changes accompany it.
Base: `hermes` at 7c9bdd9. Task: t_866775c9.

Companion files (read together):

| File | Contents |
|---|---|
| ARCHITECTURE_V2.md (this file) | Requirements, current-state inventory, superseded specs, target architecture, entity model, commands, front ends, persistence, security, observability, migration plan, decision log |
| [CORE_API.md](CORE_API.md) | Event model, the API modules and front ends call, gRPC over Unix domain sockets (.proto sketches, .NET 8 notes), request-to-game-thread mapping |
| [MODULES.md](MODULES.md) | Module manifest and lifecycle, supervision, deployment on this host, the desert and temple worked examples, failure-mode table, testing |
| [OPEN_QUESTIONS_V2.md](OPEN_QUESTIONS_V2.md) | Numbered owner questions with options, recommendations and consequences |

Nothing in this design has been run. Where a statement about the current code is made, the file is cited
(paths relative to the repo root, on `hermes`). Where a claim about a library, the OS or performance could not be
verified here, it is marked **(unverified)**.

## 0. How to read this document

The owner is learning IPC and event-driven design as part of this work, so the design prefers explicit, inspectable
mechanisms over frameworks: plain protobuf messages, one stream per module you can log line by line, one game
thread, hand-written supervisor. Trade-offs are explained where a choice is made, and every choice that is the
owner's to make is in OPEN_QUESTIONS_V2.md (referenced as Q-numbers). Where this document says "recommended" it
means "the design below assumes it"; it is not an answer to a question.

## 1. Fixed requirements (owner)

1. Core functionality and the telnet-specific interface are separated. Telnet is a front end, not part of core.
2. Core handles the basic entities (room, player, ...) as it does now. Core is responsible for ALL entities in the
   world and ALL persistence. It is the only process that talks to MongoDB.
3. Composition over inheritance: core provides basic object types; behaviours are loaded from modules. Behaviours hook
   pre-defined events and follow a general contract.
4. Modules load and unload as needed. Each module is its own running process. Core starts them.
5. IPC is gRPC over Unix domain sockets. No network service calls.
6. Prefer explicit, inspectable designs; explain trade-offs.

Worked examples A (desert sandstorm) and B (temple lever and traps) are in MODULES.md sections 7 and 8.

## 2. Current-state inventory (grounded in `hermes`)

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
  `AnonymousCommand`. I could not reproduce 44; the migration table in section 8.4 uses the 33 real ones. Of those,
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

### 2.8 Couplings that the split has to cut (all verified in code)

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

If the owner accepts this design, these existing specs become partly historical. Each gets a "superseded by
ARCHITECTURE_V2.md section N" banner **when the corresponding migration phase lands**, not before.

| Spec | Status under V2 |
|---|---|
| WEB_EDITING.md sections 6 (game-side refresh/poll), 8 (Rev optimistic concurrency between two writers), 9 (what game stops writing), 13-14 (migration/tests for polling) | **Superseded** once the web goes through core (phase 6). With one writer there is no cross-process conflict to detect between web and game. `Rev` stays only if the owner wants optimistic concurrency between two human editors (Q27). `RoomWebSync`, `PlayerWebSync`, `Player.ApplyWebEdit/SyncWebFieldsFromStore`, `Room.ApplyWebEdit`, `PlayerFields.NotWrittenByGame`, `WithoutWebBookkeeping` go away |
| WEB_EDITING.md sections 4 (allow-lists), 5/15 (roles), 7 (audit), 10 (importer interaction), 15 (owner decisions) | **Survive as rules**, enforced in core instead of the web: see the survive list below |
| WEB.md | Routes, cookie auth and the plaintext caveat survive. "Talks to Mongo" is **invalidated**: MUD.Web becomes a core API client. `MongoAccountStore` et al. are removed in phase 6 |
| GAME_LOOP.md | **Survives and is extended.** The single game thread, queues, tick, NextActionAt, persistence worker, spool, logout ordering stay. Changes: the inbound `Line/Connected/Disconnected` kinds are generalised to front-end session messages; `IClientConnection` is replaced by a session abstraction; gRPC handlers become another producer of `WorldEvent` items |
| RUNTIME_STATE.md | **Survives** (write-through, definition vs state, repository lock). Its per-mutation table (get/drop/build/import) is updated when components land |
| ROOM_FILES.md | YAML format, identity, import semantics, definition-vs-state **survive**. Section 4.5/7 (behaviour registry resolving to `ExtendedProperty` types) and section 8 (`class:` room subclass) are **superseded** by component bindings and module bindings (section 5.5 below). Section 9 (locally-modified flag) is unchanged and still unimplemented |
| ARCHITECTURE.md, API.md, SPEC.md, DEPLOYMENT.md, CONFIGURATION.md | Describe today's layout; updated phase by phase. DEPLOYMENT.md gains module units/children in phase 4 |
| DESIGN_PRINCIPLES.md | **Survives intact.** "Everything is persistent" is the constraint that shapes module state (section 5.4). The sentence "mudlib is mostly data, a C# subclass is the exception" is strengthened: modules replace subclassing |

Rules that survive unchanged (and where they are enforced in V2):

- **Audit**: per-field Old/New rows, audit-first (`Applied:false` before apply), Password never an audited field,
  Admin-readable only (WEB_EDITING 7 and 15 Q9/Q10). In V2 core writes the audit row for **every** mutation that has a
  non-system actor; a module is an actor (`Source: "module:<name>"`) (section 7.4).
- **Role strictness**: inclusive roles (Admin includes Creator), cannot grant above own role, edit only strictly lower
  roles, nobody edits an Admin via web, last-Admin check. Enforced in core's API handlers for front ends.
- **Room deletion behaviour**: default room (StartRoomKey) never deletable; verify destination first; contents to a
  random adjacent room else the default room; wind message; dangling exits removed and audited. Becomes a core
  operation `DeleteRoom`; modules may not call it (capability).
- **Room update message**: "Something feels different..." Core emits it on creator/web edits; module-driven
  overlay changes are not announced this way (modules supply their own text).
- **Retirement**: retired characters cannot log in; admin/self rules as in WEB_EDITING 15.
- **Plaintext passwords for now** (WEB.md caveat). V2 does not change password storage. Note the consequence: a
  front end that forwards passwords to core over the UDS sees plaintext; this is no worse than today (section 11).
- **Single game thread** (GAME_LOOP.md): all entity mutation happens on one thread inside core.
- Persistence principle (DESIGN_PRINCIPLES 1): no gameplay state only in memory.

## 4. Target architecture

### 4.1 Processes

```
                          Internet / LAN                          localhost only
   telnet clients ---TCP:7680---+                         browser ---HTTP:5080---+
                                v                                                v
                      +-------------------+                          +-------------------+
                      |  telnet front end |                          |   web front end   |
                      |  (MUD.Telnet host)|                          |   (MUD.Web)       |
                      |  sockets, telnet  |                          |   Razor Pages     |
                      |  options, ANSI    |                          |   cookie auth     |
                      +---------+---------+                          +---------+---------+
                                |  gRPC / Unix domain socket                   |  gRPC / UDS
                                |  ($XDG_RUNTIME_DIR/mud/core.sock)            |
                                v                                              v
  +--------------------------------------------------------------------------------------------+
  |                                      CORE (MUD.Main)                                       |
  |   gRPC host (Kestrel on UDS)                                                               |
  |      |  every request becomes an InboundItem                                               |
  |      v                                                                                     |
  |   +-----------------------------  GAME THREAD (single)  --------------------------------+  |
  |   | World: rooms, players, NPCs, items, features, components | command resolve/execute |  |
  |   | event dispatcher (pre/post) | timers | tick | capability + quota checks | audit rows |  |
  |   +-----------------------------------------------------------------------------------+  |
  |   persistence worker --> MongoDB (ONLY process with a Mongo connection)                    |
  |   module supervisor (start/stop/restart, health, backoff)   YAML import/export             |
  +-------+------------------------------+-----------------------------+-----------------------+
          | gRPC / UDS (same socket,     |                             |
          | one bidi stream per module)  |                             |
          v                              v                             v
   +--------------+              +--------------+               +--------------+
   | module:      |              | module:      |               | module: ...  |
   | desert       |              | temple       |               |              |
   | own process  |              | own process  |               |              |
   | own memory   |              | own memory   |               |              |
   +--------------+              +--------------+               +--------------+
        children of core (spawned and supervised by core)
```

Responsibilities:

| Process | Owns | Does not do |
|---|---|---|
| core | All entities, all rooms/players/items/components, the game thread, command resolution for built-ins, event dispatch, timers, capabilities, quotas, audit, persistence (sole Mongo client), YAML import/export, module supervision, the API socket, accounts and authentication | Telnet protocol, terminal escape sequences, HTML, module-specific rules |
| telnet front end | TCP accept, telnet negotiation (NAWS/TTYPE/echo), line framing and input limits, connection caps, idle timeout, throttling of raw connection rate, rendering semantic output to ANSI, prompt echo-off | World state, command parsing beyond "this is a line", Mongo |
| web front end | HTTP, cookie session, pages, CSRF, throttle of web login attempts | Mongo. Calls core API for every read and write |
| module | Its own behaviour code and in-memory working state; handler logic for subscribed events; schema of its own components | Mongo, other modules' data, direct sockets to anything but core, mutating entities without going through the API |

What stays **in-process in core** (and why): the game thread and world model (single-writer rule), the persistence
worker (it needs snapshots from the game thread, GAME_LOOP.md 8), built-in commands that are inseparable from the
world model (look, get, drop, movement, say, tell, who, inventory, ...), account/authentication, creator/admin
tooling (build, import, export), YAML import/export, the event dispatcher and supervisor. Splitting any of these into
processes would add IPC on the hottest paths without a stated benefit.

### 4.2 Why one core process and not a "services" mesh

Everything that mutates the world must serialise on one thread (GAME_LOOP.md owner decision 1, which this design
keeps). A multi-process core would need distributed transactions for no gain at this scale. The extension points
are therefore: modules (behaviour) and front ends (I/O). Both are clients of the same API.

### 4.3 In-process modules (a deliberate escape hatch)

The module contract (events in, API calls out) is defined as an interface, with two adapters: an out-of-process adapter
(gRPC stream) and an in-process adapter (a C# class running inside core and called on the game thread). Built-in
behaviour such as the room search and timed-effect behaviours can start in-process (migration phase 3) and be moved out
when it is worth the IPC. This keeps the contract honest (the same tests run against both adapters, MODULES.md 11) and
lets the owner decide per module whether the isolation matters (Q4).

## 5. Entity model

### 5.1 Core object types

Core keeps a small, closed set of entity **kinds**. A kind fixes the minimum components an entity must have and which
core-owned operations apply to it.

| Kind | Today | Required components | Notes |
|---|---|---|---|
| `room` | `Room` | `Description`, `Exits`, `Position` (area, X/Y/Z), `Contents` | Has occupants (derived from location, not stored) |
| `player` | `Player` | `Description`, `Location`, `Container` (inventory), `Account`, `Roles`, `Body` (gender/race/class) | Account holds name, NameKey, password (plaintext for now), Retired, term preference |
| `npc` | `NPC` | `Description`, `Location`, `Container`, `Body` | No Account. `NPC` as a subclass disappears; "npc" is just a kind |
| `item` | `InventoryItem`, `Food`, `Wand` | `Description`, `Location` | `Food.Servings`, `Wand.CurrentCharges` become module or core-registered components |
| `feature` | `Feature` | `Description`, `FeatureText` (RoomLine, DetailDescription) | Stays attached to a room (embedded or child entity: Q8) |

`Living` disappears as a type. "Living" becomes a query: "has `Location` and `Body`". Code that is written
`if (x is Living)` becomes `if (x.Has<Body>())`. `Player`/`NPC` differ by kind and by having an `Account`/`Session`.

### 5.2 Components

A **component** is a named bag of data attached to an entity. One component of a given name per entity.

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

Module-owned components (schemaless to core, namespaced `<module>.<name>`, e.g. `desert.sandstorm_overlay`,
`temple.trap`): described in 5.3.

### 5.3 Typed versus schemaless component data

| | Typed (C# classes in core) | Schemaless (protobuf `Struct`/BSON document) |
|---|---|---|
| Who uses it | Core-owned components | Module-owned components |
| Validation | Compile time + validators | Optional JSON-Schema fragment in the module manifest; core validates writes when a schema is declared |
| Query/index | Core can index and join | Core can store and return, and match simple equality on top-level fields only (Q6) |
| Evolution | Core migrations (like `ObjectIdMigration`) | Module bumps a `schema_version` it stores in the component; the module migrates its own data on load |
| Mongo shape | Fields on the entity document | Sub-document under `components.<module>.<name>` |

Recommendation: typed for core, schemaless-with-optional-schema for modules. A fully schemaless core would make the
renderer, matcher and import impossible to reason about; a fully typed module system would force a core release for
every new module field. This is Q6.

### 5.4 Persistence ownership and module state

Three classes of data, because "no gameplay state only in memory" (DESIGN_PRINCIPLES 1) has to hold for modules too:

1. **Core data**: entities and core components. Persisted by core.
2. **Module component data**: attached to entities (room has a `desert.exposure` component; a lever has
   `temple.lever`). Module requests the write via the API; core validates, applies on the game thread, audits, persists
   with the entity. It is therefore visible to queries, survives restarts, and **survives module unload and upgrade**
   because it lives in core's documents under the module's namespace and is never deleted merely because the module
   is not running (the unload policy is explicit, MODULES.md 5.4).
3. **Module-private state**: not attached to any entity (sandstorm schedule, a storm's position as a record). Recommended: the module stores it through the same API into a
   per-module key/value document (`ModuleState`), persisted by core in a `module_state` collection. It is still
   "owned" by the module (core does not interpret it) but core persists it, so the module is stateless across restarts. Alternative: modules keep private state in memory only and rebuild on start
   (simple, but violates the principle for state that matters, and loses a storm on crash). Alternative: modules get
   their own storage (violates requirement 2: core sole persistence). Q5, Q7.

Rule of thumb: if losing it on a crash would be visible to a player, it must be in 2 or 3.

### 5.5 How modules attach behaviour

A module never subclasses anything. It does three things:

1. Declares in its manifest which **events** it subscribes to and, per subscription, a **filter** (entity kind, area
   prefix, component present). Filtering happens in core so uninterested modules are never woken.
2. Declares **component names** it owns (namespace) and optional JSON Schema.
3. Declares **verbs** it contributes (commands) and **capabilities** it needs.

Binding to world content (Q9): an area YAML header lists modules (`modules: [desert]`), and individual entities
carry module components declared in YAML (`components: { temple.trap: { damage: 10 } }`) replacing the
`properties: - behavior: search` form of ROOM_FILES.md 4.5. The importer treats a module component's `config` as
definition and its `state` as state, as today (ROOM_FILES.md 5, 7.4). Import validates component names against
manifests of installed modules (unknown component = validation error, same as unknown `behavior` today).
When an area is loaded, core starts every module named in the area header that is not running (example A).

### 5.6 How this replaces inheritance and ExtendedProperty

| Today | V2 |
|---|---|
| `Living` : `Player`, `NPC` | kinds + components; `Living` = "has Location + Body" |
| `Room` subclass via `class:` | plain `room` kind + module components/handlers; ROOM_FILES 8 `class:` field is removed (mapped to a module binding at migration) |
| `InventoryItem` : `Food`, `Wand` | `item` kind + components (`food.servings` / `wand.charges`), `Examine` text modifiers via the `entity.describe` pre-event (CORE_API 2.4) |
| `AdminProperty`, `CreatorProperty` | `Roles` core component |
| `BleedingProperty` (heartbeat) | a timed status effect: component `status.bleeding` + a `status` module subscribed to `tick`; or built-in |
| `BasicSearchProperty` | component `search` (config in YAML) + handler on the `search` verb/event, either built-in in-process module or a module |
| `IHeartbeat` | the `tick` event (CORE_API 2.3) delivered to subscribers, scoped by filter |
| `ISearchable`, `IExaminable`, `IUpdatable` | events `command.search`, `entity.describe`, `tick` |
| Virtual `Examine(examiner)` overrides | `entity.describe` pre-event allowing modules to append/replace text |

Persisted-data migration is a design topic of its own (existing documents have `_t` discriminators and
`ExtendedProperties`); the plan is "no data to migrate" per DESIGN_PRINCIPLES 3 (no live players), but this must be
confirmed by the owner (Q10).

## 6. Event model (summary; full catalogue in CORE_API.md section 2)

Core defines named events. **Pre-events** are synchronous from the command's point of view: the action is held, subscribed
modules are asked in priority order, any may veto or alter, with a hard deadline per handler. **Post-events** are
notifications after the action is committed: fire and forget. All handlers are called with a correlation id. The
critical rule of the whole design: **the game thread never blocks waiting for a module**. A pre-event suspends the
*action* (the command is parked, the actor's `NextActionAt`-style busy flag is set), the loop carries on, and the
module's reply arrives later as an inbound item that resumes the action. That keeps GAME_LOOP.md rule "no
blocking in commands" intact. Details and the cost: CORE_API.md 2.2 and 6.

## 7. Cross-cutting design

### 7.1 Commands

Where commands live:

| Group | Home in V2 | Examples |
|---|---|---|
| Inseparable from world model | core built-ins | look, get, drop, inventory, say, tell, chat, who, status, help, commands, save, stop |
| Account/session | core built-ins (session verbs the front end forwards) | quit, chpwd (+ `term` as a **front-end-local** setting, section 7.2) |
| Creator/admin tooling | core built-ins (need import/export/Mongo path) | build, create, clone, cd, navigate, terraform, trans, import, export, genmap, promote |
| Movement | core built-in generated from `Exits` (as today, `Room.RebuildExitCommands`), emitting `entity.move` pre/post events | north, n, ... |
| World behaviour verbs | modules | search (when it becomes a module), pull (lever), future spells and skills |

Raw line flow (CORE_API.md 6): front end sends `SubmitLine(session, line)`; core, on the game thread, takes the
prompt-handler shortcut if any (passwords), else resolves in the same order as today (room exits -> known commands ->
role commands -> defaults) with one addition: **module-contributed verbs** are consulted after room exits and before
defaults. If the verb belongs to a built-in, it runs as today. If it belongs to a module, core emits
`command.invoke` (a pre-event-shaped request/response) to that module with the parsed text; the response carries
output messages and requested mutations, applied atomically by core. Prefix matching (`GetCommandFromInput`) is
replaced by an explicit verb table keyed by a case-folded token with deterministic precedence (the current
dictionary-order behaviour is an existing wart, not preserved).

### 7.2 Output and rendering

Today core renders (`%^Red%^` -> ANSI) and `Player` holds the connection. In V2 core produces **semantic output**
and never sees a terminal. Two options for the wire format:

- A (recommended first cut): keep the existing `%^Token%^` markup string as the wire format. The front end owns the
  `ITerminalHandler`s (`AnsiTerminal`, `GMudTerminal`, `DefaultTerminal` move out of `MUD.Core/Formatting`) and the
  per-session terminal choice; the `term` command becomes a front-end-local setting (or a core setting stored in
  `Account.Term` and sent at session start). Simple and low-risk; markup text remains stringly typed.
- B: structured output (a repeated list of `Span { text, style }` plus message kind: room description, say, system).
  Lets the web render HTML styles and lets modules emit styled text without knowing the markup. More work and more schema.

Message envelope in either case: `Output { session_id, channel, text }` where channel is `game`, `system`, `chat`,
so a web client can later split them (Q24).

### 7.3 Front ends

Telnet front end - separate **process** or separate **project in the same process**? Evaluated:

| | Separate project, in-process with core | Separate process (recommended end state) |
|---|---|---|
| Isolation | A telnet bug can crash core | Telnet crash/DoS does not touch world state or lose unflushed writes |
| Complexity | Low: only needs an interface boundary | gRPC session stream, reconnect logic, per-line IPC |
| Latency | None | ~1 UDS round trip per line (**unverified**; expected well below telnet RTT) |
| Learning value | Low | High (same API the web and modules use) |
| Restart | Restarting telnet restarts the game | Telnet restarts alone (players reconnect); core restart alone drops sessions |

Recommendation: first extract the boundary as a **separate project in the same process** (phase 1) because it is the
cheapest way to prove the interface and keeps the game deployable at every step, then move to a separate process in
phase 7 behind the same interface. This is Q1.

Web front end: `MUD.Web` stays an ASP.NET Core process; its stores (`MongoAccountStore`, `MongoRoomEditStore`,
`MongoPlayerEditStore`, `MongoAuditLog`, `MongoRoomReader`) are replaced by thin API clients. Its validation
(`MUD.Data/RoomRules`, `PlayerFields`) moves to core because core now enforces it; the web may keep a copy for form
feedback only.

Session ownership: core owns the **player** and the **session record** (`session_id`, player id, front end id,
state). A front end owns the **connection**. A session is created when core accepts `Authenticate`/`CreatePlayer`
from a front end. Identity of a connection towards core is the `session_id` core returned, never a socket handle.

When a front end restarts: its streams to core break. Current behaviour on disconnect is "log the player out"
(`World.handleInbound` Disconnected -> `Player.Quit`), and net-dead handling was deferred (GAME_LOOP Q14). V2 keeps
that default: core treats the front end's stream loss as a disconnect of every session on it. Optional improvement: a
grace period (`linkdead` for N seconds) so a front-end restart does not log everybody out (Q25).
When core restarts: all sessions are gone (as today on a server restart); front ends reconnect with backoff and
show connected clients "The world is restarting" and a login prompt (or hold the TCP connection open: Q26).

### 7.4 Persistence and consistency

- Core is the sole writer and the sole Mongo client. The `MongoClient` in `MUD.Web` is removed.
- Write path unchanged: game thread mutation -> snapshot -> `PersistenceWorker` (GAME_LOOP.md 8). A mutation requested
  by a module is simply another source of the same game-thread mutation; the persistence behaviour is identical to a
  player-caused change.
- Audit: the existing `audit` record gains `Source: "web" | "module:<name>" | "game" | "system"` and a `CorrelationId`.
  Rule of proportion: web and creator edits audit **per field**, as today. Module mutations audit at **batch level by
  default** (one row per `ApplyBatch` listing entity id, component name, old/new hash or value), configurable per
  module/component (an auditing every sandstorm step would flood: Q34). Audit-first still holds for web/creator edits;
  for module mutations the audit write is queued with the data write (Q34).
- Snapshots/backup: `mongodump` against a stopped or quiesced core is the baseline; because core is the only
  writer, a consistent backup can be taken by asking core for a flush (`Flush` admin RPC) first. The spool directory
  must be included.
- Out-of-band tools: `scripts/seed-admin.sh` talks to Mongo directly. Under V2 rule 2 it must either (a) run only with
  core stopped, or (b) be replaced by a core admin RPC (`EnsureAdmin`, callable from a `core-admin` CLI over the UDS with
  OS-user peer check). Recommended: (a) documented now, (b) later. Any other tool that writes Mongo is subject to the
  same rule. Module state created while core is stopped does not exist: modules cannot run without core.
- Consistency between core and module: core is authoritative. A module's view is a cache; any decision needing
  authoritative state is re-checked by core when applying the mutation (preconditions in the request, CORE_API 3.3).

### 7.5 Security and trust model

Facts: all processes run as the same OS user (`mark`) on one host. Unix socket file mode 0600 inside a 0700 directory
(`/run/user/1000/mud/`; `/run/user/1000` is `drwx------` here, verified) stops *other* OS users, not other processes of the same user.

What the design gives:

- Fault isolation: a module crash, leak or infinite loop cannot corrupt the game thread or core memory. Module
  failure is contained to its own events.
- Authority isolation by API: a module can only do what the API exposes, checked against its capability grant; it has
  no Mongo credentials (the `MUD_MONGO_URI` is only in the core unit's environment; modules are spawned with a cleaned
  environment).
- Rate/quota isolation: per-module limits (CORE_API 5).

What it does **not** give:

- No protection against a malicious module: a same-user process can `ptrace` or read `/proc/<pid>/environ` of core, read
  `~/.config/mud/mongo.env` (mode 600, same user), and talk to Mongo directly. Capability checks are a seatbelt against
  *bugs*, not against *attackers*.
- Authentication on the socket is a shared-secret handshake (a per-start token passed to the child in its environment) plus,
  optionally, a peer PID check via `SO_PEERCRED` **(.NET exposure unverified; may need `getsockopt` via P/Invoke)**. Any same-user
  process that learns the token could impersonate a module.

Stance: **only the owner's own modules run, trust-by-review** (Q37). Untrusted third-party modules would require running
them as another OS user, in a systemd sandbox (`DynamicUser`, `ProtectHome`, namespaces) and a socket reachable across users (ACL / group). That is
a future option and out of scope. Plaintext passwords pass over the UDS from front ends to core; with the
socket 0600 this is no worse than today where the password crosses a TCP socket in plaintext to telnet already; hashing remains a separate
future card.

### 7.6 Observability and testing

- Correlation: every inbound item gets a `trace_id` (new GUID or from the front end); every event and API call carries
  it; logs include `trace_id`, `module`, `session`, `entity`. The gRPC metadata mirrors it (`x-trace-id`).
  .NET's `Activity` / W3C trace-context support can carry it, though a plain string field is sufficient and more inspectable.
- Logging: today it is `Console.WriteLine`; use the same sink everywhere (stdout to journald). Module stdout/stderr is
  captured by core and re-logged with a `[module:name]` prefix.
- Metrics (counters/gauges exposed via a `GetStats` RPC and logged every minute; no Prometheus dependency to start):
  inbound queue depth, tick duration (already warned in `GameLoop.Tick`), events dispatched per module, handler latency
  p50/p99, deadline misses, vetoes, quota rejections, module restarts, stream reconnects.
- Testing core with a fake module host: an in-process `IModuleHost` test double that scripts responses and can delay or drop them,
  driven by the existing `IClock` fake (`GameLoopTests.cs` already steps ticks with a fake clock). The in-process adapter (4.3)
  runs the **same** contract tests as the gRPC adapter.
- Contract tests for the .proto: (a) golden binary files per message version checked in as test fixtures, decoded by
  current code (backward compatibility); (b) a test that rejects removal/renumbering by diffing the compiled descriptor
  set against a committed baseline (tool choice is **unverified**; `buf breaking` is a common choice but would be a new tool dependency, Q22);
  (c) a conformance suite any module host/adapter must pass.
- Module author without core: a `ModuleTestHarness` library (phase 4) that hosts a module's handler class against a fake
  core: feeds scripted events and records requested mutations. No sockets, no Mongo.
- Mongo is not available in the sandbox, so Mongo-backed paths stay unverified until the owner's host runs them (as
  specs/RUNTIME_STATE.md 4 already states).

## 8. Migration plan

Principle: every phase ends deployable with `dotnet build MUD.sln` and `dotnet test MUD.sln` green, and behaviour for
players unchanged unless stated. Sizes are rough: S = a day or two of agent work, M = several days, L = a week plus.
Each phase should be split into several cards later, as listed.

### Phase 1: front-end-neutral core API; telnet out of core (still in one process) - size M, split into 3-4 cards

Scope: introduce `ISession`/`IFrontEnd` abstractions in core (`SendOutput`, `Disconnect(flush)`, prompt/echo control);
move `ITerminalHandler`s and `SelectedTerm` rendering out of core into the telnet project; move `LoginSession`,
`LoginThrottle` into a `MUD.FrontEnd.Telnet` project that references core, not the reverse; delete the `MUD.Core ->
MUD.Telnet` project reference; replace `Player.Connection : IClientConnection` and `World._sessions` keyed by
connection with `session_id` keyed sessions; `World.DoLogin/CreatePlayer` become API methods that return results rather than writing to a connection. Core API shape defined as C# interfaces mirroring CORE_API.md
section 3, still called in-process.
Risk: medium - touches `Player.cs`, `World.cs`, `Program.cs`, and 7-8 test files (TelnetClientTests, TelnetLimitTests, LoginSessionTests,
SessionTakeoverTests, PlayerQuitTests, ServerLifecycleTests, InputSanitiseTests, ConcurrencySafetyTests). Hotspot: `Player.cs` (~450 lines mixing identity, persistence, web sync and connection).
Can be cut: nothing; everything later depends on it. Can be split: (1a) session abstraction and removal of telnet types from `Player`/`World`; (1b) move rendering and `term`; (1c) move login flow; (1d) project reference flip and tests.

### Phase 2: components alongside the old types; migrate ExtendedProperty - size L, split into 4-5 cards

Scope: introduce `Components` on entities (core typed + module schemaless dictionary), a component registry, BSON shape
`components.<name>`. Dual-read: old fields remain, new components are populated; port roles (`Roles`), search config,
bleeding; convert `Living.DoHeartbeat` to a component tick; stop using the polymorphic `ExtendedProperties`. Change the YAML `properties` loader to emit components; keep importer semantics (ROOM_FILES 6) including config-vs-state split (fixes the "cooldowns reset on every boot" gap of ROOM_FILES 7.4).
Risk: high - persisted shape changes, class maps, the web reads `ExtendedProperties` `_t` for roles (`MUD.Web` `MongoAccountStore`) so
the web must be updated in the same phase or the old field kept as a mirror until phase 6.
Can be cut: removing `Living`/`Player`/`NPC` subclasses can be deferred (components can coexist); keep as separate card.
Split: (2a) component registry + storage; (2b) Roles + web mirror; (2c) search/bleeding migration; (2d) YAML bindings; (2e) retire inheritance (optional, later).

### Phase 3: in-process event bus with pre/post contract - size M, split into 3 cards

Scope: event dispatcher in core with subscriptions, filters, priorities, pre-event suspension (command parking) and
post-event delivery; fire the catalogue's core events from movement, get/drop, login/logout, tick; an in-process
module adapter; port one built-in behaviour (search) to it. Introduce the quota and capability checkers (no gRPC yet).
Risk: medium-high - the parked-command state machine is the hardest new concept (CORE_API 2.2). Needs deterministic tests with the fake clock.
Can be cut: priorities and filters beyond "event name + area"; the `entity.describe` event.
Split: (3a) dispatcher + post-events; (3b) pre-events and parked commands; (3c) port search + timers.

### Phase 4: gRPC over UDS host and first out-of-process module - size L, split into 4 cards

Scope: add Grpc.AspNetCore to core (Kestrel on a UDS), .proto files, module supervisor, manifest loader, handshake and
authentication, one bidi stream per module, reconnect, health, the `hello`-style trivial module (greets on login, counts
moves), module test harness, systemd/deployment notes, structured logging with trace ids.
Risk: high for learning curve, moderate for code. New dependencies (Grpc.AspNetCore, Grpc.Tools codegen at build; CI uses `actions/setup-dotnet@v4` with 8.0.x and plain `dotnet build`, so codegen via NuGet should work in CI **(unverified)**).
Can be cut: peer-credential checks, JSON-Schema validation of component writes, hot reload.
Split: (4a) .proto + generated stubs + contract tests; (4b) core gRPC host and handshake; (4c) supervisor and lifecycle; (4d) trivial module + harness + docs.

### Phase 5: desert and temple example modules - size M-L, split into 2 cards

Scope: implement the two worked examples (MODULES.md 7-8) as real modules with real area content, including overlay
components and cleanup on unload. These double as the acceptance test of phases 2-4.
Risk: medium; likely to expose gaps in phases 2-4 (budget a revision pass on CORE_API).
Can be cut: the temple (keep desert), or the lever (keep traps).

### Phase 6: web front end on the core API; remove Rev/poll - size L, split into 4-5 cards

Scope: expose front-end RPCs (CORE_API 3.4) for accounts, rooms, players, audit, map; rewrite `MUD.Web` stores as clients;
remove `MongoClient` from `MUD.Web`; delete `RoomWebSync`, `PlayerWebSync`, `ApplyWebEdit`, `SyncWebFieldsFromStore`,
`PlayerFields.NotWrittenByGame` special casing; port the audit writer to core; update WEB.md and supersede WEB_EDITING 6/8/9.
Risk: medium - big surface, but behaviour is spec'd and tests exist in `MUD.Web.Tests` (8 files) that can be re-targeted at a fake core client.
Can be cut: move one page at a time; the poll machinery is removed last, after all pages use the API.
Split by page group: accounts+login; rooms; players+audit; map; cleanup.

### Phase 7: telnet front end as a separate process - size M, split into 2-3 cards

Scope: run the telnet host as its own executable talking to core over the same session RPC; second systemd unit; handle
front-end/core restart semantics (7.3); delete the in-process adapter if unused.
Risk: low-medium given phase 1 and 4; main risks are session-loss behaviour and output backpressure.
Can be cut: yes - the game works with the telnet front end in-process indefinitely. This phase is optional for the
owner's stated goal if Q1 is answered "in-process first".

### Order assessment

The suggested order (1..7) is kept with two amendments: (a) phase 2 (components) depends on nothing in phase 1 and could run in
parallel by a different worker, but both touch `GameObject.cs`/`Player.cs`/`Room.cs` so sequential is safer (hotspot
files); (b) the **web move (6) could be done before the out-of-process module work (4-5)** if the owner cares more about removing Mongo access
from `MUD.Web` than about modules, since it needs only phase 1's API. Which comes first is Q40.

Total: roughly 7 phases, about 28 cards. Nothing is started by this document.

## 9. Command migration table (33 command classes)

| Class | Category | V2 home |
|---|---|---|
| Look, Get, Drop, Inventory, Say, Tell, Chat, Who, Status, Help, Commands, Save, Stop | Default | core built-in |
| Quit | Default | core built-in (ends session; front end closes) |
| Chpwd | Default | core built-in (`ISensitiveInputCommand` redaction moves with the prompt flow to the front end: the front end must not echo or log the argument) |
| Term | Default | front end-local (selects renderer); core stores preference in `Account` if persistence wanted |
| Search | Default | module or in-process built-in (uses `search` component) - candidate for first port |
| Wear, Remove, Wield, Lower | Default | stubs today; core built-ins when implemented (`Body` slots) |
| Build, Create, Clone, CD, Navigate, Terraform, Trans, Import, Export, GenMap | Creator | core built-ins (creator tools; need importer/exporter and audit) |
| ApplyStatus | Creator | stub; becomes a module-facing creator command that sets a component |
| Promote | Admin | core built-in (role rules) |
| (empty files) Shutdown, Dest, Edit, LS, Proto, RM | - | not implemented; not migrated |

Exit commands (`AnonymousCommand` from `Room.RebuildExitCommands`) become the movement path through `entity.move`.

## 10. Decision log

| # | Decision (recommended) | Alternatives considered | Why |
|---|---|---|---|
| D1 | One core process owns the world on one game thread | Sharded/multi-process core; actor-per-room | Keeps GAME_LOOP.md rule; avoids distributed state; scale is small |
| D2 | Modules are clients of an explicit API; no in-core plugin DLLs | Load module assemblies in core (as `MUD.PlanarRealms` is loaded today) | Requirement 4; isolation; but an in-process adapter exists for the same contract |
| D3 | gRPC over UDS | Raw protobuf over socket; JSON lines; named pipes; D-Bus | Requirement 5; code generation, deadlines, streaming and status codes built in; .NET 8 supports it (CORE_API 8) |
| D4 | One long-lived bidirectional stream per module for events and requests, plus unary RPCs only for front ends' request/response | Per-call unary in both directions (two servers); server-stream + unary; client polling | Ordering of events per module; single connection to supervise; core can push without the module hosting a server. Trade-off: multiplexing request correlation is manual (CORE_API 7) |
| D5 | Pre-event suspends the action, never blocks the game thread | Block the game thread with a short deadline | A blocked loop stalls every player and breaks GAME_LOOP 6; the suspend model costs a re-validation step but keeps tick latency independent of modules |
| D6 | Overlay components tagged with owner for module-made room changes | Module rewrites room definition fields and later restores | Core can undo exactly the module's changes on crash/unload without the module's help; definition stays authored (DESIGN_PRINCIPLES 2) |
| D7 | Module state persisted by core (component data and `ModuleState`) | Module-private files/DB; in-memory only | Requirement 2; principle "everything is persistent" |
| D8 | Schemaless module components with optional schema | Typed codegen per module; fully typed core only | Modules evolve without core release |
| D9 | Core renders nothing terminal-specific; front end renders | Core keeps ITerminalHandler | Requirement 1; enables web output |
| D10 | Telnet first as project, then process | Process immediately; stay in-process | Lowest risk first, still ends at requirement 1 |
| D11 | Fail policy per hook declared in the manifest, core default per event class (CORE_API 2.5) | One global policy | Trap (fail open) vs paid-action/rule-enforcement (fail closed) differ |
| D12 | Core starts modules as child processes | systemd template units per module started by core via `systemctl --user` | Requirement 4 says core starts them; children die with core's cgroup. Systemd units remain an option for later hardening (MODULES 9) |
| D13 | Plain correlation-id strings in messages | OpenTelemetry/Activity everywhere | More inspectable for a learner; OTel can be added later |
| D14 | Plaintext passwords unchanged | Hash during this work | Out of scope; separate card (WEB.md caveat) |

Open items that these decisions depend on are in OPEN_QUESTIONS_V2.md; if an answer contradicts a "recommended" decision above, the dependent sections need a revision pass (the questions note this).

## 11. Follow-ups noticed while reading the code (NOT part of this design; recommend as backlog cards)

1. `Living.ResolveCommand` checks `CreatorCommands` for Admins instead of `AdminCommands` (`Living.cs`, Admin branch). Possibly intentional (admin commands reachable via `KnownCommands`) but looks like a bug. Verify before V2 changes resolution.
2. `ExtendedProperty.InitializeProperty/RemoveProperty` appear never to be called on add/remove; `BleedingProperty` messages rely on them.
3. `Food`/`Wand` are not registered for DI (commented-out lines in `Program.cs`), so they cannot be loaded from Mongo.
4. Six empty command source files (0 bytes) are in the tree and the card's "44 command classes" does not match the code (33 real).
5. Heartbeats only reach players, not NPCs or rooms.
6. `CommandSource.GetCommandFromInput` first-match order is dictionary enumeration order.

These were not fixed, per scope rules. No backlog cards were filed by this task (the card forbids follow-up cards; these are
recorded here for the owner/orchestrator to file).
