# Planar Realms — API Reference (key interfaces)

Concise reference for the extension points. All types verified against
commit `ce8f7b9`.

## 1. Command system (MUD.Core/Commands)

```csharp
public enum CommandCategories { Default, Learned, Creator, Admin }

public interface ICommand
{
    string[] CommandKeywords { get; }
    CommandCategories CommandCategory { get; }
    string HelpText { get; }
    object[] ParseCommand(Living commandIssuer, string input);
    void DoCommand(Living commandIssuer, object[] commandArgs);
}
```

- **Registration:** implement `ICommand`, put the class in an assembly
  scanned by `World.LoadTypes` (currently `MUD.Core`, `MUD.PlanarRealms`).
  Scrutor registers it as a singleton; `CommandCategory` decides which
  `CommandSource` bucket it lands in.
- **Resolution order** (`Living.ResolveCommand`):
  1. room's `RoomCommandSource` (exits + room-attached commands)
  2. `Living.KnownCommands` (per-object string list, `CommandSource.GetSubset`)
  3. creator commands — if object has `AdminProperty` or `CreatorProperty`
  4. `DefaultCommands` (fallback)
  - NOTE: `World.AdminCommands` is built but currently **not consulted** by
    `ResolveCommand`.
- **Execution:** `World.DoCommand` → `CommandQueue` (one
  `ConcurrentQueue<CommandExecution>` per `Living`; serial per living via
  `IsBusy`) → drained by the world loop → `Task.Run`.
- `CommandSource`: keyword→command dictionary; `GetCommandFromInput` does
  prefix matching (keyword + space, or exact). `AnonymousCommand` wraps
  `Func` handlers (used for room exits).

## 2. Telnet transport (MUD.Telnet)

```csharp
public class Server
{
    public Server(IPAddress ipAddress, int port);
    public void Start();
    public void Stop();
    public event EventHandler ServerStopped;
    public event EventHandler<Client> ClientConnected;
    public event EventHandler<Client> ClientDisconnected;
    public void SendMessageToAll(string message);
    public void SendMessageToAll(byte[] data);
    public void SendMessageToAll(TelnetCommand telnetCommand);
}

public class Client
{
    public Client(Socket workSocket);
    public IPEndPoint RemoteAddress { get; }
    public const int BufferSize = 1024;
    public bool IsRemoteEcho { get; set; }
    public bool IsEchoSupressed { get; set; }
    public event EventHandler<Client> ClientDisconnected;
    public event EventHandler<String> DataReceived;   // assembled lines
    public void Send(string data);
    public void Send(byte[] data);
    public void Send(TelnetCommand command);
    public void Read();
    public void Disconnect();
}
```

- Raw TCP, blocking accept loop on the server thread; async
  `BeginRead`/`BeginSend` per client. IAC state machine
  (`TelnetCommand`, `CommandCode`, `OptionCode`) with partial
  TerminalType subnegotiation.
- Known defect: `Send(string)` line-ending normalization is a no-op
  (`data.Replace(...)` result discarded).

## 3. Persistence (MUD.Data)

```csharp
[BsonIgnoreExtraElements(Inherited = true)]
public abstract class Entity
{
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; }
}

public interface IRepository<TModel> where TModel : Entity
{
    void Insert(TModel model);
    bool Update(TModel model);        // ReplaceOne; false if nothing modified
    bool Delete(TModel model);
    TModel Get(string id);
    IEnumerable<TModel> GetAll();
    IEnumerable<TModel> Search(Expression<Func<TModel, bool>> matchExpression);
}
```

- Extend `Repository<TModel>` (abstract, DI-registered singleton) and
  register a marker interface (pattern: `IPlayerRepository :
  IRepository<Player>`) so game code can depend on it.
- Objects are instantiated by the Mongo driver through DI constructors
  (BsonClassMap with DI creators, managed by `MongoDBClassMapManager`).
- Current collections: `player`, `room` in db `testmud`.
- Thread-safety: **none** (in-memory cache + `TODO`).

## 4. Output (MUD.Core/Formatting)

```csharp
public interface ITerminalHandler
{
    string TerminalName { get; }
    string[] Aliases { get; }
    string GetReplacement(EFormatOptions option);
    string ResolveOutput(string outputString);   // default interface method
}
```

- Markup: `%^Option%^text%^Reset%^`; `ResolveOutput` substitutes every
  `EFormatOptions` token via `GetReplacement`.
- Implementations: `DefaultTerminal`, `AnsiTerminal` (256-color),
  `GMudTerminal`. Selected per player with the `term` command.
- `EFormatOptions` enum defines the option set (colors etc.).

## 5. Entity extension points (MUD.Core)

- **GameObject** (abstract): `ShortDescription`, `LongDescription`,
  `Plurals`, `Adjectives`, `Handles`, `ExtendedProperties`;
  `Examine()`, `IsMatchForString`, `IsPluralMatchForString`.
- **Living**: `Gender`, `Race`, `Class`, `KnownCommands`, `CurrentLocationId`,
  `Inventory`/`WornItems`/`HeldItems`, `IsBusy`;
  `ReceiveMessage`, `DoHeartbeat`, `MatchObjects(input, includeInventory,
  includeRoomItems, includeRoomOccupants, includeRoomFeatures)`,
  `MoveToRoom`, `ResolveCommand`.
- **Player** adds: `PlayerName`, `Password` (plaintext), `Client`,
  `SelectedTerm`, `Save()`, `Quit()`.
- **Room** adds: `Items`, `Features`, `Exits`, `Occupants` (runtime),
  `RoomCommandSource` (directions built as `AnonymousCommand`s),
  `TellRoom`, `EnterRoom`/`ExitRoom`, `Examine(examiner)`.
- **Capability interfaces** for `ExtendedProperty` objects:
  `IHeartbeat` (ticked in `Living.DoHeartbeat`), `ISearchable`,
  `IExaminable`, `IUpdatable`. Properties are `Entity` subclasses —
  persisted and polymorphic; attach via creator `apply`.

## 6. World (MUD.Core/World) — orchestrator API

```csharp
public class World
{
    // command sources (populated by DI scan)
    public CommandSource AllCommands { get; }
    public CommandSource DefaultCommands { get; }
    public CommandSource CreatorCommands { get; }
    public CommandSource AdminCommands { get; }   // currently unused in resolution

    public void Start();                       // heartbeat timer + command loop
    public void AddPlayer(Player player);      // broadcasts join
    public void RemovePlayer(Player player);   // broadcasts leave
    public Room GetRoom(string id);            // via IRoomRepository
    // DoLogin / DoCreateUser: plaintext password compare
    // new-player defaults: start room 5f6e27f20c1fdd24b4b18b1a,
    //   race "human", terminal "Default"
}
```
