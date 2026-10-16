# Planar Realms — Architecture

## 1. Solution layout

```
MUD.sln
├── MUD.Main/         Executable host (Program.cs, appsettings.json)
├── MUD.Core/         Game logic: World, domain objects, commands,
│                     repositories, output formatting, properties
├── MUD.Data/         Generic MongoDB data access: Entity, Repository<T>,
│                     MongoDBClassMapManager
├── MUD.Telnet/       Raw-socket telnet Server/Client (no dependencies)
└── MUD.PlanarRealms/ World content module (room classes), loaded at
                      runtime by assembly name — NOT a csproj reference
```

Dependency graph:

```
MUD.Main ──► MUD.Core ──► MUD.Data
     │          │  └─────► MUD.Telnet
     ├──────────┴─────────► MUD.Telnet
     └─────────────────────► MUD.Data

MUD.PlanarRealms ──► MUD.Core   (scanned at runtime, not compile-referenced)
```

`MUD.Telnet` and `MUD.Data` are dependency-free leaf libraries.

## 2. Boot sequence (MUD.Main/Program.cs)

1. `Main(string ip, int port, string db)` — CLI args (DragonFruit package
   declared but unused).
2. `new MongoClient(db)`; hard-exit on failure. Database hardcoded to
   `testmud`; asserts `players` collection exists.
3. `new World(...)` — World builds its own DI container (see §3).
4. `World.Start()` on a dedicated thread (game loop).
5. `new Server(...)` (telnet) on a dedicated thread; program wires
   `ClientConnected` / `messageReceived`.
6. **Pre-login gate:** a raw `messageReceived` handler in `Program.cs`
   handles `connect <user> <pass>`, `create <user> <pass>`, `who`, `quit`
   directly — *not* via the command system. After successful login the
   handler is unsubscribed and the `Player` object takes over I/O.
7. Host-console debug loop on the main thread: `b` broadcast, `c` color
   test, `t` terminal-type subneg, `q` quit.

## 3. DI and type scanning (MUD.Core/World.cs)

`World.LoadTypes()` runs on the World thread:

- `Assembly.LoadFrom` of `MUD.Core.dll` and `MUD.PlanarRealms.dll` from the
  output directory (hardcoded name list, backslash path join — works, is
  fragile on Linux).
- Scrutor scans those assemblies and registers:
  - `ICommand` implementations → singleton (grouped by `CommandCategory`
    into `AllCommands`, `DefaultCommands`, `CreatorCommands`, `AdminCommands`)
  - `ITerminalHandler` → singleton
  - `Repository<>` subclasses → singleton
  - concrete `Entity` subclasses → transient
  - `World` itself → singleton
- `MongoDBClassMapManager` clears and re-registers BsonClassMaps so the
  Mongo driver instantiates entities through DI (constructor injection,
  e.g. `Player(World, CommandQueue, IPlayerRepository)`).

The content-assembly list is hardcoded; dropping in a new content DLL
requires editing `World.LoadTypes`. (Runtime reload of creator code is a
stated goal, not yet implemented.)

## 4. Game loop and concurrency

```
World.Start():
  Timer (2s)          → doHeartbeat: iterates Players → Living.DoHeartbeat
  while (_isRunning): → CommandQueue.ExecuteAllCommands()   ← no sleep
      each dequeue   → Task.Run(command.Execute)
```

- One `ConcurrentQueue<CommandExecution>` per `Living` in a
  `ConcurrentDictionary`; per-living sequential execution via an `IsBusy`
  flag. `stop` command clears a player's queue.
- **Concurrency model (current):** uncoordinated. Command tasks, the
  heartbeat timer, and telnet read callbacks all touch `World.Players`,
  `Room._occupants`, and repository caches simultaneously. See SPEC.md
  known-issues #4.
- Player auto-save every 10 minutes (heartbeat counter); disconnect calls
  `Player.Quit()` (save + remove) — net-death equals logout.

## 5. Data flow (one command)

```
Client keystrokes
  → MUD.Telnet.Client (async socket read, IAC state machine, line assembly)
  → Client.DataReceived event
  → Player (post-login) → World.DoCommand(player, input)
  → CommandQueue enqueue (per-player serial queue)
  → game-loop dequeue → Task.Run
  → Living.ResolveCommand:  room commands (RoomCommandSource)
                            → Living.KnownCommands
                            → creator/admin commands (if Admin/Creator property)
                            → DefaultCommands (fallback)
  → ICommand.ParseCommand → ICommand.DoCommand
  → Living.ReceiveMessage → ITerminalHandler.ResolveOutput (markup → bytes)
  → MUD.Telnet.Client.Send
```

Broadcasts: `World`/`Room.TellRoom` → each occupant's `ReceiveMessage` →
same terminal pipeline.

## 6. Domain model (MUD.Core/GameObjects)

```
MUD.Data.Entity                    (abstract; string Id ↔ ObjectId)
└── GameObject (abstract)          ShortDescription, LongDescription,
    │                              Plurals, Adjectives, Handles,
    │                              ExtendedProperties, Examine()
    ├── Living                   Race/Class/Gender, KnownCommands,
    │   │                          Inventory/WornItems/HeldItems,
    │   │                          CurrentLocationId, MatchObjects,
    │   │                          ResolveCommand, DoHeartbeat,
    │   │                          ReceiveMessage
    │   ├── Player                 auth fields, Client, Save/Quit
    │   └── NPC                    (empty stub)
    ├── Room                       Items, Features, Exits, Occupants,
    │                              RoomCommandSource, EnterRoom/ExitRoom
    ├── InventoryItem              └── Wand (charges), Food (servings)
    └── Feature                    (furniture referenced in descriptions)
Exit                               (Name, DestinationId; not a GameObject)
```

Rooms are resolved lazily from `CurrentLocationId` via `World.GetRoom` →
`IRoomRepository`.

## 7. Persistence (MUD.Data + MUD.Core/Repositories)

- Document-based MongoDB. Root class `Entity` with
  `[BsonIgnoreExtraElements]`; polymorphic subclasses each get a
  BsonClassMap registered by `MongoDBClassMapManager` (uses a reflection
  hack to clear the driver's private class-map table).
- `Repository<TModel> : IRepository<TModel>` (abstract): `IMongoCollection`
  + in-memory `Dictionary<string,T>` cache. `Update` = full `ReplaceOne`;
  success judged by `ModifiedCount` (unchanged resave returns `false`).
  Thread-safety: admitted `TODO` — currently not safe.
- Concrete: `PlayerRepository` (db `testmud`, collection `player`),
  `RoomRepository` (db `testmud`, collection `room`), each behind a marker
  interface (`IPlayerRepository`, `IRoomRepository`) for DI.
- `MUD.Data.Database` is a dead shell class (commented-out body).

## 8. Extensibility: ExtendedProperties

- `ExtendedProperty : Entity` — behaviors attached to objects, persisted in
  Mongo like any entity.
- Capability interfaces: `IHeartbeat` (ticked by `Living.DoHeartbeat`),
  `ISearchable`, `IExaminable`, `IUpdatable`.
- Existing: `BleedingProperty` (living), `AdminProperty` / `CreatorProperty`
  (player privilege flags — presence gates creator commands),
  `BasicSearchProperty` (room).
- Attached in-game via creator `apply` command; content modules may define
  their own property classes and have them DI-scanned.

## 9. Output formatting (MUD.Core/Formatting)

Markup format: `%^Option%^text%^Reset%^`. `ITerminalHandler.ResolveOutput`
(default interface method) walks `EFormatOptions` and substitutes each
option via `GetReplacement`.

| Handler | Notes |
|---|---|
| `DefaultTerminal` | strip-to-plain |
| `AnsiTerminal` | 256-color escapes |
| `GMudTerminal` | GMud codes |

Players pick one with the `term` command (`Player.SelectedTerm`).
`StringExtensions` holds list-joining, article, plural, and number-word
helpers.
