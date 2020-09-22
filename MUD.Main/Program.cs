using System;
using System.Net;
using MUD.Telnet;
using MongoDB.Bson;
using MongoDB.Driver;
using MUD.Core;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson.Serialization;
using System.Threading;
using System.Text.RegularExpressions;

namespace MUD.Main
{
    class Program
    {
        private static World _gameWorld;
        private static Server _serverInstance;
        private static string welcomeMessage =
@"
                         Welcome to Planar Realms 0.1
--------------------------------------------------------------------------------
    Use 'connect <username> <password>' to connect to an existing player.
    Use 'create <username> <password>' to create a new player.
    Use 'who' to see who is currently online.
    Use 'quit' to disconnect.
--------------------------------------------------------------------------------";

        static void Main(string[] args)
        {
            Console.Write("Checking databases...");
            MongoClient dbClient = new MongoClient("mongodb+srv://testuser:qVvizXD1jrUaRdz4@cluster0.9titb.gcp.mongodb.net/test");
            var database = dbClient.GetDatabase("testmud"); // This creates the database if it doesn't otherwise exist?
            if (database == null) { Console.WriteLine("Database not found!"); }
            var collection = database.GetCollection<BsonDocument>("players"); // Check that the collection is there. If it is not, then the collection exists in memory and is created on the actual DB when needed.
            if (collection == null) { database.CreateCollection("players"); }
            collection = database.GetCollection<BsonDocument>("rooms"); // Check that the collection is there.
            //collection.InsertOne(BsonDocument.Parse(jsonContent)); // The BSON object will have _id and it will be set by the insert call.
            Console.WriteLine("done.");

            Console.Write("Starting world...");
            _gameWorld = World.Instance;
            Thread gameWorldThread = new Thread(_gameWorld.Start);
            gameWorldThread.Start();
            Console.WriteLine("done.");

            Console.Write("Starting telnet server...");
            _serverInstance = new Server(IPAddress.Any, 23);
            _serverInstance.ClientConnected += clientConnected;
            _serverInstance.ClientDisconnected += clientDisconnected;

            Thread serverThread = new Thread(_serverInstance.Start);
            serverThread.Start();
            Console.WriteLine("done.");

            Console.WriteLine("SERVER STARTED: " + DateTime.Now);
            char read2 = Console.ReadKey(true).KeyChar;

            do
            {
                if (read2 == 'b')
                {
                    _serverInstance.SendMessageToAll(Console.ReadLine());
                }
                if (read2 == 't')
                {
                    //s2.SendMessageToAll(new TelnetCommand(CommandCode.DO, OptionCode.TerminalType));
                }
            } while ((read2 = Console.ReadKey(true).KeyChar) != 'q');

            _gameWorld.Stop();
            _serverInstance.Stop();

            //return;

            //Player testPlayer = new Player() { PlayerName = "holen", Password = "password", Gender = "male", Race = "hobbit", Class = "rogue" };
            //var jsonContent = JsonConvert.SerializeObject(testPlayer);
        }

        private static void clientConnected(object sender, Client c)
        {
            Console.WriteLine("CONNECTED: " + c);

            c.DataReceived += messageReceived;
            c.Send(welcomeMessage);
        }

        private static void clientDisconnected(object sender, Client c)
        {
            c.DataReceived -= messageReceived;
            Console.WriteLine("DISCONNECTED: " + c);
        }

        private static void connectionBlocked(IPEndPoint ep)
        {
            Console.WriteLine(string.Format("BLOCKED: {0}:{1} at {2}", ep.Address, ep.Port, DateTime.Now));
        }

        private static void messageReceived(object sender, string message)
        {
            Client client = (Client)sender;
            message = message.TrimEnd('\n').TrimEnd('\r');

            Console.WriteLine("MESSAGE: " + message);

            if (message == "quit")
            {
                client.Disconnect();
            }
            else if (message == "who")
            {
                // Show the list of logged in players
                if (_gameWorld.Players == null || _gameWorld.Players.Count == 0)
                {
                    client.Send("No one is logged on.");
                }
                else if (_gameWorld.Players.Count == 1)
                {
                    client.Send(String.Format("{0} is logged on.", _gameWorld.Players.First().PlayerName));
                }
                else
                {
                    client.Send(String.Format("{0} are logged on.", String.Join(", ", _gameWorld.Players.Select(p => p.PlayerName))));
                }
            }
            else if (message.StartsWith("connect "))
            {
                string[] args = message.Substring(8).Split(' ');

                if (args.Length != 2)
                {
                    // bad input. 
                    client.Send("Please provide a username and password to connect. Format: connect <username> <password>");
                }

                string username = args[0];
                string password = args[1];

                // begin login attempt.
                var foundPlayer = Player.LoadFromUsername(username);
                if (foundPlayer == null)
                {
                    // Player not found.
                    client.Send(String.Format("Player {0} was not found.", username));
                }
                else
                {
                    // Found the player.. check the password.
                    if (foundPlayer.CheckPassword(password))
                    {
                        // Ok login
                        if (_gameWorld.Players.Contains(foundPlayer))
                        {
                            // Already logged on.
                        }
                        else
                        {
                            foundPlayer.ConnectionStatus = EPlayerConnectionStatus.LoggedIn;
                            foundPlayer.Connection = client;
                            client.DataReceived -= messageReceived;
                            _gameWorld.AddPlayer(foundPlayer);
                        }
                    }
                    else
                    {
                        // bad login
                        client.Send("Incorrect password.");
                    }
                }
            }
            else if (message.StartsWith("create "))
            {
                string[] args = message.Substring(8).Split(' ');

                if (args.Length != 2)
                {
                    // bad input. 
                    client.Send("Please provide a username and password to create a new account. Format: create <username> <password>");
                }

                string username = args[0];
                string password = args[1];
            }
            else
            {
                client.Send("Command {0} was not recognized.");
            }
        }
    }
}