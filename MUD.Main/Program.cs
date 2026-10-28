using System;
using System.Net;
using Microsoft.Extensions.Configuration;
using MUD.Telnet;
using MUD.CoreApi;
using MUD.Core.Sessions;
using MongoDB.Bson;
using MongoDB.Driver;
using MUD.Core;
using System.Linq;
using System.Threading;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using MUD.Core.Formatting;
using MongoDB.Bson.Serialization;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson.Serialization.IdGenerators;
using MUD.Core.Commands;

namespace MUD.Main
{
    class Program
    {
        private static World _gameWorld;
        private static Server _serverInstance;
        private static TelnetLimits _telnetLimits = TelnetLimits.FromEnvironment();
        private static readonly ConditionalWeakTable<IClientConnection, LoginSession> _loginSessions = new ConditionalWeakTable<IClientConnection, LoginSession>();
        // The core session id of each telnet client; core generates the ids.
        private static readonly ConditionalWeakTable<Client, object> _sessionIds = new ConditionalWeakTable<Client, object>();
        private static LoginThrottle _loginThrottle = new LoginThrottle(_telnetLimits.LoginMaxFailures, _telnetLimits.LoginThrottleDuration);
        private static string alternateMessage =
@"


          ______      _ _         ___        ______          _         
          | ___ \    | | |       / _ \       | ___ \        | |        
          | |_/ /   _| | |______/ /_\ \______| |_/ /_ _ _ __| |_ _   _ 
          |  __/ | | | | |______|  _  |______|  __/ _` | '__| __| | | |
          | |  | |_| | | |      | | | |      | | | (_| | |  | |_| |_| |
          \_|   \__,_|_|_|      \_| |_/      \_|  \__,_|_|   \__|\__, |
                                                                  __/ |
                                                                 |___/ 

--------------------------------------------------------------------------------
    Use 'connect' to log in to an existing player. You will be prompted for a
    name and password; the password is not echoed.
    Use 'create' to create a new player (name, password and confirmation).
    Scripted clients may use 'connect <username> <password>' and
    'create <username> <password>', but then the password is visible.
    Use 'who' to see who is currently online.
    Use 'quit' to disconnect.
--------------------------------------------------------------------------------";
        private static string welcomeMessage =
@"
                          Welcome to Planar Realms 0.2
                        _____  _                         
                        |  __ \| |                        
                        | |__) | | __ _ _ __   __ _ _ __  
                        |  ___/| |/ _` | '_ \ / _` | '__| 
                        | |    | | (_| | | | | (_| | |    
                  />___|_|___ |_|\__,_|_|_|_|\__,_|_|__________    
         [#########[]____________________________________________\
                   \>   |  __ \          | |              
                        | |__) |___  __ _| |_ __ ___  ___ 
                        |  _  // _ \/ _` | | '_ ` _ \/ __|
                        | | \ \  __/ (_| | | | | | | \__ \
                        |_|  \_\___|\__,_|_|_| |_| |_|___/
                                                                                |
--------------------------------------------------------------------------------
    Use 'connect' to log in to an existing player. You will be prompted for a
    name and password; the password is not echoed.
    Use 'create' to create a new player (name, password and confirmation).
    Scripted clients may use 'connect <username> <password>' and
    'create <username> <password>', but then the password is visible.
    Use 'who' to see who is currently online.
    Use 'quit' to disconnect.
--------------------------------------------------------------------------------";

        private static ShutdownCoordinator _shutdown;

        private static readonly ManualResetEventSlim _shutdownRequested = new ManualResetEventSlim(false);

        // Runs the shutdown sequence off the signal thread, then exits the process. Used by the signal handlers.
        private static void shutdownAndExit(string reason)
        {
            Console.WriteLine("SHUTDOWN requested: " + reason);
            _shutdownRequested.Set();
            System.Threading.Tasks.Task.Run(() =>
            {
                if (_shutdown != null) { _shutdown.Run(); }
                Environment.Exit(0);
            });
        }

        static void Main(string ip = null, int? port = null)
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true)
                .Build();

            // The connection string is a secret: it is only ever read from the environment.
            var mongoUri = Environment.GetEnvironmentVariable("MUD_MONGO_URI");
            if (string.IsNullOrWhiteSpace(mongoUri))
            {
                Console.Error.WriteLine("MUD_MONGO_URI is not set. Set it to the MongoDB connection string (for the systemd service it comes from ~/.config/mud/mongo.env).");
                Environment.Exit(1);
                return;
            }
            var databaseName = config["WorldSettings:DatabaseName"];
            if (string.IsNullOrWhiteSpace(databaseName)) { databaseName = "testmud"; }

            // Bind address and port: command line, then environment, then appsettings.json.
            if (string.IsNullOrWhiteSpace(ip)) { ip = Environment.GetEnvironmentVariable("MUD_BIND_ADDRESS"); }
            if (string.IsNullOrWhiteSpace(ip)) { ip = config["TelnetSettings:IPAddress"]; }
            if (port == null)
            {
                int parsedPort;
                if (int.TryParse(Environment.GetEnvironmentVariable("MUD_PORT"), out parsedPort)) { port = parsedPort; }
                else if (int.TryParse(config["TelnetSettings:Port"], out parsedPort)) { port = parsedPort; }
                else { port = 7680; }
            }
            var startRoomKey = config["WorldSettings:StartRoomKey"];
            if (string.IsNullOrWhiteSpace(startRoomKey))
            {
                Console.WriteLine("WorldSettings:StartRoomKey is not configured in appsettings.json!");
                return;
            }

            Console.Write("Checking databases...");
            MongoClient dbClient = null;

            try
            {
                dbClient = new MongoClient(mongoUri);
                // //setup our DI
                // IServiceProvider _serviceProvider = new ServiceCollection()
                //     // Load all commands 
                //     .Scan(scan => scan
                //         //.FromCallingAssembly()
                //         //                    .FromAssemblies(new System.Reflection.Assembly[] {})
                //         .FromApplicationDependencies()
                //         .AddClasses(classes => classes.AssignableTo<ICommand>().Where(t => t.Name != "AnonymousCommand"))
                //         .AsImplementedInterfaces()
                //         .WithSingletonLifetime()
                //     )
                //     // Load all terminals
                //     .Scan(scan => scan
                //         .FromApplicationDependencies()
                //         .AddClasses(classes => classes.AssignableTo<ITerminalHandler>())
                //         .AsImplementedInterfaces()
                //         .WithSingletonLifetime()
                //     )
                //     // Load up the mongo client
                //     .AddSingleton<IMongoClient>(dbClient)                
                //     .AddSingleton<IMongoDatabase>(dbClient.GetDatabase("testmud"))
                //     // Load utility stuff?
                //     .AddSingleton<CommandQueue>()
                //     .AddTransient<InventoryItem>()
                //     .AddTransient<Wand>()
                //     .AddTransient<Food>()
                //     .AddTransient<Player>()
                //     .AddTransient<World>()
                //     .BuildServiceProvider();

                // Apparently SetIsRootClass can't be set after the ClassMap has been Frozen.
                // BsonClassMap.RegisterClassMap<Item>(cm =>
                // {
                //     cm.AutoMap();
                //     cm.SetIsRootClass(true);
                //     cm.MapIdProperty("Id").SetIdGenerator(StringObjectIdGenerator.Instance);
                //     cm.SetCreator(() => { return _serviceProvider.GetService<Item>(); });
                // });
                // BsonClassMap.RegisterClassMap<Wand>(cm =>
                // {
                //     cm.AutoMap();
                //     cm.SetCreator(() => { return _serviceProvider.GetService<Wand>(); });
                // });
                // BsonClassMap.RegisterClassMap<Food>(cm =>
                // {
                //     cm.AutoMap();
                //     cm.SetCreator(() => { return _serviceProvider.GetService<Food>(); });
                // });

                var database = dbClient.GetDatabase(databaseName); // This creates the database if it doesn't otherwise exist?
                if (database == null) { Console.WriteLine("Database not found!"); }
                var collection = database.GetCollection<BsonDocument>("player"); // Check that the collection is there. If it is not, then the collection exists in memory and is created on the actual DB when needed.
                var testFind = collection.AsQueryable().FirstOrDefault();
                foreach (var legacyCollection in new[] { "room", "player" })
                {
                    var count = MUD.Data.ObjectIdMigration.MigrateCollection(database.GetCollection<BsonDocument>(legacyCollection), Console.WriteLine);
                    if (count > 0) { Console.WriteLine(string.Format("Migrated {0} legacy ObjectId documents in '{1}'.", count, legacyCollection)); }
                }
                // collection = database.GetCollection<BsonDocument>("rooms"); // Check that the collection is there.
                // collection.InsertOne(BsonDocument.Parse(jsonContent)); // The BSON object will have _id and it will be set by the insert call.

                // var helper = new Data.MongoDBClassMapManager(_serviceProvider);
                // helper.ClearClassMaps();
                // helper.RegisterRootClassMap(typeof(Data.Entity));
                // helper.RegisterClassMap(typeof(GameObject));
                // helper.RegisterClassMap(typeof(InventoryItem));
                // helper.RegisterClassMap(typeof(Wand));
                // helper.RegisterClassMap(typeof(Food));
                // helper.RegisterClassMap(typeof(Player));

                // var itemCollection = database.GetCollection<InventoryItem>("item");

                // itemCollection.DeleteMany((m => true));

                // InventoryItem newItem = new InventoryItem(database)
                // {
                //     ShortDescription = "soft cloth",
                //     LongDescription = "This is a small, soft cloth. It is suitable for polishing things or blowing one's nose, but not much else."
                // };
                // newItem.Update();

                // Wand newWand = new Wand(database)
                // {
                //     ShortDescription = "wand of missiles",
                //     LongDescription = "This is a short, straight stick that has been polished and painted red. It has been imbued with magical power such that it will shoot magic missiles at one's foe.",
                //     CurrentCharges = 8,
                //     MaxCharges = 10
                // };
                // newWand.Update();

                // Food newFood = new Food(database)
                // {
                //     ShortDescription = "loaf of bread",
                //     LongDescription = "A toasty, scrumptious loaf of fresh bread.",
                //     Servings = 2
                // };
                // newFood.Update();

                // var item = itemCollection.Find<InventoryItem>((m => m.Id == newItem.Id)).FirstOrDefault();
                // var wand = (Wand)itemCollection.Find<InventoryItem>((m => m.Id == newWand.Id)).FirstOrDefault();
                // wand.UseWand();
                // wand.Update();
                // var food = itemCollection.Find<InventoryItem>((m => m.Id == newFood.Id)).FirstOrDefault();

                // var playerCollection = database.GetCollection<Player>("players");

                // var newPlayer = _serviceProvider.GetService<Player>();
                // newPlayer.PlayerName = "testplayer";
                // newPlayer.Race = "womble";
                // newPlayer.Class = "fighter";
                // newPlayer.Gender = "neuter";
                // newPlayer.Password = "password";

                // var foundPlayer = playerCollection.Find<Player>((m => m.PlayerName == "Holen")).FirstOrDefault();
                // foundPlayer.Update();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Could not connect to suitable database!");
                Console.WriteLine(ex);
                return;
            }
            Console.WriteLine("done.");


            Console.Write("Starting world...");
            var contentDirectory = config["WorldSettings:ContentDirectory"];
            if (!string.IsNullOrWhiteSpace(contentDirectory) && !System.IO.Path.IsPathRooted(contentDirectory))
            {
                contentDirectory = System.IO.Path.Combine(AppContext.BaseDirectory, contentDirectory);
            }
            bool importOnStartup;
            if (!bool.TryParse(config["WorldSettings:ImportOnStartup"], out importOnStartup)) { importOnStartup = true; }
            _gameWorld = new World(dbClient, new WorldSettings { StartRoomKey = startRoomKey, DatabaseName = databaseName, ContentDirectory = contentDirectory, ImportOnStartup = importOnStartup });
            try
            {
                _gameWorld.ImportContent();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Content import failed: " + ex.Message);
            }
            try
            {
                _gameWorld.PreparePlayerAccounts();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Player account preparation failed: " + ex.Message);
            }
            _gameWorld.EnableWebSync();
            _gameWorld.LoginThrottle = _loginThrottle;
            _gameWorld.PreLoginHandler = (sessionId, line) => { var connection = _gameWorld.GetSessionConnection(sessionId); if (connection != null) { messageReceived(connection, line); } };
            Thread gameWorldThread = new Thread(_gameWorld.Start);
            gameWorldThread.Start();
            Console.WriteLine("done.");

            Console.Write("Starting telnet server...");
            IPAddress ipAddress = null;
            if (!IPAddress.TryParse(ip, out ipAddress)) { ipAddress = IPAddress.Any; }

            _serverInstance = new Server(ipAddress, port.Value, _telnetLimits);
            _serverInstance.ClientConnected += clientConnected;
            _serverInstance.ClientDisconnected += clientDisconnected;

            Thread serverThread = new Thread(_serverInstance.Start);
            serverThread.Start();
            Console.WriteLine("done.");

            Console.WriteLine("SERVER STARTED: " + DateTime.Now);

            // Graceful shutdown: stop accepting, tell clients, save every online player through the persistence
            // worker and drain it, then close connections. Bounded: after 10 s it exits anyway and logs what
            // was not saved. The World's own drain deadline is shorter so it can spool before this one fires.
            _shutdown = new ShutdownCoordinator(
                () => _serverInstance.StopAccepting(),
                () => _serverInstance.SendMessageToAll("The server is shutting down."),
                () => _gameWorld.Stop(),
                () => _serverInstance.Stop(),
                () => _gameWorld.DescribeUnsaved(),
                Console.WriteLine);
            _gameWorld.ShutdownTimeout = _shutdown.Timeout - TimeSpan.FromSeconds(2);

            // SIGTERM (systemd stop), Ctrl+C / SIGINT, and process exit.
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; shutdownAndExit("SIGTERM"); });
            PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; shutdownAndExit("SIGINT"); });
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; shutdownAndExit("Ctrl+C"); };
            AppDomain.CurrentDomain.ProcessExit += (s, e) => _shutdown.Run();

            char read2 = ' ';
            bool isInteractive = !Console.IsInputRedirected;
            if (!isInteractive)
            {
                // Under systemd stdin is not a terminal: block until a shutdown is requested instead of spinning.
                _shutdownRequested.Wait();
                _shutdown.Run();
                return;
            }

            while (read2 != 'q')
            {
                if (read2 == 'b')
                {
                    _serverInstance.SendMessageToAll(Console.ReadLine());
                }

                if (read2 == 'c')
                {
                    string colorTest = "";
                    for (int i = 0; i < 256; i++)
                    {
                        colorTest += "\x1B[38;5;" + i + "m " + i.ToString("D3") + " \x1B[0m";
                        if (i == 15 || i == 231 || ((i > 15) && (i < 232) && (((i - 15) % 6) == 0)))
                        {
                            colorTest += "\r\n";
                        }
                    }
                    _serverInstance.SendMessageToAll(colorTest);

                    _serverInstance.SendMessageToAll("\x1B[32mGREEN\x1B[0m");
                    _serverInstance.SendMessageToAll("\x1B[92mBRIGHT GREEN\x1B[0m");
                    _serverInstance.SendMessageToAll("\x1B[1m\x1B[32mBOLD GREEN\x1B[0m");
                    _serverInstance.SendMessageToAll("\x1B[1m\x1B[92mBOLD BRIGHT GREEN\x1B[0m");
                    _serverInstance.SendMessageToAll("\x1B[42mGREEN BACKGROUND\x1B[0m");
                    _serverInstance.SendMessageToAll("\x1B[102mBRIGHT GREEN BACKGROUND\x1B[0m");
                    _serverInstance.SendMessageToAll("\x1B[1m\x1B[42mBOLD GREEN BACKGROUND\x1B[0m");
                    _serverInstance.SendMessageToAll("\x1B[1m\x1B[102mBOLD BRIGHT GREEN BACKGROUND\x1B[0m");
                }

                if (read2 == 't')
                {
                    _serverInstance.SendMessageToAll(new byte[] { (byte)CommandCode.IAC, (byte)CommandCode.DO, (byte)OptionCode.TerminalType });
                }

                if (isInteractive)
                {
                    try
                    {
                        read2 = Console.ReadKey(true).KeyChar;
                    }
                    catch (InvalidOperationException)
                    {
                        // Can't accept console input: wait for a shutdown signal instead.
                        _shutdownRequested.Wait();
                        break;
                    }
                }
            }

            _shutdown.Run();
        }

        private static void clientConnected(object sender, Client client)
        {
            Console.WriteLine("CONNECTED: " + client);

            SessionId sessionId = _gameWorld.OpenSession("telnet", client.RemoteKey, new TelnetSessionSink(client));
            _sessionIds.Add(client, sessionId);
            IClientConnection connection = _gameWorld.GetSessionConnection(sessionId);
            client.DataReceived += (s, line) => _gameWorld.SubmitLine(sessionId, line);
            client.Send(welcomeMessage);
            // Pre-login handling reads world state, so it runs on the game thread, after the welcome text.
            _gameWorld.Post(() =>
            {
                messageReceived(connection, "who");
                connection.Send("");
            });
        }

        private static void clientDisconnected(object sender, Client client)
        {
            object sessionId;
            if (_sessionIds.TryGetValue(client, out sessionId)) { _gameWorld.CloseSession((SessionId)sessionId, SessionEndReason.ClientClosed); }
            Console.WriteLine("DISCONNECTED: " + client);
        }

        private static void connectionBlocked(IPEndPoint ep)
        {
            Console.WriteLine(string.Format("BLOCKED: {0}:{1} at {2}", ep.Address, ep.Port, DateTime.Now));
        }

        private static void messageReceived(object sender, string message)
        {
            IClientConnection client = (IClientConnection)sender;
            message = message.TrimEnd('\n').TrimEnd('\r');

            LoginSession session = _loginSessions.GetValue(client, k => new LoginSession(k, new WorldLoginHandler(_gameWorld, k), _loginThrottle));

            // Never log a line that may contain a password.
            Console.WriteLine("MESSAGE: " + session.Loggable(message));

            if (session.State != LoginState.Idle)
            {
                session.HandleLine(message);
            }
            else if (message == "quit" || message.StartsWith("quit "))
            {
                client.Disconnect(false);
            }
            else if (message == "who" || message.StartsWith("who "))
            {
                // Show the list of logged in players
                if (_gameWorld.Players == null || _gameWorld.Players.Count == 0)
                {
                    client.Send("No one is logged on.");
                }
                else if (_gameWorld.Players.Count == 1)
                {
                    client.Send(String.Format("{0} is logged on.", _gameWorld.Players.First().PlayerName));
                }
                else
                {
                    client.Send(String.Format("{0} are logged on.", _gameWorld.Players.Select(p => p.PlayerName).GetListText()));
                }
            }
            else if (session.HandleLine(message))
            {
                // Consumed by the login flow.
            }
            else
            {
                string commandString = message;
                if (message.Contains(" "))
                {
                    message = message.Substring(0, message.IndexOf(" "));
                }
                client.Send(string.Format("Command {0} was not recognized.", commandString));
            }

            if (session.IsLoggedIn)
            {
                // Lines from now on go to the player (World routes by connection).
                var legacySink = ((SessionConnection)client).Sink as TelnetSessionSink;
                var telnetClient = legacySink == null ? null : legacySink.Connection as Client;
                if (telnetClient != null) { telnetClient.IsLoggedIn = true; }
            }
        }

        private class WorldLoginHandler : ILoginHandler
        {
            private readonly World _world;
            private readonly IClientConnection _client;

            public WorldLoginHandler(World world, IClientConnection client)
            {
                _world = world;
                _client = client;
            }

            public bool Login(string name, string password)
            {
                return _world.DoLogin(name, password, ((SessionConnection)_client).Id);
            }

            public bool Create(string name, string password)
            {
                return _world.DoCreateUser(name, password, ((SessionConnection)_client).Id);
            }
        }
    }
}