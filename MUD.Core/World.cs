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
using MUD.Core.Repositories;
using MUD.Core.GameObjects;

namespace MUD.Core
{
    public class World
    {
        public List<Player> Players;

        // public List<Room> Rooms;

        public CommandSource AllCommands;

        public CommandSource DefaultCommands;

        public List<ITerminalHandler> TerminalHandlers;

        private IServiceProvider _serviceProvider;

        private Timer _heartbeatTimer;

        private bool _isRunning;


        private CommandQueue _commandQueue;

        public World(IMongoClient dbClient)
        {
            //setup our DI
            _serviceProvider = new ServiceCollection()
                // Load all commands 
                .Scan(scan => scan
                    //.FromCallingAssembly()
                    //                    .FromAssemblies(new System.Reflection.Assembly[] {})
                    .FromApplicationDependencies()
                    .AddClasses(classes => classes.AssignableTo<ICommand>().Where(t => t.Name != "AnonymousCommand"))
                    .AsImplementedInterfaces()
                    .WithSingletonLifetime()
                )
                // Load all terminals
                .Scan(scan => scan
                    .FromApplicationDependencies()
                    .AddClasses(classes => classes.AssignableTo<ITerminalHandler>())
                    .AsImplementedInterfaces()
                    .WithSingletonLifetime()
                )
                // Load up the mongo client
                .AddSingleton<IMongoClient>(dbClient)
                .AddSingleton<IMongoDatabase>(dbClient.GetDatabase("testmud"))
                // Load utility stuff?
                .AddSingleton<CommandQueue>()

                // Load GameObject types
                //.AddTransient<GameObject>() // No this is an abstract base class. We need to register it with mongo, but whatever.
                .AddTransient<Player>()
                .AddTransient<Creator>()
                .AddTransient<Admin>()
                .AddTransient<Room>()

                // Load repositories
                .AddSingleton<IPlayerRepository, PlayerRepository>()
                .AddSingleton<IRoomRepository, RoomRepository>()

                // Load this and build.
                .AddSingleton<World>(this)
                .BuildServiceProvider();

            // Make sure the classMaps are properly registered.
            var classMapManager = new MUD.Data.MongoDBClassMapManager(_serviceProvider);
            classMapManager.ClearClassMaps();
            classMapManager.RegisterClassMap(typeof(GameObject));
            classMapManager.RegisterClassMap(typeof(Player));
            classMapManager.RegisterClassMap(typeof(Creator));
            classMapManager.RegisterClassMap(typeof(Admin));
            classMapManager.RegisterClassMap(typeof(Room));

            List<ICommand> loadedCommands = _serviceProvider.GetServices<ICommand>().ToList();
            AllCommands = new CommandSource(loadedCommands.ToList());
            DefaultCommands = new CommandSource(loadedCommands.Where(c => c.IsDefault).ToList());
            TerminalHandlers = _serviceProvider.GetServices<ITerminalHandler>().ToList();
            _commandQueue = _serviceProvider.GetService<CommandQueue>();

            Players = new List<Player>();
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
                    foundPlayer.ConnectionStatus = EPlayerConnectionStatus.LoggedIn;
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
                Player newPlayer = _serviceProvider.GetService<Player>(); // new Player(this, playerProvider, new PlayerEntity()) // Swap out for DI instead.

                newPlayer.PlayerName = userName;
                newPlayer.Password = password;
                newPlayer.Race = "human";
                newPlayer.CurrentLocationId = "5f6e27f20c1fdd24b4b18b1a";
                newPlayer.SelectedTerm = "Default"; // Or just do the subnegotiation to get a value for this...
                newPlayer.ConnectionStatus = EPlayerConnectionStatus.LoggedIn;
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

        public void LoadGameObjectType(Type objectType)
        {
            var classMapManager = new MUD.Data.MongoDBClassMapManager(_serviceProvider);
            classMapManager.RegisterClassMap(objectType);
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