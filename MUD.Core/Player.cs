using System;
using System.Linq;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MUD.Core.Commands;
using MUD.Core.Entities;
using MUD.Core.Formatting;
using MUD.Core.Interfaces;
using MUD.Core.Repositories;
using MUD.Core.Repositories.Interfaces;
using MUD.Telnet;

namespace MUD.Core
{
    public class Player : PlayerEntity, IUpdatable, IExaminable
    {
        private IPlayerRepository _playerRepository;  // TODO: How inject?

        private Client _connection;

        private Room _currentLocation;

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
                    _currentLocation = World.Instance.GetRoom(CurrentLocation_id);
                }
                return _currentLocation;
            }
            set { _currentLocation = value; } // Maybe make this private so we have to load via ID?
        }

        public Player(IPlayerRepository playerRepository, PlayerEntity entity)
        {
            _playerRepository = playerRepository;
            entity.Map(this);
        }

        public bool Save()
        {
            ReceiveMessage("%^Bold%^%^Yellow%^Saving...%^Reset%^");
            if (string.IsNullOrWhiteSpace(_id))
            {
                _playerRepository.Insert(this);
                _lastSave = DateTime.Now;
                return true;
            }
            else
            {
                if (_playerRepository.Update(this))
                {
                    _lastSave = DateTime.Now;
                    return true;
                }
            }
            return false;
        }

        public void Quit()
        {
            ReceiveMessage("Thanks for playing!");
            ConnectionStatus = EPlayerConnectionStatus.Offline;
            if (Save())
            {
                World.Instance.RemovePlayer(this);
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
                return World.Instance.AllCommands.GetSubset(KnownCommands);
            }
            return new CommandSource();
        }

        public void MoveToRoom(string roomIdToEnter)
        {
            var roomToEnter = World.Instance.GetRoom(roomIdToEnter);
            if (CurrentLocation != null)
            {
                CurrentLocation.ExitRoom(this);
            }

            CurrentLocation = roomToEnter;
            CurrentLocation_id = roomToEnter._id;

            roomToEnter.EnterRoom(this);
            ReceiveMessage(roomToEnter.Examine());
        }

        public void ReceiveMessage(string message)
        {
            var selectedTerminalHandler = World.Instance.TerminalHandlers.FirstOrDefault(th => th.TerminalName == SelectedTerm || (th.Aliases != null && th.Aliases.Contains(SelectedTerm)));
            if (selectedTerminalHandler != null)
            {
                message = selectedTerminalHandler.ResolveOutput(message);
            }
            _connection.Send(message);
        }

        public string Examine()
        {
            return string.Format("Here stands {0}, a prime example of {1} {2}.", PlayerName, Race.GetArticle(), Race);
        }

        public void Update()
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
                CommandQueue.Instance.AddCommand(new CommandExecution(this, message, matchedCommand));
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
                matchedCommand = CurrentLocation.ExitCommandSource.GetCommandFromInput(input);
            }
            if (matchedCommand != null) { return matchedCommand; }

            matchedCommand = GetKnownCommandSource().GetCommandFromInput(input);
            if (matchedCommand != null) { return matchedCommand; }

            matchedCommand = World.Instance.DefaultCommands.GetCommandFromInput(input);
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