// using System;
// using MongoDB.Bson;
// using MongoDB.Bson.Serialization;
// using MongoDB.Driver;
// using MUD.Telnet;

// namespace MUD.Core {
//     public class Login{
        
//         public static void handleLogin(Player playerSession, string message)
//         {
//             Console.WriteLine("BEGINNING LOGIN FOR: " + playerSession.TelnetClient.RemoteAddress);

//             if (playerSession.SessionStatus == EPlayerConnectionStatus.Guest)
//             {
//                 Console.WriteLine(playerSession.TelnetClient.RemoteAddress + ": Login: " + message);

//                 MongoClient dbClient = new MongoClient("mongodb+srv://testuser:qVvizXD1jrUaRdz4@cluster0.9titb.gcp.mongodb.net/test");
//                 var database = dbClient.GetDatabase("testmud");
//                 var collection = database.GetCollection<BsonDocument>("players");

//                 var filter = Builders<BsonDocument>.Filter.Eq("PlayerName", message);
//                 var foundUser = collection.Find(filter).FirstOrDefault();

//                 if (foundUser != null)
//                 {
//                     playerSession.TelnetClient.IsEchoSupressed = true;
//                     playerSession.TelnetClient.Send("Password: ");
//                     playerSession.SessionStatus = EPlayerConnectionStatus.Authenticating;
//                     playerSession = BsonSerializer.Deserialize<Player>(foundUser);
//                     return;
//                 }
//                 else
//                 {
//                     Console.WriteLine("User not recognized, kicking.");
//                     playerSession.TelnetClient.Disconnect();
//                 }
//             }
//             else if (playerSession.SessionStatus == EPlayerConnectionStatus.Authenticating)
//             {
//                 playerSession.TelnetClient.IsEchoSupressed = false;
//                 Console.WriteLine(playerSession.TelnetClient.RemoteAddress + ": Password: " + message);
//                 if (message == playerSession.Password)
//                 {
//                     playerSession.TelnetClient.Send("Successfully authenticated." + Server.END_LINE);
//                     playerSession.SessionStatus = EPlayerConnectionStatus.LoggedIn;
//                     playerSession.TelnetClient.Send(Server.END_LINE + Server.CURSOR);
//                 }
//                 else
//                 {
//                     Console.WriteLine("Incorrect password, kicking.");
//                     playerSession.TelnetClient.Disconnect();
//                 }
//             }
//         }
//     }
// }