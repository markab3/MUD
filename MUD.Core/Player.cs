using System;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;
using MongoDB.Driver;
using MUD.Core.Interfaces;
using MUD.Telnet;

namespace MUD.Core
{
    public class Player : IUpdatable, IExaminable
    {
        private Client _connection;

        [BsonId(IdGenerator = typeof(StringObjectIdGenerator))]
        [BsonRepresentation(BsonType.ObjectId)]
        public string _id { get; set; }
        public string PlayerName { get; set; }
        public string Password { get; set; }
        public string Gender { get; set; }
        public string Race { get; set; }
        public string Class { get; set; }
        public Room CurrentLocation { get; set; }

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

        public EPlayerConnectionStatus ConnectionStatus { get; set; }

        public static Player LoadFromUsername(string userName)
        {
            MongoClient dbClient = new MongoClient("mongodb+srv://testuser:qVvizXD1jrUaRdz4@cluster0.9titb.gcp.mongodb.net/test");
            var database = dbClient.GetDatabase("testmud");
            var collection = database.GetCollection<Player>("players");

            var foundUser = collection.AsQueryable().Where(p => p.PlayerName.ToLower().Contains(userName)).FirstOrDefault();

            if (foundUser != null)
            {
                foundUser.ConnectionStatus = EPlayerConnectionStatus.Authenticating;
                return foundUser;
            }
            return null;
        }

        public bool CheckPassword(string password)
        {
            return password == Password;
        }

        public string Examine()
        {
            return string.Format("Here stands {0}, a prime example of {1} {2}.", PlayerName, Race.GetArticle(), Race);
        }

        public void Update()
        {
            if (ConnectionStatus == EPlayerConnectionStatus.LoggedIn)
            {
                Connection.Send("Update!");
            }
        }

        public void DataReceivedHandler(object sender, string message)
        {
            // check to see if this matches a command that the player has available.
            // if it does, add that command to the queue
            if (ChatCommand.IsInputValid(message)) // Move this logic to something else..something to match Command classes with input.
            {
                CommandQueue.Instance.AddCommand(new ChatCommand(this, message));
            }
            else if (message.Trim().ToLower() == "quit")
            {
                World.Instance.RemovePlayer(this);
                Connection.Disconnect();
            }
        }
        private void ClientDisconnectedHandler(object sender, Client e)
        {
            // I think this is what we want for now... 
            // I think in other muds the player object doesn't get removed unless you quit via the actual command. That way you can't just kill your connection and avoid death or whatever.
            // We would need to add a timeout feature...
            World.Instance.RemovePlayer(this); 
            e.DataReceived -= DataReceivedHandler;
            e.ClientDisconnected -= ClientDisconnectedHandler;
            ConnectionStatus = EPlayerConnectionStatus.NetDead; // Maybe just online/offline? Not sure when net dead is a thing...
        }
    }
}