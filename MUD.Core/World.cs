using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MongoDB.Driver;
using MUD.Core.Commands;
using MUD.Core.Formatting;
using Microsoft.Extensions.DependencyInjection;
using MUD.Telnet;
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

        private IServiceProvider _serviceProvider;

        private Timer _heartbeatTimer;

        private bool _isRunning;

        private CommandQueue _commandQueue;

        public World(IMongoClient dbClient)
        {
            _serviceProvider = LoadTypes(dbClient);

            List<ICommand> loadedCommands = _serviceProvider.GetServices<ICommand>().ToList();
            AllCommands = new CommandSource(loadedCommands.ToList());
            DefaultCommands = new CommandSource(loadedCommands.Where(c => c.CommandCategory == CommandCategories.Default).ToList());
            CreatorCommands = new CommandSource(loadedCommands.Where(c => c.CommandCategory == CommandCategories.Creator).ToList());
            AdminCommands = new CommandSource(loadedCommands.Where(c => c.CommandCategory == CommandCategories.Admin).ToList());

            TerminalHandlers = _serviceProvider.GetServices<ITerminalHandler>().ToList();

            _commandQueue = _serviceProvider.GetService<CommandQueue>();

            Players = new List<Player>();
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
                    var loadedAssembly = System.Reflection.Assembly.LoadFrom(currentDirectory + "\\" + currentAssembly);
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
                .AddSingleton<IMongoDatabase>(dbClient.GetDatabase("testmud"))

                // Load utility stuff?
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

        public void Start()
        {
            var startTimeSpan = TimeSpan.Zero;
            var periodTimeSpan = TimeSpan.FromSeconds(2);

            _heartbeatTimer = new System.Threading.Timer(new TimerCallback(doHeartbeat), null, startTimeSpan, periodTimeSpan);

            _isRunning = true;

            while (_isRunning)
            {
                _commandQueue.ExecuteAllCommands();
            }
        }

        public void Stop()
        {
            _heartbeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _isRunning = false;
        }

        public void AddPlayer(Player newPlayer)
        {
            if (newPlayer == null) { return; }
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

            var playerRepository = _serviceProvider.GetService<IPlayerRepository>();
            var foundPlayer = playerRepository.Search(p => p.PlayerName.ToLower().Contains(userName)).FirstOrDefault();

            if (foundPlayer != null)
            {
                if (foundPlayer.Password == password)
                {
                    foundPlayer.Connection = client;
                    foundPlayer.ConnectionStatus = PlayerConnectionStatuses.LoggedIn;
                    AddPlayer(foundPlayer);
                    return true;
                }
            }
            return false;
        }

        public bool DoCreateUser(string userName, string password, Client connectingClient)
        {
            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password)) { return false; }

            var playerRepository = _serviceProvider.GetService<IPlayerRepository>();
            var foundUser = playerRepository.Search(p => p.PlayerName.ToLower().Contains(userName)).FirstOrDefault();

            if (foundUser == null)
            {
                Player newPlayer = _serviceProvider.GetService<Player>();

                newPlayer.PlayerName = userName;
                newPlayer.ShortDescription = userName;
                newPlayer.Password = password;
                newPlayer.Race = "human";
                newPlayer.CurrentLocationId = "5f6e27f20c1fdd24b4b18b1a";
                newPlayer.SelectedTerm = "Default"; // Or just do the subnegotiation to get a value for this...
                newPlayer.ConnectionStatus = PlayerConnectionStatuses.LoggedIn;
                newPlayer.Connection = connectingClient;

                AddPlayer(newPlayer);
                return true;
            }
            return false;
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
            // Do heartbeat updates.
            foreach (Player currentPlayer in Players)
            {
                currentPlayer.DoHeartbeat();
            }
        }
    }
}