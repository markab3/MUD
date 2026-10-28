using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MUD.Core.Commands;
using MUD.Core.Components;
using MUD.Core.GameObjects;
using MUD.Core.Import;
using Xunit;

namespace MUD.Tests
{
    // Search configuration and cooldown as the room's "search" component, and bleeding as the "status.bleeding"
    // component plus a heartbeat behavior entry (owner decisions D2, D13, D54, D56; card P2-4).
    public class SearchBleedingComponentTests
    {
        private class Recorder : Living
        {
            public List<string> Messages = new List<string>();

            public Recorder() : base(null) { }

            public override void ReceiveMessage(string message) { Messages.Add(message); }
        }

        private static Room RoomWithSearch(int refreshMilliseconds = 60000)
        {
            var room = new Room(null) { Id = "t.hall", ShortDescription = "a hall" };
            room.Search.Entries.Add(new SearchEntry
            {
                EntryId = "table_search",
                Target = "table",
                RefreshMilliseconds = refreshMilliseconds,
                Reward = new InventoryItem { ShortDescription = "sword", LongDescription = "A sword.", Handles = new List<string> { "sword" } }
            });
            return room;
        }

        private static readonly DateTime Noon = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);

        [Fact]
        public void SearchGivesTheRewardAndStartsTheCooldown()
        {
            var room = RoomWithSearch();
            var searcher = new Recorder();

            Assert.True(room.Search.TrySearch(searcher, " Table ", Noon));
            Assert.Single(searcher.Inventory);
            Assert.Equal("sword", searcher.Inventory[0].ShortDescription);
            Assert.False(room.Search.TrySearch(searcher, "table", Noon.AddSeconds(59)));
            Assert.Single(searcher.Inventory);
            Assert.True(room.Search.TrySearch(searcher, "table", Noon.AddSeconds(60)));
            Assert.Equal(2, searcher.Inventory.Count);
        }

        [Fact]
        public void SearchForAnotherTargetFindsNothing()
        {
            var room = RoomWithSearch();
            var searcher = new Recorder();

            Assert.False(room.Search.TrySearch(searcher, "floor", Noon));
            Assert.Empty(searcher.Inventory);
        }

        [Fact]
        public void TheCooldownSurvivesSerializeAndDeserialize()
        {
            var room = RoomWithSearch();
            room.Search.TrySearch(new Recorder(), "table", Noon);

            var reloaded = BsonSerializer.Deserialize<Room>(room.ToBsonDocument());

            // A freshly booted room used to be searchable at once, because the cooldown was a private field that was never stored.
            Assert.False(reloaded.Search.TrySearch(new Recorder(), "table", Noon.AddSeconds(30)));
            Assert.True(reloaded.Search.TrySearch(new Recorder(), "table", Noon.AddSeconds(61)));
        }

        [Fact]
        public void ARoomWithoutASearchElementLoadsWithAnEmptySearchComponent()
        {
            var doc = new Room(null) { Id = "t.bare" }.ToBsonDocument();
            doc.Remove("search");

            var reloaded = BsonSerializer.Deserialize<Room>(doc);

            Assert.Empty(reloaded.Search.Entries);
            Assert.False(reloaded.Search.TrySearch(new Recorder(), "table", Noon));
        }

        [Fact]
        public void ApplyDefinitionKeepsTheCooldownOfAnEntryWithTheSameId()
        {
            var live = RoomWithSearch();
            live.Search.TrySearch(new Recorder(), "table", Noon);
            var fresh = RoomWithSearch(1000);

            live.ApplyDefinition(fresh);

            Assert.Equal(1000, live.Search.Entries[0].RefreshMilliseconds);
            Assert.Equal(Noon.AddMilliseconds(60000), live.Search.Entries[0].NextRewardAt);
        }

        [Fact]
        public void ASearchCommandRewardIsSavedWithTheRoom()
        {
            var roomRepository = new FakeRoomRepo();
            var room = RoomWithSearch();
            var clock = new FakeClock();
            var queue = new CommandQueue(clock);
            var searcher = new QuietPlayer(new FakePlayerRepo()) { CurrentLocation = room };
            typeof(Room).GetField("_roomRepository", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(room, roomRepository);
            roomRepository.Insert(room);

            queue.AddCommand(new CommandExecution(searcher, "search table", new SearchCommand(queue)));
            clock.Advance(SearchCommand.SearchDuration);
            queue.Dispatch();

            Assert.Single(searcher.Inventory);
            var stored = roomRepository.Docs["t.hall"]["search"]["entries"].AsBsonArray[0].AsBsonDocument;
            Assert.True(stored.Contains("nextRewardAt"));
        }

        [Fact]
        public void BleedingIsAComponentAndAHeartbeatEntry()
        {
            var living = new Recorder();

            Assert.True(BleedingStatus.Apply(living, 3));

            Assert.True(living.HasComponent("status.bleeding"));
            Assert.Equal(3, BleedingStatus.RemainingDuration(living));
            var entry = Assert.Single(living.Behaviors);
            Assert.Equal(("heartbeat", "status", "Bleeding"), (entry.Hook, entry.Module, entry.Type));
            Assert.False(BleedingStatus.Apply(living, 9));
            Assert.Equal(3, BleedingStatus.RemainingDuration(living));
        }

        [Fact]
        public void BleedingRunsThroughTheLivingHeartbeatAndEnds()
        {
            var living = new Recorder();
            BleedingStatus.Apply(living, 2);
            living.Messages.Clear();

            living.DoHeartbeat();
            Assert.Equal(new[] { "You continue to bleed from your wounds." }, living.Messages);
            Assert.Equal(1, BleedingStatus.RemainingDuration(living));

            living.Messages.Clear();
            living.DoHeartbeat();
            Assert.Equal(new[] { "You continue to bleed from your wounds.", "The flow of blood slows to a trickle and then stops." }, living.Messages);
            Assert.False(living.HasComponent("status.bleeding"));
            Assert.Empty(living.Behaviors);

            living.Messages.Clear();
            living.DoHeartbeat();
            Assert.Empty(living.Messages);
        }

        [Fact]
        public void BleedingSurvivesSerializeAndDeserialize()
        {
            var player = new Player(null, null, null) { Id = "p1" };
            BleedingStatus.Apply(player, 5);

            var reloaded = BsonSerializer.Deserialize<Player>(player.ToBsonDocument());

            Assert.Equal(5, BleedingStatus.RemainingDuration(reloaded));
            Assert.Single(reloaded.Behaviors, b => b.Module == "status" && b.Type == "Bleeding");
        }

        [Fact]
        public void ApplyStatusCommandStartsBleedingOnTheNamedLivingForACreatorOnly()
        {
            var room = new Room(null) { Id = "t.hall" };
            var creator = new Recorder { CurrentLocation = room, Plurals = new List<string>() };
            var victim = new Recorder { CurrentLocation = room, ShortDescription = "a goblin", Handles = new List<string> { "goblin" }, Plurals = new List<string>() };
            room.Occupants.Add(creator);
            room.Occupants.Add(victim);
            var command = new ApplyStatusCommand();

            command.DoCommand(creator, new object[] { "bleeding to goblin" });
            Assert.False(victim.HasComponent("status.bleeding"));
            Assert.Contains("This is a creator only command. How did you get this?!", creator.Messages);

            creator.Roles.Creator = true;
            command.DoCommand(creator, new object[] { "bleeding to goblin" });
            Assert.True(victim.HasComponent("status.bleeding"));
            Assert.Contains("Applied bleeding.", creator.Messages);
        }

        [Fact]
        public void AreaFileSearchEntriesAreStoredInTheSearchComponentAndExportBack()
        {
            const string yaml = @"
area:
  schema_version: 1
  prefix: t
  name: Test
rooms:
  - key: hall
    short: a hall
    long: A hall.
    properties:
      - behavior: search
        id: s1
        params:
          target: table
          refresh_ms: 1000
          reward: {short: a coin, long: A coin.}
";
            var store = new FakeStore();
            Assert.True(new AreaImporter(store).Import(yaml).Success);

            var stored = store.Docs["t.hall"];
            Assert.False(stored.Contains("ExtendedProperties"));
            var entry = stored["search"]["entries"].AsBsonArray[0].AsBsonDocument;
            Assert.Equal("table", entry["target"].AsString);
            Assert.Equal(1000, entry["refreshMilliseconds"].AsInt32);
            Assert.Equal("a coin", entry["reward"]["ShortDescription"].AsString);

            var exported = new AreaExporter(store).ExportRoom("t.hall");
            Assert.True(exported.Success, string.Join("; ", exported.Errors));
            Assert.Contains("behavior: search", exported.Yaml);
        }

        [Fact]
        public void AFeatureNoLongerAcceptsProperties()
        {
            const string yaml = @"
area:
  schema_version: 1
  prefix: t
  name: Test
rooms:
  - key: hall
    short: a hall
    long: A hall.
    features:
      - short: a table
        long: A table.
        properties: []
";
            var result = new AreaImporter(new FakeStore()).Import(yaml);

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("unknown key 'properties'"));
        }
    }
}
