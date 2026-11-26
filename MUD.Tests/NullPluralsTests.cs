using System.Collections.Generic;
using System.IO;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MUD.Core;
using MUD.Core.Commands;
using MUD.Core.GameObjects;
using MUD.Core.Import;
using Xunit;

namespace MUD.Tests
{
    // Regression: "look table" threw NullReferenceException because Player/NPC (built without Plurals) sat in the
    // room's occupants and GameObject.Plurals had no default. Stored documents may also hold null or no plurals.
    [Collection("World construction")]
    public class NullPluralsTests
    {
        private readonly FakeClock _clock = new FakeClock();

        private class Talker : Player
        {
            public readonly List<string> Messages = new List<string>();
            public Talker(World world, CommandQueue queue, FakePlayerRepo repo) : base(world, queue, repo) { }
            public override void ReceiveMessage(string message) { Messages.Add(message); }
        }

        private Talker NewPlayerIn(Room room)
        {
            var world = new World(new MongoDB.Driver.MongoClient("mongodb://127.0.0.1:1"), new WorldSettings(), _clock);
            var player = new Talker(world, new CommandQueue(_clock), new FakePlayerRepo()) { Id = "p.ann", PlayerName = "ann", ShortDescription = "ann", KnownCommands = new string[0] };
            room.EnterRoom(player, "ann arrives");
            player.CurrentLocation = room;
            player.CurrentLocationId = room.Id;
            player.ConnectionStatus = PlayerConnectionStatuses.LoggedIn;
            return player;
        }

        private static Room SampleHall()
        {
            string path = null;
            for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d != null; d = d.Parent)
            {
                var candidate = Path.Combine(d.FullName, "MUD.Main", "Content", "sample_area.yaml");
                if (File.Exists(candidate)) { path = candidate; break; }
            }
            Assert.NotNull(path);
            var store = new FakeStore();
            Assert.True(new AreaImporter(store).Import(File.ReadAllText(path)).Success);
            var room = new Room(new FakeRoomRepo()) { Id = "sample_area.hall" };
            room.ApplyDefinition(BsonSerializer.Deserialize<Room>(store.Docs["sample_area.hall"]));
            return room;
        }

        [Fact]
        public void PluralsDefaultsToEmptyList()
        {
            Assert.Empty(new Feature().Plurals);
            Assert.Empty(new InventoryItem().Plurals);
        }

        [Fact]
        public void NullPluralsDoesNotThrowInMatching()
        {
            var item = new InventoryItem { ShortDescription = "a lamp", Handles = new List<string> { "lamp" }, Plurals = null, Adjectives = null };
            Assert.False(item.IsPluralMatchForString("lamps"));
            Assert.True(item.IsMatchForString("lamp"));
            item.Handles = null;
            Assert.False(item.IsMatchForString("lamp"));
        }

        [Fact]
        public void MissingPluralsInStoredDocumentIsHandled()
        {
            var doc = new BsonDocument { { "_id", "t.lamp" }, { "ShortDescription", "a lamp" }, { "Plurals", BsonNull.Value } };
            var item = BsonSerializer.Deserialize<InventoryItem>(doc);
            Assert.False(item.IsPluralMatchForString("lamps"));
        }

        [Fact]
        public void LookTableInSampleHallWithAPlayerPresentFindsTheTable()
        {
            var room = SampleHall();
            var player = NewPlayerIn(room);

            player.DataReceivedHandler(null, "look table");

            Assert.DoesNotContain(player.Messages, m => m.Contains("error executing"));
            Assert.Contains(player.Messages, m => m.Contains("The table is scarred"));
        }

        [Fact]
        public void MatchObjectsToleratesObjectsWithNullPlurals()
        {
            var room = SampleHall();
            var player = NewPlayerIn(room);
            player.Plurals = null;
            player.Inventory.Add(new InventoryItem { ShortDescription = "a lamp", Handles = new List<string> { "lamp" }, Plurals = null });
            room.Features.Add(new Feature { ShortDescription = "a rug", Handles = new List<string> { "rug" }, Plurals = null });

            Assert.Single(player.MatchObjects("table"));
            Assert.Single(player.MatchObjects("lamp"));
        }

        [Fact]
        public void GetToleratesNullPluralsOnOtherRoomObjects()
        {
            var room = SampleHall();
            var player = NewPlayerIn(room);
            player.Plurals = null;
            var sword = new InventoryItem { Id = "i.sword", ShortDescription = "a sword", Handles = new List<string> { "sword" }, Plurals = null };
            room.Items.Add(sword);

            player.DataReceivedHandler(null, "get sword");

            Assert.Contains(sword, player.Inventory);
            Assert.DoesNotContain(player.Messages, m => m.Contains("error executing"));
        }
    }
}
