using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MUD.Core.GameObjects;
using MUD.Data;
using Xunit;

namespace MUD.Tests
{
    public class ObjectIdMigrationTests
    {
        private static readonly ObjectId RoomOid = ObjectId.Parse("5f6e27f20c1fdd24b4b18b1a");
        private static readonly ObjectId OtherOid = ObjectId.Parse("5f6e27f20c1fdd24b4b18b1b");

        private static BsonDocument LegacyRoom()
        {
            return new BsonDocument
            {
                { "_id", RoomOid },
                { "Name", "Old room" },
                { "Exits", new BsonArray { new BsonDocument { { "Name", "n" }, { "DestinationId", OtherOid } } } }
            };
        }

        [Fact]
        public void ConvertRewritesIdAndReferenceObjectIds()
        {
            var doc = LegacyRoom();

            Assert.True(ObjectIdMigration.Convert(doc));

            Assert.Equal(BsonType.String, doc["_id"].BsonType);
            Assert.Equal("5f6e27f20c1fdd24b4b18b1a", doc["_id"].AsString);
            Assert.Equal("5f6e27f20c1fdd24b4b18b1b", doc["Exits"][0]["DestinationId"].AsString);
        }

        [Fact]
        public void ConvertRewritesPlayerLocationAndNestedItemIds()
        {
            var doc = new BsonDocument
            {
                { "_id", OtherOid },
                { "CurrentLocationId", RoomOid },
                { "Inventory", new BsonArray { new BsonDocument("_id", RoomOid) } }
            };

            Assert.True(ObjectIdMigration.Convert(doc));

            Assert.Equal("5f6e27f20c1fdd24b4b18b1a", doc["CurrentLocationId"].AsString);
            Assert.Equal("5f6e27f20c1fdd24b4b18b1a", doc["Inventory"][0]["_id"].AsString);
        }

        [Fact]
        public void ConvertLeavesOtherObjectIdFieldsAndStringsAlone()
        {
            var doc = new BsonDocument { { "_id", "castle.gate" }, { "Other", OtherOid }, { "DestinationId", "castle.hall" } };

            Assert.False(ObjectIdMigration.Convert(doc));

            Assert.True(doc["Other"].IsObjectId);
            Assert.Equal("castle.gate", doc["_id"].AsString);
        }

        [Fact]
        public void ConvertedLegacyRoomDeserializes()
        {
            var doc = LegacyRoom();
            ObjectIdMigration.Convert(doc);

            var room = BsonSerializer.Deserialize<Room>(doc);

            Assert.Equal("5f6e27f20c1fdd24b4b18b1a", room.Id);
            Assert.Equal("5f6e27f20c1fdd24b4b18b1b", room.Exits[0].DestinationId);
        }

        // Runs against a real MongoDB only when MUD_TEST_MONGO_URI is set; otherwise it is a no-op.
        [Fact]
        public void MigrateCollectionAgainstRealMongo()
        {
            var uri = Environment.GetEnvironmentVariable("MUD_TEST_MONGO_URI");
            if (string.IsNullOrWhiteSpace(uri)) { return; }

            var client = new MongoClient(uri);
            var dbName = "mud_idmigration_test_" + Guid.NewGuid().ToString("N");
            try
            {
                var collection = client.GetDatabase(dbName).GetCollection<BsonDocument>("room");
                collection.InsertOne(LegacyRoom());
                collection.InsertOne(new BsonDocument { { "_id", "castle.gate" }, { "Exits", new BsonArray() } });

                Assert.Equal(1, ObjectIdMigration.MigrateCollection(collection));
                Assert.Equal(0, ObjectIdMigration.MigrateCollection(collection)); // idempotent

                Assert.Equal(2, collection.CountDocuments(new BsonDocument()));
                var typed = client.GetDatabase(dbName).GetCollection<Room>("room");
                var room = typed.Find(r => r.Id == "5f6e27f20c1fdd24b4b18b1a").Single();
                Assert.Equal("5f6e27f20c1fdd24b4b18b1b", room.Exits[0].DestinationId);
            }
            finally
            {
                client.DropDatabase(dbName);
            }
        }
    }
}
