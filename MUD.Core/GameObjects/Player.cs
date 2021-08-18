using System;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MUD.Core.Commands;
using MUD.Core.Formatting;
using MUD.Core.Repositories.Interfaces;
using MUD.Telnet;

namespace MUD.Core.GameObjects
{
    public class Player : Living
    {
        private IPlayerRepository _playerRepository;

        private Client _connection;

        private CommandQueue _commandQueue;

        public string PlayerName { get; set; }

        public string Password { get; set; }

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
        public PlayerConnectionStatuses ConnectionStatus { get; set; }

        public Player(World world, CommandQueue commandQueue, IPlayerRepository playerRepository) : base(world)
        {
            _commandQueue = commandQueue;
            _playerRepository = playerRepository;
        }

        public bool Save()
        {
            ReceiveMessage("%^Bold%^%^Yellow%^Saving...%^Reset%^");
            if (string.IsNullOrWhiteSpace(Id))
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
            ConnectionStatus = PlayerConnectionStatuses.Offline;
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

        public override void ReceiveMessage(string message)
        {
            var selectedTerminalHandler = _world.TerminalHandlers.FirstOrDefault(th => th.TerminalName.ToLower() == SelectedTerm?.ToLower() || (th.Aliases != null && th.Aliases.Any(a => a.ToLower() == SelectedTerm?.ToLower())));
            if (selectedTerminalHandler != null)
            {
                message = selectedTerminalHandler.ResolveOutput(message);
            }
            _connection.Send(message);
        }

        public override void DoHeartbeat()
        {
            base.DoHeartbeat();

            if (ConnectionStatus == PlayerConnectionStatuses.LoggedIn)
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