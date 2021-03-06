using System;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MUD.Core.Commands;
using MUD.Core.Formatting;
using MUD.Telnet;

namespace MUD.Core
{
    public class Player : GameObject
    {
        private Client _connection;

        private World _world;

        private CommandQueue _commandQueue;

        private Room _currentLocation;

        public string Gender { get; set; }

        public string Race { get; set; }

        [BsonRepresentation(BsonType.ObjectId)]
        public string CurrentLocation_id { get; set; }

        public string PlayerName { get; set; }

        public string Password { get; set; }

        public string Class { get; set; }

        public string[] KnownCommands { get; set; }

        public string SelectedTerm { get; set; }

        [BsonIgnore]
        public Client Connection
        {
            get { return _connection; }
            set
            {
                // If connection is changing, drop event handlers for the old one.
                if (_connection != null && value != _connection)
                {
                    _connection.DataReceived -= DataReceivedHandler;
                    _connection.ClientDisconnected -= ClientDisconnectedHandler;
                }
                _connection = value;
                if (value != null)
                {
                    _connection.DataReceived += DataReceivedHandler;
                    _connection.ClientDisconnected += ClientDisconnectedHandler;
                }
            }
        }

        [BsonIgnore]
        public EPlayerConnectionStatus ConnectionStatus { get; set; }

        [BsonIgnore]
        public Room CurrentLocation
        {
            get
            {
                if (_currentLocation == null && !string.IsNullOrWhiteSpace(CurrentLocation_id))
                {
                    _currentLocation = _world.GetRoom(CurrentLocation_id);
                }
                return _currentLocation;
            }
            set { _currentLocation = value; } // Maybe make this private so we have to load via ID?
        }

        public Player(IMongoDatabase db, World world, CommandQueue commandQueue) : base(db)
        {
            _world = world;
            _commandQueue = commandQueue;
        }

        public bool Save()
        {
            ReceiveMessage("%^Bold%^%^Yellow%^Saving...%^Reset%^");
            return base.Update();
        }

        public void Quit()
        {
            ReceiveMessage("Thanks for playing!");
            ConnectionStatus = EPlayerConnectionStatus.Offline;
            if (Save())
            {
                _world.RemovePlayer(this);
                Connection.Disconnect();
            }
            else
            {
                // Uh oh!
            }
        }

        public CommandSource GetKnownCommandSource()
        {
            if (KnownCommands != null)
            {
                return _world.AllCommands.GetSubset(KnownCommands);
            }
            return new CommandSource();
        }

        public void MoveToRoom(string roomIdToEnter)
        {
            var roomToEnter = _world.GetRoom(roomIdToEnter);
            if (CurrentLocation != null)
            {
                CurrentLocation.ExitRoom(this);
            }

            CurrentLocation = roomToEnter;
            CurrentLocation_id = roomToEnter._id;

            roomToEnter.EnterRoom(this);
            ReceiveMessage(roomToEnter.Examine(this));
        }

        public void ReceiveMessage(string message)
        {
            var selectedTerminalHandler = _world.TerminalHandlers.FirstOrDefault(th => th.TerminalName == SelectedTerm || (th.Aliases != null && th.Aliases.Contains(SelectedTerm)));
            if (selectedTerminalHandler != null)
            {
                message = selectedTerminalHandler.ResolveOutput(message);
            }
            _connection.Send(message);
        }

        public override string Examine()
        {
            return string.Format("Here stands {0}, a prime example of {1} {2}.", PlayerName, Race.GetArticle(), Race);
        }

        public void DoHeartbeat()
        {
            if (ConnectionStatus == EPlayerConnectionStatus.LoggedIn)
            {
                if (DateTime.Now.Subtract(_lastSave).Minutes >= 10)
                {
                    Save();
                }
            }
        }

        public void DataReceivedHandler(object sender, string message)
        {
            // check to see if this matches a command that the player has available.
            // if it does, add that command to the queue
            ICommand matchedCommand = ResolveCommand(message);
            if (matchedCommand != null) // Move this logic to something else..something to match Command classes with input.
            {
                _commandQueue.AddCommand(new CommandExecution(this, message, matchedCommand));
            }
            else
            {
                ReceiveMessage("Command not recognized.");
            }
        }

        /// <summary>
        /// Checks the player, their environment, their equipment, and the global default commands to find a command that matches the provided input.
        /// </summary>
        public ICommand ResolveCommand(string input)
        {
            ICommand matchedCommand = null;

            if (CurrentLocation != null)
            {
                matchedCommand = CurrentLocation.RoomCommandSource.GetCommandFromInput(input);
            }
            if (matchedCommand != null) { return matchedCommand; }

            matchedCommand = GetKnownCommandSource().GetCommandFromInput(input);
            if (matchedCommand != null) { return matchedCommand; }

            matchedCommand = _world.DefaultCommands.GetCommandFromInput(input);
            if (matchedCommand != null) { return matchedCommand; }

            return null;
        }

        private void ClientDisconnectedHandler(object sender, Client e)
        {
            // I think this is what we want for now... 
            // I think in other muds the player object doesn't get removed unless you quit via the actual command. That way you can't just kill your connection and avoid death or whatever.
            // We would need to add a timeout feature...
            e.DataReceived -= DataReceivedHandler;
            e.ClientDisconnected -= ClientDisconnectedHandler;
            Quit();
            //ConnectionStatus = EPlayerConnectionStatus.NetDead; // Maybe just online/offline? Not sure when net dead is a thing...
        }
    }
}