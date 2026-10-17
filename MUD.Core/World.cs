using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MongoDB.Driver;
using MUD.Core.Commands;
using MUD.Core.Formatting;
using Microsoft.Extensions.DependencyInjection;
using MUD.Telnet;
using MUD.Core.Repositories;
using MUD.Core.Repositories.Interfaces;
using MUD.Core.GameObjects;
using MUD.Data;
using System.IO;
using System.Reflection;

namespace MUD.Core
{
    public class World
    {
        public List<Player> Players;

        public CommandSource AllCommands;

        public CommandSource DefaultCommands;

        public CommandSource CreatorCommands;

        public CommandSource AdminCommands;

        public List<ITerminalHandler> TerminalHandlers;

        // Shared with the pre-login flow so wrong passwords typed into in-game commands count too. May be null.
        public LoginThrottle LoginThrottle { get; set; }

        private IServiceProvider _serviceProvider;

        private CommandQueue _commandQueue;

        private WorldSettings _settings;

        private IClock _clock;

        private GameLoop _loop;

        private PersistenceWorker _persistence;

        // Everything below is touched only on the game thread (specs/GAME_LOOP.md section 3).
        private Dictionary<IClientConnection, Player> _sessions;

        private HashSet<string> _finalSavesPending;

        private bool _shuttingDown;

        // Per-connection inbound accounting; touched from socket threads, so it is concurrent.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<IClientConnection, ClientInbox> _inboxes =
            new System.Collections.Concurrent.ConcurrentDictionary<IClientConnection, ClientInbox>();

        private readonly ManualResetEventSlim _stopped = new ManualResetEventSlim(false);

        private class ClientInbox
        {
            public int Pending;
            public int Dropped;
        }

        /// <summary>Max unprocessed lines queued per client; further lines are dropped (specs/GAME_LOOP.md Q4).</summary>
        public const int MaxQueuedLinesPerClient = 20;

        /// <summary>Dropped lines after which a client is disconnected for flooding.</summary>
        public const int MaxDroppedLinesBeforeDisconnect = 50;

        /// <summary>How long shutdown waits for the persistence worker before spooling what is left.</summary>
        public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>The time source used by the loop, command timing and autosave. Replaceable for tests.</summary>
        public IClock Clock { get { return _clock ?? SystemClock.Instance; } }

        /// <summary>
        /// Handles a line from a connection that has not logged in (connect / create / who ...). Set by the host.
        /// Runs on the game thread.
        /// </summary>
        public Action<IClientConnection, string> PreLoginHandler { get; set; }

        public World(IMongoClient dbClient, WorldSettings settings) : this(dbClient, settings, null)
        {
        }

        public World(IMongoClient dbClient, WorldSettings settings, IClock clock)
        {
            _settings = settings;
            _clock = clock ?? SystemClock.Instance;
            _sessions = new Dictionary<IClientConnection, Player>();
            _finalSavesPending = new HashSet<string>();
            _serviceProvider = LoadTypes(dbClient);

            List<ICommand> loadedCommands = _serviceProvider.GetServices<ICommand>().ToList();
            AllCommands = new CommandSource(loadedCommands.ToList());
            DefaultCommands = new CommandSource(loadedCommands.Where(c => c.CommandCategory == CommandCategories.Default).ToList());
            CreatorCommands = new CommandSource(loadedCommands.Where(c => c.CommandCategory == CommandCategories.Creator).ToList());
            AdminCommands = new CommandSource(loadedCommands.Where(c => c.CommandCategory == CommandCategories.Admin).ToList());

            TerminalHandlers = _serviceProvider.GetServices<ITerminalHandler>().ToList();

            _commandQueue = _serviceProvider.GetService<CommandQueue>();

            Players = new List<Player>();

            _loop = new GameLoop(Clock, handleInbound, onTick, () => doHeartbeat(null));
            _loop.OnFatal = beginShutdown;
        }

        public IServiceProvider LoadTypes(IMongoClient dbClient)
        {
            // Load assemblies
            var currentDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string[] libAssemblyNames = new string[] { "MUD.Core.dll", "MUD.PlanarRealms.dll" }; // Make this config value?
            List<Assembly> libAssemblies = new List<Assembly>();
            foreach (string currentAssembly in libAssemblyNames)
            {
                try
                {
                    var loadedAssembly = System.Reflection.Assembly.LoadFrom(Path.Combine(currentDirectory, currentAssembly));
                    if (loadedAssembly != null)
                    {
                        libAssemblies.Add(loadedAssembly);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(string.Format("Exception encountered when loading assemblies. Exception was:\r\n{0}", ex));
                }
            }

            //setup our DI
            IServiceProvider serviceProvider = new ServiceCollection()
                // Load all commands 
                .Scan(scan => scan
                    .FromAssemblies(libAssemblies)
                    .AddClasses(classes => classes.AssignableTo<ICommand>().Where(t => t.Name != "AnonymousCommand"))
                    .AsImplementedInterfaces()
                    .WithSingletonLifetime()
                )

                // Load all terminals
                .Scan(scan => scan
                    .FromAssemblies(libAssemblies)
                    .AddClasses(classes => classes.AssignableTo<ITerminalHandler>())
                    .AsImplementedInterfaces()
                    .WithSingletonLifetime()
                )

                // Load up the mongo client
                .AddSingleton<IMongoClient>(dbClient)
                .AddSingleton<WorldSettings>(_settings)
                .AddSingleton<IMongoDatabase>(dbClient.GetDatabase(_settings.DatabaseName))

                // Load utility stuff?
                .AddSingleton<IClock>(Clock)
                .AddSingleton<CommandQueue>()

                // Load repositories
                .Scan(scan => scan
                    .FromAssemblies(libAssemblies)
                    .AddClasses(classes => classes.AssignableTo(typeof(Repository<>)).Where(t => !t.IsGenericType))
                    .AsImplementedInterfaces()
                    .WithSingletonLifetime()
                )

                // Load Entity types
                .Scan(scan => scan
                    .FromAssemblies(libAssemblies)
                    .AddClasses(classes => classes.AssignableTo<Entity>().Where(t => !t.IsAbstract))
                    .AsSelf()
                    .WithTransientLifetime()
                )

                // Load this and build.
                .AddSingleton<World>(this)

                .BuildServiceProvider();

            // Make sure the classMaps are properly registered.
            // TODO: Do assembly scans here.
            var classMapManager = new MUD.Data.MongoDBClassMapManager(serviceProvider);
            classMapManager.ClearClassMaps();

            // Add Things that go to the DB.
            classMapManager.RegisterEntityClasses(libAssemblies);

            return serviceProvider;
        }

        /// <summary>
        /// Runs the game on the calling thread until Stop: starts the persistence worker, then loops. Everything
        /// that changes the world happens on this thread. Returns after shutdown has finished.
        /// </summary>
        public void Start()
        {
            Thread persistenceThread = startPersistence();
            try
            {
                _loop.Run();
            }
            finally
            {
                // Normally a no-op: shutdown has already run. Covers a loop that stopped some other way.
                if (!_shuttingDown) { beginShutdown(); }
                if (persistenceThread != null && !persistenceThread.Join(ShutdownTimeout + TimeSpan.FromSeconds(5)))
                {
                    Console.WriteLine("The persistence worker did not finish before the shutdown deadline.");
                }
                _stopped.Set();
            }
        }

        /// <summary>
        /// Asks the game thread to log everyone out, flush persistence and exit, and waits (bounded) for it.
        /// Safe to call from any thread, and repeated calls are ignored.
        /// </summary>
        public void Stop()
        {
            if (_loop == null) { return; }
            Post(beginShutdown);
            _stopped.Wait(ShutdownTimeout + TimeSpan.FromSeconds(10));
        }

        /// <summary>Queues a callback to run on the game thread. Safe from any thread; never blocks.</summary>
        public bool Post(Action action)
        {
            return _loop != null && _loop.Post(InboundItem.ForEvent(action));
        }

        /// <summary>
        /// Called on socket threads with each decoded line. Only counts and enqueues; the line is handled on the
        /// game thread. A client that is more than MaxQueuedLinesPerClient behind has lines dropped, and is
        /// disconnected if it keeps doing it.
        /// </summary>
        public void EnqueueLine(IClientConnection client, string line)
        {
            if (_loop == null) { return; }
            ClientInbox inbox = _inboxes.GetOrAdd(client, c => new ClientInbox());
            if (Interlocked.Increment(ref inbox.Pending) > MaxQueuedLinesPerClient)
            {
                Interlocked.Decrement(ref inbox.Pending);
                if (Interlocked.Increment(ref inbox.Dropped) >= MaxDroppedLinesBeforeDisconnect)
                {
                    client.DisconnectWithMessage("You are sending too much input. Goodbye.");
                }
                else
                {
                    client.Send("Too much input; that line was ignored.");
                }
                return;
            }
            if (!_loop.Post(InboundItem.ForLine(client, line))) { Interlocked.Decrement(ref inbox.Pending); }
        }

        /// <summary>Called on socket threads when a connection closes, for any reason.</summary>
        public void EnqueueDisconnect(IClientConnection client)
        {
            if (_loop != null) { _loop.Post(InboundItem.ForDisconnected(client)); }
        }

        private Thread startPersistence()
        {
            var players = _serviceProvider.GetService<IPlayerRepository>() as ISnapshotRepository;
            var rooms = _serviceProvider.GetService<IRoomRepository>() as ISnapshotRepository;
            var byCollection = new Dictionary<string, ISnapshotRepository>();
            if (players != null) { byCollection[players.CollectionName] = players; }
            if (rooms != null) { byCollection[rooms.CollectionName] = rooms; }

            var writer = new RepositoryPersistWriter(name => byCollection.TryGetValue(name, out ISnapshotRepository r) ? r : null);
            _persistence = new PersistenceWorker(writer, Clock, action => Post(action), new SpoolStore(SpoolDirectory));
            int replayed = _persistence.ReplaySpool();
            if (replayed > 0) { Console.WriteLine("Replaying " + replayed + " unsaved snapshot(s) spooled by an earlier run."); }

            // From here on, saves are snapshots handed to the worker. Before this point (startup import, account
            // preparation) repositories are written synchronously, as before.
            foreach (var repository in byCollection.Values) { repository.Sink = _persistence; }

            var thread = new Thread(_persistence.Run) { Name = "persistence" };
            thread.Start();
            return thread;
        }

        /// <summary>Where snapshots that could not be written at shutdown are kept (~/.local/share/mud/spool).</summary>
        public string SpoolDirectory
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_settings != null ? _settings.SpoolDirectory : null)) { return _settings.SpoolDirectory; }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mud", "spool");
            }
        }

        // Shutdown sequence (specs/GAME_LOOP.md section 9), on the game thread: log every player out (final
        // snapshots posted), tell the persistence worker to drain, and stop the loop.
        private void beginShutdown()
        {
            if (_shuttingDown) { return; }
            _shuttingDown = true;

            foreach (Player player in Players.ToList())
            {
                try
                {
                    player.ReceiveMessage("The server is shutting down.");
                    player.Quit();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(string.Format("Exception logging out {0} at shutdown: {1}", player.PlayerName, ex));
                }
            }

            if (_persistence != null) { _persistence.Shutdown(ShutdownTimeout); }
            _loop.Stop();
        }

        private void onTick()
        {
            if (_commandQueue != null) { _commandQueue.Dispatch(); }
        }

        // Routes one inbound item. Game thread only.
        private void handleInbound(InboundItem item)
        {
            switch (item.Kind)
            {
                case InboundKind.Line:
                    {
                        ClientInbox inbox;
                        if (_inboxes.TryGetValue(item.Client, out inbox)) { Interlocked.Decrement(ref inbox.Pending); }
                        if (_shuttingDown) { return; }

                        Player player;
                        if (_sessions.TryGetValue(item.Client, out player))
                        {
                            // After logout has started, further input from that client is ignored.
                            if (player.ConnectionStatus == PlayerConnectionStatuses.Offline) { return; }
                            player.DataReceivedHandler(item.Client, item.Line);
                        }
                        else if (PreLoginHandler != null)
                        {
                            PreLoginHandler(item.Client, item.Line);
                        }
                        break;
                    }
                case InboundKind.Disconnected:
                    {
                        ClientInbox ignored;
                        _inboxes.TryRemove(item.Client, out ignored);
                        Player player;
                        if (_sessions.TryGetValue(item.Client, out player))
                        {
                            _sessions.Remove(item.Client);
                            // Current behavior preserved: losing the connection logs the player out.
                            player.Quit();
                        }
                        break;
                    }
                case InboundKind.WorldEvent:
                    item.Action();
                    break;
            }
        }

        /// <summary>Records that a player's final save is queued; their account cannot log in until it completes.</summary>
        public void BeginFinalSave(Player player, PersistRequest request)
        {
            string id = player.Id;
            _finalSavesPending.Add(id);
            var snapshots = _serviceProvider != null ? _serviceProvider.GetService<IPlayerRepository>() as ISnapshotRepository : null;
            Action<bool> previous = request.OnCompleted;
            request.OnCompleted = persisted =>
            {
                if (previous != null) { previous(persisted); }
                _finalSavesPending.Remove(id);
                // The saved snapshot is now the truth; drop the stale live object so a re-login reloads it.
                if (snapshots != null) { snapshots.Evict(id); }
            };
        }

        /// <summary>True while a logout save for this player id has been queued but not yet written.</summary>
        public bool IsFinalSavePending(string playerId)
        {
            return _finalSavesPending != null && _finalSavesPending.Contains(playerId);
        }

        /// <summary>
        /// Called by Player.Quit: the connection no longer maps to a player, so a later Disconnected item for it
        /// does not run the logout a second time.
        /// </summary>
        public void RetireConnection(IClientConnection connection)
        {
            if (connection != null && _sessions != null) { _sessions.Remove(connection); }
        }

        public void AddPlayer(Player newPlayer)
        {
            if (newPlayer == null) { return; }
            if (newPlayer.Connection != null) { _sessions[newPlayer.Connection] = newPlayer; }
            Players.Add(newPlayer);
            foreach (var currentPlayer in Players)
            {
                currentPlayer.ReceiveMessage(String.Format("[%^Green%^{0} enters Planar Realms%^Reset%^]", newPlayer.PlayerName));
            }
            if (newPlayer.CurrentLocation != null)
            {
                newPlayer.CurrentLocation.EnterRoom(newPlayer, string.Format("{0} enters the game.", newPlayer.PlayerName));
            }
            newPlayer.DataReceivedHandler(null, "look");
        }

        public void RemovePlayer(Player player)
        {
            if (Players.Remove(player))
            {
                if (player.CurrentLocation != null)
                {
                    player.CurrentLocation.ExitRoom(player, string.Format("{0} leaves the game.", player.PlayerName));
                }
                foreach (var currentPlayer in Players)
                {
                    currentPlayer.ReceiveMessage(String.Format("[%^Green%^{0} left Planar Realms%^Reset%^]", player.PlayerName));
                }
            }
        }

        public bool DoLogin(string userName, string password, Client client)
        {
            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password)) { return false; }

            var foundPlayer = GetPlayerAccounts().Authenticate(userName, password);

            if (foundPlayer != null && IsFinalSavePending(foundPlayer.Id))
            {
                // The previous session's logout save is still queued; logging in now could load stale data.
                client.Send("Your previous session is still being saved, try again in a moment.");
                return false;
            }

            if (foundPlayer != null)
            {
                foundPlayer.Connection = client;
                foundPlayer.ConnectionStatus = PlayerConnectionStatuses.LoggedIn;
                AddPlayer(foundPlayer);
                return true;
            }
            return false;
        }

        public bool DoCreateUser(string userName, string password, Client connectingClient)
        {
            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password)) { return false; }

            // Every failure is reported to the client here; the caller only needs to know it did not succeed.
            var accounts = GetPlayerAccounts();
            userName = PlayerAccounts.Clean(userName);

            string nameProblem = PlayerAccounts.ValidateName(userName);
            if (nameProblem != null)
            {
                connectingClient.Send(nameProblem);
                return false;
            }

            if (accounts.FindByName(userName) == null)
            {
                Player newPlayer = _serviceProvider.GetService<Player>();

                newPlayer.PlayerName = userName;
                newPlayer.NameKey = PlayerAccounts.Normalize(userName);
                newPlayer.ShortDescription = userName;
                newPlayer.Password = password;
                newPlayer.Race = "human";
                newPlayer.CurrentLocationId = _settings.StartRoomKey;
                newPlayer.SelectedTerm = "Default"; // Or just do the subnegotiation to get a value for this...
                newPlayer.ConnectionStatus = PlayerConnectionStatuses.LoggedIn;
                newPlayer.Connection = connectingClient;

                // The unique index decides under concurrent creates: a duplicate-key error on save means the name is taken.
                var saved = accounts.TrySave(newPlayer);
                if (saved == PlayerSaveResult.NameTaken)
                {
                    connectingClient.Send(string.Format("There is already someone by the name of {0}.", userName));
                    return false;
                }
                if (saved != PlayerSaveResult.Saved)
                {
                    connectingClient.Send("Unable to create your character right now. Please try again later.\r\n");
                    return false;
                }

                AddPlayer(newPlayer);
                return true;
            }
            connectingClient.Send(string.Format("There is already someone by the name of {0}.", userName));
            return false;
        }

        // Backfills Player.NameKey for stored players and creates the unique NameKey index. Never throws:
        // problems are logged and startup continues (without the index if it cannot be created).
        public void PreparePlayerAccounts()
        {
            GetPlayerAccounts().Initialize();
        }

        private PlayerAccounts GetPlayerAccounts()
        {
            return new PlayerAccounts(_serviceProvider.GetService<IPlayerRepository>(), Console.WriteLine);
        }

        // Imports every area file in the configured content directory (no-op when none is configured).
        public void ImportContent()
        {
            if (string.IsNullOrWhiteSpace(_settings.ContentDirectory) || !_settings.ImportOnStartup) { return; }

            var store = new MUD.Core.Import.RoomRepositoryImportStore(_serviceProvider.GetService<IRoomRepository>());
            var service = new MUD.Core.Import.AreaFileService(new MUD.Core.Import.AreaImporter(store), _settings.ContentDirectory);
            foreach (var line in service.ImportAll())
            {
                Console.WriteLine(line);
            }
        }

        public Room GetRoom(string roomId)
        {
            if (string.IsNullOrWhiteSpace(roomId)) { return null; }

            var roomRepository = _serviceProvider.GetService<IRoomRepository>();
            return roomRepository.Get(roomId);
        }

        // Might want this on the living object or whatever is needing a periodic update like this - like for HP regen, etc. Doesn't need to be centralized necessarily.
        private void doHeartbeat(object state)
        {
            // Do heartbeat updates. Iterate a snapshot so players added or removed during the tick
            // cannot break enumeration, and catch per player so one failure cannot stop the timer.
            foreach (Player currentPlayer in Players.ToList())
            {
                try
                {
                    currentPlayer.DoHeartbeat();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(string.Format("Exception encountered during heartbeat for {0}: {1}", currentPlayer.PlayerName, ex));
                }
            }
        }
    }
}