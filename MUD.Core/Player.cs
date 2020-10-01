using System;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;
using MongoDB.Driver;
using MUD.Core.Commands;
using MUD.Core.Formatting;
using MUD.Core.Interfaces;
using MUD.Telnet;

namespace MUD.Core
{
    public class Player : Living, IUpdatable, IExaminable
    {
        private Client _connection;

        private CommandSource _knownCommandsLibrary;

        private DateTime _lastSave;

        private string _selectedTerm;

        private ITerminalHandler _selectedTerminalHandler;

        [BsonId(IdGenerator = typeof(StringObjectIdGenerator))]
        [BsonRepresentation(BsonType.ObjectId)]
        public string _id { get; set; }

        public string PlayerName { get; set; }

        public string Password { get; set; }

        public string Class { get; set; }

        public string[] KnownCommands { get; set; }

        public string SelectedTerm
        {
            get { return _selectedTerm; }

            set
            {
                _selectedTerm = value;
                _selectedTerminalHandler = World.Instance.TerminalHandlers.FirstOrDefault(th => th.TerminalName == _selectedTerm || (th.Aliases != null && th.Aliases.Contains(_selectedTerm)));
            }
        }

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

        public static Player Login(string userName, string password, Client connectingClient)
        {
            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password)) { return null; }

            MongoClient dbClient = new MongoClient("mongodb+srv://testuser:qVvizXD1jrUaRdz4@cluster0.9titb.gcp.mongodb.net/test");
            var database = dbClient.GetDatabase("testmud");
            var collection = database.GetCollection<Player>("players");

            var foundUser = collection.AsQueryable().Where(p => p.PlayerName.ToLower().Contains(userName)).FirstOrDefault();

            if (foundUser != null)
            {
                if (foundUser.Password == password)
                {
                    foundUser.ConnectionStatus = EPlayerConnectionStatus.LoggedIn;
                    foundUser.Connection = connectingClient;
                    foundUser._lastSave = DateTime.Now;

                    foundUser._knownCommandsLibrary = World.Instance.AllCommands.GetSubset(foundUser.KnownCommands);
                    return foundUser;
                }
            }
            return null;
        }

        public bool Save()
        {
            ReceiveMessage("\x1B[33mSaving...\x1B[0m");
            MongoClient dbClient = new MongoClient("mongodb+srv://testuser:qVvizXD1jrUaRdz4@cluster0.9titb.gcp.mongodb.net/test");
            var database = dbClient.GetDatabase("testmud");
            var collection = database.GetCollection<Player>("players");
            //var filter = Builders<Player>.Filter.Eq(p => p._id == _id);
            var result = collection.ReplaceOne<Player>((p => p._id == _id), this);
            if (result != null && result.IsModifiedCountAvailable && result.ModifiedCount == 1)
            {
                _lastSave = DateTime.Now;
                return true;
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

        public override void ReceiveMessage(string message)
        {
            if (_selectedTerminalHandler != null)
            {
                message = _selectedTerminalHandler.ResolveOutput(message);
            }
            _connection.Send(message);
        }

        public new string Examine()
        {
            return string.Format("Here stands {0}, a prime example of {1} {2}.", PlayerName, Race.GetArticle(), Race);
        }

        public new void Update()
        {
            if (ConnectionStatus == EPlayerConnectionStatus.LoggedIn)
            {
                if (DateTime.Now.Subtract(_lastSave).Minutes >= 10)
                {
                    ReceiveMessage("\x1B[33mSaving...\x1B[0m");
                    MongoClient dbClient = new MongoClient("mongodb+srv://testuser:qVvizXD1jrUaRdz4@cluster0.9titb.gcp.mongodb.net/test");
                    var database = dbClient.GetDatabase("testmud");
                    var collection = database.GetCollection<Player>("players");
                    //var filter = Builders<Player>.Filter.Eq(p => p._id == _id);
                    collection.ReplaceOne<Player>((p => p._id == _id), this);
                    _lastSave = DateTime.Now;
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

            matchedCommand = _knownCommandsLibrary.GetCommandFromInput(input);
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