using System;
using System.Net;
using MUD.Telnet;
using MongoDB.Bson;
using MongoDB.Driver;
using MUD.Core;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson.Serialization;

namespace MUD.Main
{
    class Program
    {
        private static Server s;
        private static List<PlayerSession> activeSessions = new List<PlayerSession>();

        static void Main(string[] args)
        {
            //Player testPlayer = new Player() { PlayerName = "holen", Password = "password", Gender = "male", Race = "hobbit", Class = "rogue" };
            //var jsonContent = JsonConvert.SerializeObject(testPlayer);

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
            s = new Server(IPAddress.Any);
            s.ClientConnected += clientConnected;
            s.ClientDisconnected += clientDisconnected;
            s.ConnectionBlocked += connectionBlocked;
            s.MessageReceived += messageReceived;
            s.start();
            Console.WriteLine("done.");

            Console.WriteLine("SERVER STARTED: " + DateTime.Now);

            char read = Console.ReadKey(true).KeyChar;

            do
            {
                if (read == 'b')
                {
                    s.sendMessageToAll(Console.ReadLine());
                }
            } while ((read = Console.ReadKey(true).KeyChar) != 'q');

            s.stop();
        }

        private static void clientConnected(Client c)
        {
            Console.WriteLine("CONNECTED: " + c);
            activeSessions.Add(new PlayerSession(c));

            s.sendMessageToClient(c, "Telnet Server" + Server.END_LINE + "Login: ");
        }

        private static void clientDisconnected(Client c)
        {
            Console.WriteLine("DISCONNECTED: " + c);
        }

        private static void connectionBlocked(IPEndPoint ep)
        {
            Console.WriteLine(string.Format("BLOCKED: {0}:{1} at {2}", ep.Address, ep.Port, DateTime.Now));
        }

        private static void messageReceived(Client c, string message)
        {
            Console.WriteLine("messageReceived: " + message.Replace("\r", "\\r").Replace("\n", "\\n"));
            message = message.TrimEnd('\n').TrimEnd('\r');

            PlayerSession playerSession = activeSessions.FirstOrDefault(s=> s.TelnetClient == c);

            if (playerSession.SessionStatus != EPlayerSessionStatus.LoggedIn)
            {
                handleLogin(playerSession, message);
                c.resetReceivedData();
                return;
            }

            Console.WriteLine("MESSAGE: " + message);

            if (message == "quit" || message == "logout" ||
                message == "exit")
            {
                s.kickClient(c);
                s.sendMessageToClient(c, Server.END_LINE + Server.CURSOR);
            }

            else if (message == "clear")
            {
                c.resetReceivedData();
                s.clearClientScreen(c);
                s.sendMessageToClient(c, Server.CURSOR);
            }

            else
            {
                // Send to command parser
                c.resetReceivedData();
                s.sendMessageToClient(c, Server.END_LINE + Server.CURSOR);
            }
        }

        private static void handleLogin(PlayerSession playerSession, string message)
        {
            Console.WriteLine("BEGINNING LOGIN FOR: " + playerSession.TelnetClient.getRemoteAddress());

            if (playerSession.SessionStatus == EPlayerSessionStatus.Guest)
            {
                Console.WriteLine(playerSession.TelnetClient.getRemoteAddress() + ": Login: " + message);

                MongoClient dbClient = new MongoClient("mongodb+srv://testuser:qVvizXD1jrUaRdz4@cluster0.9titb.gcp.mongodb.net/test");
                var database = dbClient.GetDatabase("testmud");
                var collection = database.GetCollection<BsonDocument>("players");

                var filter = Builders<BsonDocument>.Filter.Eq("PlayerName", message);
                var foundUser = collection.Find(filter).FirstOrDefault();

                if (foundUser != null)
                {
                    s.sendMessageToClient(playerSession.TelnetClient, Server.END_LINE + "Password: ");
                    playerSession.SessionStatus = EPlayerSessionStatus.Authenticating;
                    playerSession.SessionPlayer = BsonSerializer.Deserialize<Player>(foundUser);
                    return;
                }
                else
                {
                    Console.WriteLine("User not recognized, kicking.");
                    s.kickClient(playerSession.TelnetClient);
                    activeSessions.Remove(playerSession);
                }
            }
            else if (playerSession.SessionStatus == EPlayerSessionStatus.Authenticating)
            {
                Console.WriteLine(playerSession.TelnetClient.getRemoteAddress() + ": Password: " + message);
                if (message == playerSession.SessionPlayer.Password)
                {
                    s.clearClientScreen(playerSession.TelnetClient);
                    s.sendMessageToClient(playerSession.TelnetClient, Server.END_LINE + "Successfully authenticated." + Server.END_LINE + Server.CURSOR);
                    playerSession.SessionStatus = EPlayerSessionStatus.LoggedIn;
                }
                else
                {
                    Console.WriteLine("Incorrect password, kicking.");
                    s.kickClient(playerSession.TelnetClient);
                }
            }
        }
    }
}