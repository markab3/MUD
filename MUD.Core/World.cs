using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using MongoDB.Driver;
using MUD.Core.Commands;
using MUD.Core.Formatting;
using MUD.Core.Repositories;
using MUD.Core.Repositories.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using MUD.Telnet;
using MUD.Core.Entities;

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

        private List<Room> _rooms;

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
                .Scan(scan => scan
                    .FromApplicationDependencies()
                    .AddClasses(classes => classes.AssignableTo<ITerminalHandler>())
                    .AsImplementedInterfaces()
                    .WithSingletonLifetime()
                )
                .AddSingleton<IMongoClient>(dbClient)
                .AddSingleton<IPlayerRepository, PlayerRepository>()
                .AddSingleton<IRoomRepository, RoomRepository>()
                .AddSingleton<CommandQueue>()
                .AddTransient<Player>()
                .AddTransient<Room>()
                .AddSingleton<World>(this)
                .BuildServiceProvider();

            List<ICommand> loadedCommands = _serviceProvider.GetServices<ICommand>().ToList();
            AllCommands = new CommandSource(loadedCommands.ToList());
            DefaultCommands = new CommandSource(loadedCommands.Where(c => c.IsDefault).ToList());
            TerminalHandlers = _serviceProvider.GetServices<ITerminalHandler>().ToList();
            _commandQueue = _serviceProvider.GetService<CommandQueue>();

            Players = new List<Player>();
            _rooms = new List<Room>();
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
            var foundPlayerEntity = playerRepository.Search(p => p.PlayerName.ToLower().Contains(userName)).FirstOrDefault();

            if (foundPlayerEntity != null)
            {
                if (foundPlayerEntity.Password == password)
                {
                    Player foundPlayer = _serviceProvider.GetService<Player>(); // new Player(this, playerRepository, foundUser);
                    foundPlayer.LoadEntity(foundPlayerEntity);
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
                Player newPlayer = _serviceProvider.GetService<Player>(); // new Player(this, playerRepository, new PlayerEntity()) // Swap out for DI instead.

                newPlayer.PlayerName = userName;
                newPlayer.Password = password;
                newPlayer.Race = "human";
                newPlayer.CurrentLocation_id = "5f6e27f20c1fdd24b4b18b1a";
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

            Room foundRoom = _rooms.FirstOrDefault(r => r._id == roomId);

            if (foundRoom != null) { return foundRoom; }

            var roomRepository = (IRoomRepository)_serviceProvider.GetService(typeof(IRoomRepository));
            var roomEntity = roomRepository.Get(roomId);

            if (roomEntity == null) { return null; }

            foundRoom = _serviceProvider.GetService<Room>();
            foundRoom.LoadEntity(roomEntity);
            _rooms.Add(foundRoom);
            return foundRoom;
        }

        // Might want this on the living object or whatever is needing a periodic update like this - like for HP regen, etc. Doesn't need to be centralized necessarily.
        private void doHeartbeat(object state)
        {
            // Do heartbeat updates.
            foreach (Player currentPlayer in Players)
            {
                currentPlayer.Update();
            }
        }
    }
}