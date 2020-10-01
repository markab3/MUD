using System;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MUD.Data
{
    public class Database
    {
        //IMongoCollection<Player> _playersCollection;
        //IMongo
        public Database(string connectionString, string databaseName)
        {
            MongoClient dbClient = new MongoClient(connectionString);
            var database = dbClient.GetDatabase(databaseName); // This creates the database if it doesn't otherwise exist?
            if (database == null) { Console.WriteLine("Database not found!"); }
            //var collection = database.GetCollection<Player>("players"); // Check that the collection is there. If it is not, then the collection exists in memory and is created on the actual DB when needed.
            //if (collection == null) { database.CreateCollection("players"); }
            //collection = database.GetCollection<BsonDocument>("rooms"); // Check that the collection is there.
        }
    }
}