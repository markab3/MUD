using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MUD.Core.GameObjects;
using Xunit;

namespace MUD.Tests
{
    public class StringKeyIdTests
    {
        [Fact]
        public void NewRoomGetsGeneratedObjectIdString()
        {
            var room = new Room(null);
            Assert.True(ObjectId.TryParse(room.Id, out _));
        }

        [Fact]
        public void SuppliedKeyOverridesGeneratedId()
        {
            var room = new Room(null) { Id = "castle.throne_room" };
            Assert.Equal("castle.throne_room", room.Id);
        }

        [Fact]
        public void RoomIdIsSerializedAsPlainStringNotObjectId()
        {
            var room = new Room(null) { Id = "castle.throne_room" };
            room.Exits.Add(new Exit { Name = "out", DestinationId = "castle.gate" });

            var doc = room.ToBsonDocument();

            Assert.Equal(BsonType.String, doc["_id"].BsonType);
            Assert.Equal("castle.throne_room", doc["_id"].AsString);
            Assert.Equal(BsonType.String, doc["Exits"][0]["DestinationId"].BsonType);
        }

        [Fact]
        public void ExitDestinationAcceptsNonObjectIdKey()
        {
            var exit = BsonSerializer.Deserialize<Exit>(new BsonDocument { { "Name", "n" }, { "DestinationId", "castle.gate" } });
            Assert.Equal("castle.gate", exit.DestinationId);
        }

        [Fact]
        public void RoomAreaAndCoordinatesDefaultToUnplaced()
        {
            var room = new Room(null);
            Assert.Null(room.Area);
            Assert.Null(room.X);
            Assert.Null(room.Y);
            Assert.Null(room.Z);
        }
    }
}
