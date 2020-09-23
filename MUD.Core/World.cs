using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

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

        public List<Player> Players;

        public CommandLibrary AllCommands;
        public CommandLibrary DefaultCommands;

        private Timer _heartbeatTimer;

        private bool _isRunning;
        private World()
        {
            Players = new List<Player>();
            AllCommands = new CommandLibrary();
            AllCommands.LoadCommandsFromAssembly(Assembly.GetExecutingAssembly());
            DefaultCommands = new CommandLibrary(new Dictionary<string, ICommand>(AllCommands.Commands.Where(kvp => kvp.Value.IsDefault)));
        }

        public void Start()
        {
            var startTimeSpan = TimeSpan.Zero;
            var periodTimeSpan = TimeSpan.FromSeconds(5);

            _heartbeatTimer = new System.Threading.Timer(new TimerCallback(doHeartbeat), null, startTimeSpan, periodTimeSpan);

            _isRunning = true;

            while(_isRunning) {
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
            Players.Add(newPlayer);
            foreach (var currentPlayer in Players)
            {
                currentPlayer.Connection.Send(String.Format("[{0} enters Planar Realms]", newPlayer.PlayerName));
            }
        }

        public void RemovePlayer(Player player)
        {
            if (Players.Remove(player))
            {
                foreach (var currentPlayer in Players)
                {
                    currentPlayer.Connection.Send(String.Format("[{0} left Planar Realms]", player.PlayerName));
                }
            }
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