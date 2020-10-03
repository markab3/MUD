using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using MongoDB.Driver;
using MUD.Core.Commands;
using MUD.Core.Formatting;
using MUD.Core.Repositories.Interfaces;

namespace MUD.Core
{
    public class World
    {
        public static World Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new World();
                }
                return _instance;
            }
        }

        private static World _instance;

        public static IServiceProvider ServiceProvider;

        public List<Player> Players;

        // public List<Room> Rooms;

        public CommandSource AllCommands;

        public CommandSource DefaultCommands;

        public List<ITerminalHandler> TerminalHandlers;

        private Timer _heartbeatTimer;

        private bool _isRunning;

        private World()
        {
            Players = new List<Player>();

            /// Hey dufus - this smells a lot like a dependency injection framework... maybe look into doing that..
            AllCommands = new CommandSource();
            AllCommands.LoadCommandsFromAssembly(Assembly.GetExecutingAssembly());
            DefaultCommands = new CommandSource(new Dictionary<string, ICommand>(AllCommands.Commands.Where(kvp => kvp.Value.IsDefault)));
            TerminalHandlers = this.loadImplementingClasses<ITerminalHandler>();

            // Load stuff
            // MongoClient dbClient = new MongoClient("mongodb+srv://testuser:qVvizXD1jrUaRdz4@cluster0.9titb.gcp.mongodb.net/test");
            // var database = dbClient.GetDatabase("testmud"); // This creates the database if it doesn't otherwise exist?
            // if (database == null) { Console.WriteLine("Database not found!"); }
            // var roomCollection = database.GetCollection<Room>("room");
            // Rooms = roomCollection.AsQueryable().ToList<Room>(); // get all rooms?
        }

        public void Start()
        {
            var startTimeSpan = TimeSpan.Zero;
            var periodTimeSpan = TimeSpan.FromSeconds(2);

            _heartbeatTimer = new System.Threading.Timer(new TimerCallback(doHeartbeat), null, startTimeSpan, periodTimeSpan);

            _isRunning = true;

            while (_isRunning)
            {
                CommandQueue.Instance.ExecuteAllCommands();
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
                currentPlayer.ReceiveMessage(String.Format("[\x1B[32m{0} enters Planar Realms\x1B[0m]", newPlayer.PlayerName));
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
                    currentPlayer.ReceiveMessage(String.Format("[{0} left Planar Realms]", player.PlayerName));
                }
            }
        }

        public Room GetRoom(string roomId)
        {
            if (string.IsNullOrWhiteSpace(roomId))
            {
                return null;
            }

            var roomRepository = (IRoomRepository)ServiceProvider.GetService(typeof(IRoomRepository));
            var roomEntity = roomRepository.Get(roomId);

            if (roomEntity == null)
            {
                return null;
            }

            return new Room(roomRepository, roomEntity);
        }

        private List<T> loadImplementingClasses<T>(Assembly assemblyToLoad = null)
        {
            if (assemblyToLoad == null) { assemblyToLoad = Assembly.GetExecutingAssembly(); }
            var classes = new List<T>();
            var commandTypes = assemblyToLoad.DefinedTypes.Where(t => t.ImplementedInterfaces.Any(i => i == typeof(T)));
            foreach (TypeInfo currentType in commandTypes)
            {
                T newClass = (T)Activator.CreateInstance(currentType);
                classes.Add(newClass);
            }
            return classes;
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