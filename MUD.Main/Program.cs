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

namespace MUD.Main
{
    class Program
    {
        private static Server _serverInstance;
        private static List<PlayerSession> activeSessions = new List<PlayerSession>();

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
            Console.WriteLine("done.");

            Console.Write("Starting telnet server...");
            _serverInstance = new Server(IPAddress.Any, 23);
            _serverInstance.ClientConnected += clientConnected;
            _serverInstance.ClientDisconnected += clientDisconnected;

            Thread thread = new Thread(_serverInstance.Start);
            thread.Start();
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

            _serverInstance.Stop();

            //return;

            //Player testPlayer = new Player() { PlayerName = "holen", Password = "password", Gender = "male", Race = "hobbit", Class = "rogue" };
            //var jsonContent = JsonConvert.SerializeObject(testPlayer);


            // s = new Server(IPAddress.Any);
            // s.ClientConnected += clientConnected;
            // s.ClientDisconnected += clientDisconnected;
            // s.ConnectionBlocked += connectionBlocked;
            // s.start();

            // char read = Console.ReadKey(true).KeyChar;

            // do
            // {
            //     if (read == 'b')
            //     {
            //         s.sendMessageToAll(Console.ReadLine());
            //     }
            //     if (read == 't')
            //     {
            //         s.sendMessageToAll(new TelnetCommand(CommandCode.DO, OptionCode.TerminalType));
            //     }
            // } while ((read = Console.ReadKey(true).KeyChar) != 'q');

            // s.stop();
        }

        private static void clientConnected(object sender, Client c)
        {
            Console.WriteLine("CONNECTED: " + c);
            activeSessions.Add(new PlayerSession(c));

            c.DataReceived += messageReceived;
            c.Send("Telnet Server" + Server.END_LINE + "Login: ");
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
            Client client = (Client) sender;
            Console.WriteLine("messageReceived: " + message.Replace("\r", "\\r").Replace("\n", "\\n"));
            message = message.TrimEnd('\n').TrimEnd('\r');

            PlayerSession playerSession = activeSessions.FirstOrDefault(s => s.TelnetClient == client);

            if (playerSession.SessionStatus != EPlayerSessionStatus.LoggedIn)
            {
                handleLogin(playerSession, message);
                return;
            }

            Console.WriteLine("MESSAGE: " + message);

            if (message == "quit")
            {
                client.Disconnect();
            }

            else
            {
                // Send to command parser
                //c.SendMessageToClient(Server.END_LINE + Server.CURSOR);
            }
        }

        private static void handleLogin(PlayerSession playerSession, string message)
        {
            Console.WriteLine("BEGINNING LOGIN FOR: " + playerSession.TelnetClient.RemoteAddress);

            if (playerSession.SessionStatus == EPlayerSessionStatus.Guest)
            {
                Console.WriteLine(playerSession.TelnetClient.RemoteAddress + ": Login: " + message);

                MongoClient dbClient = new MongoClient("mongodb+srv://testuser:qVvizXD1jrUaRdz4@cluster0.9titb.gcp.mongodb.net/test");
                var database = dbClient.GetDatabase("testmud");
                var collection = database.GetCollection<BsonDocument>("players");

                var filter = Builders<BsonDocument>.Filter.Eq("PlayerName", message);
                var foundUser = collection.Find(filter).FirstOrDefault();

                if (foundUser != null)
                {
                    playerSession.TelnetClient.IsEchoSupressed = true;
                    playerSession.TelnetClient.Send("Password: ");
                    playerSession.SessionStatus = EPlayerSessionStatus.Authenticating;
                    playerSession.SessionPlayer = BsonSerializer.Deserialize<Player>(foundUser);
                    return;
                }
                else
                {
                    Console.WriteLine("User not recognized, kicking.");
                    playerSession.TelnetClient.Disconnect();
                    activeSessions.Remove(playerSession);
                }
            }
            else if (playerSession.SessionStatus == EPlayerSessionStatus.Authenticating)
            {
                playerSession.TelnetClient.IsEchoSupressed = false;
                Console.WriteLine(playerSession.TelnetClient.RemoteAddress + ": Password: " + message);
                if (message == playerSession.SessionPlayer.Password)
                {
                    playerSession.TelnetClient.Send("Successfully authenticated." + Server.END_LINE);
                    playerSession.SessionStatus = EPlayerSessionStatus.LoggedIn;
                    _serverInstance.SendMessageToAll("[" + playerSession.SessionPlayer.PlayerName + " has entered Planar Realms]"); 
                    playerSession.TelnetClient.Send(Server.END_LINE + Server.CURSOR);
                }
                else
                {
                    Console.WriteLine("Incorrect password, kicking.");
                    playerSession.TelnetClient.Disconnect();
                }
            }
        }
    }
}