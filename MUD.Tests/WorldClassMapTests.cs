using System;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MUD.Core;
using Xunit;

namespace MUD.Tests
{
    public class WorldClassMapTests
    {
        private static World NewWorld()
        {
            // MongoClient does not connect until used.
            var client = new MongoClient("mongodb://localhost:27017");
            return new World(client, new WorldSettings());
        }

        [Fact]
        public void SecondWorldDoesNotClearClassMaps()
        {
            NewWorld();
            Assert.True(BsonClassMap.IsClassMapRegistered(typeof(MUD.Data.Entity)));
            NewWorld();
            Assert.True(BsonClassMap.IsClassMapRegistered(typeof(MUD.Data.Entity)));
        }

        [Fact]
        public void WorldsCanBeBuiltInParallel()
        {
            var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() => NewWorld())).ToArray();
            Task.WaitAll(tasks);
            Assert.True(BsonClassMap.IsClassMapRegistered(typeof(MUD.Data.Entity)));
        }
    }
}
