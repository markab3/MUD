using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MUD.Core;
using MUD.Core.Commands;
using MUD.Core.Components;
using MUD.Core.GameObjects;
using Xunit;

namespace MUD.Tests
{
    // The hidden button of the sample hall: "search floor" finds it, "press button" opens a timed exit down. Everything runs
    // on fakes (fake clock, fake repositories); there is no Mongo.
    [Collection("World construction")] // World registers BSON class maps; constructing it in parallel races
    public class SecretExitBehaviorTests
    {
        private readonly FakeClock _clock = new FakeClock();

        private class TestWorld : World
        {
            public readonly Dictionary<string, Room> Rooms = new Dictionary<string, Room>();
            public TestWorld(FakeClock clock) : base(new MongoDB.Driver.MongoClient("mongodb://127.0.0.1:1"), new WorldSettings(), clock) { }
            public override Room GetRoom(string roomId) { Room room; return roomId != null && Rooms.TryGetValue(roomId, out room) ? room : null; }
        }

        private class Seeker : Player
        {
            public readonly List<string> Messages = new List<string>();
            public Seeker(World world, CommandQueue queue, FakePlayerRepo repo) : base(world, queue, repo) { }
            public override void ReceiveMessage(string message) { Messages.Add(message); }
        }

        private class Onlooker : Living
        {
            public readonly List<string> Messages = new List<string>();
            public Onlooker(World world, string name) : base(world) { ShortDescription = name; Id = "living." + name; }
            public override void ReceiveMessage(string message) { Messages.Add(message); }
        }

        private class Scene
        {
            public TestWorld World;
            public CommandQueue Queue;
            public Room Hall, Vault;
            public Seeker Ann;
            public Onlooker Bob;     // in the hall
            public Onlooker Carol;   // in the vault
            public FakePlayerRepo Repo;
        }

        private Scene NewScene(int openForSeconds = 60)
        {
            var world = new TestWorld(_clock);
            // The queue the built-in commands use (the world's own), so "search table" runs the real SearchCommand.
            var queue = (CommandQueue)typeof(World).GetField("_commandQueue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(world);

            var hall = new Room(new FakeRoomRepo()) { Id = "t.hall", ShortDescription = "a hall", LongDescription = "A hall." };
            var vault = new Room(new FakeRoomRepo()) { Id = "t.vault", ShortDescription = "a vault", LongDescription = "A vault." };
            vault.Exits.Add(new Exit { Name = "up", DestinationId = "t.hall" });
            world.Rooms[hall.Id] = hall;
            world.Rooms[vault.Id] = vault;

            var config = new BsonDocument
            {
                { "discovery", "hidden_button" }, { "verb", "press" }, { "noun", "button" }, { "dir", "down" }, { "to", "t.vault" },
                { "open_for_s", openForSeconds },
                { "press_msg", "You press the button." },
                { "open_msg", "A section of the floor grinds aside, revealing stairs leading down." },
                { "close_msg", "The floor grinds shut." },
                { "close_dest_msg", "The stairs above seal." }
            };
            hall.Search.Entries.Add(new SearchEntry
            {
                EntryId = "table_search", Target = "table", RefreshMilliseconds = 600000,
                Reward = new InventoryItem { ShortDescription = "a really cool sword", Handles = new List<string> { "sword" }, Plurals = new List<string>() }
            });
            hall.Search.Entries.Add(new SearchEntry
            {
                EntryId = "floor_search", Target = "floor",
                Discovery = new SearchDiscovery { FlagId = "hidden_button", Found = "You search the floor and find a small hidden button!", Again = "You have already found the button here." }
            });
            hall.Behaviors.Add(new BehaviorRef(BehaviorHooks.CommandEntered, SecretExitBehavior.Module, SecretExitBehavior.Type, 0, config));
            hall.Behaviors.Add(new BehaviorRef(BehaviorHooks.Heartbeat, SecretExitBehavior.Module, SecretExitBehavior.Type));

            var repo = new FakePlayerRepo();
            var ann = new Seeker(world, queue, repo) { Id = "p.ann", PlayerName = "ann", ShortDescription = "ann", KnownCommands = new[] { "search", "down", "up", "stop" } };
            hall.EnterRoom(ann, "ann arrives");
            ann.CurrentLocation = hall;
            ann.CurrentLocationId = hall.Id;
            ann.ConnectionStatus = PlayerConnectionStatuses.LoggedIn;
            var bob = new Onlooker(world, "bob");
            hall.EnterRoom(bob, "bob arrives");
            bob.CurrentLocation = hall;
            var carol = new Onlooker(world, "carol");
            vault.EnterRoom(carol, "carol arrives");
            carol.CurrentLocation = vault;
            ann.Messages.Clear(); bob.Messages.Clear(); carol.Messages.Clear();
            return new Scene { World = world, Queue = queue, Hall = hall, Vault = vault, Ann = ann, Bob = bob, Carol = carol, Repo = repo };
        }

        // "search floor", then let the search delay pass and run the delayed result.
        private void FindTheButton(Scene s)
        {
            s.Ann.DataReceivedHandler(null, "search floor");
            _clock.Advance(SearchCommand.SearchDuration);
            s.Queue.Dispatch();
        }

        private void Heartbeat(Scene s) { s.World.Events.DeliverHeartbeat(s.Hall); }

        [Fact]
        public void BehaviorIsRegisteredInTheSharedRegistry()
        {
            Assert.True(InProcessBehaviorRegistry.Shared.TryResolve("secretexit", "SecretExit", out var handler));
            Assert.IsType<SecretExitBehavior>(handler);
        }

        [Fact]
        public void PressBeforeFindingTheButtonIsNotHandled()
        {
            Scene s = NewScene();

            s.Ann.DataReceivedHandler(null, "press button");

            Assert.Equal(new[] { "Command not recognized." }, s.Ann.Messages);
            Assert.False(s.Hall.HasTemporaryExit("down"));
            Assert.Empty(s.Bob.Messages);
        }

        [Fact]
        public void SearchFloorBeginsThenFindsTheButtonAfterTheSearchDelayAndRecordsIt()
        {
            Scene s = NewScene();

            s.Ann.DataReceivedHandler(null, "search floor");

            Assert.Equal(new[] { "You begin your search." }, s.Ann.Messages);
            Assert.False(DiscoveryFlags.Has(s.Ann, "hidden_button"));
            s.Queue.Dispatch(); // not yet: still busy
            Assert.False(DiscoveryFlags.Has(s.Ann, "hidden_button"));

            _clock.Advance(SearchCommand.SearchDuration);
            s.Queue.Dispatch();

            Assert.Contains("You search the floor and find a small hidden button!", s.Ann.Messages);
            Assert.True(DiscoveryFlags.Has(s.Ann, "hidden_button"));
            Assert.DoesNotContain("Command not recognized.", s.Ann.Messages);
            Assert.Contains("ann searches around a bit.", s.Bob.Messages);
        }

        [Fact]
        public void StopCancelsTheFloorSearch()
        {
            Scene s = NewScene();

            s.Ann.DataReceivedHandler(null, "search floor");
            s.Ann.DataReceivedHandler(null, "stop");
            _clock.Advance(SearchCommand.SearchDuration);
            s.Queue.Dispatch();

            Assert.False(DiscoveryFlags.Has(s.Ann, "hidden_button"));
            Assert.DoesNotContain("You search the floor and find a small hidden button!", s.Ann.Messages);
        }

        [Fact]
        public void SearchingTheFloorAgainSaysItWasAlreadyFound()
        {
            Scene s = NewScene();
            FindTheButton(s);
            s.Ann.Messages.Clear();

            s.Ann.DataReceivedHandler(null, "search floor");
            _clock.Advance(SearchCommand.SearchDuration);
            s.Queue.Dispatch();

            Assert.Equal(new[] { "You begin your search.", "You have already found the button here." }, s.Ann.Messages);
        }

        [Fact]
        public void DiscoveryIsPerPlayer()
        {
            Scene s = NewScene();
            FindTheButton(s);
            var dave = new Seeker(s.World, s.Queue, new FakePlayerRepo()) { Id = "p.dave", PlayerName = "dave", ShortDescription = "dave", KnownCommands = new[] { "search" } };
            s.Hall.EnterRoom(dave, "dave arrives");
            dave.CurrentLocation = s.Hall;
            dave.CurrentLocationId = s.Hall.Id;
            dave.ConnectionStatus = PlayerConnectionStatuses.LoggedIn;

            dave.DataReceivedHandler(null, "press button");

            Assert.Equal(new[] { "Command not recognized." }, dave.Messages);
            Assert.False(s.Hall.HasTemporaryExit("down"));
        }

        [Fact]
        public void DiscoveryIsStoredInThePlayerComponentAndSurvivesSaveAndReload()
        {
            Scene s = NewScene();
            FindTheButton(s);

            BsonDocument component = s.Ann.Components["search"]["found"];
            Assert.Equal(new BsonArray { "hidden_button" }, component["ids"]);

            var reloaded = BsonSerializer.Deserialize<Player>(s.Repo.Docs["p.ann"]); // saved when found
            Assert.True(DiscoveryFlags.Has(reloaded, "hidden_button"));
            Assert.False(DiscoveryFlags.Has(reloaded, "other_flag"));
        }

        [Fact]
        public void SearchFloorGivesNoRewardAndHasNoRoomCooldown()
        {
            Scene s = NewScene();
            FindTheButton(s);

            Assert.Empty(s.Ann.Inventory);
            Assert.Null(s.Hall.Search.Entries[1].NextRewardAt);
        }

        [Fact]
        public void SearchTableWhenTheFloorEntryIsSecondStillUsesTheTableEntry()
        {
            Scene s = NewScene();
            s.Hall.Search.Entries.Reverse(); // floor first, table second: the typed target decides, not the position

            s.Ann.DataReceivedHandler(null, "search table");
            _clock.Advance(SearchCommand.SearchDuration);
            s.Queue.Dispatch();

            Assert.Single(s.Ann.Inventory);
        }

        [Fact]
        public void OtherSearchTargetsFallThroughToCore()
        {
            Scene s = NewScene();

            s.Ann.DataReceivedHandler(null, "search ceiling");
            _clock.Advance(SearchCommand.SearchDuration);
            s.Queue.Dispatch();

            Assert.Contains("You begin your search.", s.Ann.Messages);
            Assert.Contains("You finish your search.", s.Ann.Messages);
            Assert.False(DiscoveryFlags.Has(s.Ann, "hidden_button"));
        }

        [Fact]
        public void SwordSearchOnTheTableStillWorks()
        {
            Scene s = NewScene();

            s.Ann.DataReceivedHandler(null, "search table");
            _clock.Advance(SearchCommand.SearchDuration);
            s.Queue.Dispatch();

            Assert.Contains(s.Ann.Messages, m => m.StartsWith("You search the table and find") && m.Contains("really cool sword"));
            Assert.Single(s.Ann.Inventory);
            Assert.False(DiscoveryFlags.Has(s.Ann, "hidden_button")); // the table entry, not the floor entry, answered
        }

        [Fact]
        public void PressOpensTheExitTellsTheRoomAndMovesNobody()
        {
            Scene s = NewScene();
            FindTheButton(s);
            s.Ann.Messages.Clear();

            s.Ann.DataReceivedHandler(null, "press button");

            Assert.Equal(new[] { "You press the button." }, s.Ann.Messages.Where(m => m != "A section of the floor grinds aside, revealing stairs leading down.").ToArray());
            Assert.Contains("A section of the floor grinds aside, revealing stairs leading down.", s.Bob.Messages);
            Assert.True(s.Hall.HasTemporaryExit("down"));
            Assert.Same(s.Hall, s.Ann.CurrentLocation);
            Assert.Contains(s.Ann, s.Hall.Occupants);
            Assert.Empty(s.Carol.Messages.Where(m => m.Contains("floor")));
            Assert.DoesNotContain(s.Hall.Exits, e => e.Name == "down"); // never a stored exit
        }

        [Fact]
        public void PlayerCanUseTheOpenExitAndComeBackUp()
        {
            Scene s = NewScene();
            FindTheButton(s);
            s.Ann.DataReceivedHandler(null, "press button");

            s.Ann.DataReceivedHandler(null, "down");
            s.Queue.Dispatch();

            Assert.Same(s.Vault, s.Ann.CurrentLocation);
            Assert.Contains(s.Ann, s.Vault.Occupants);

            s.Ann.DataReceivedHandler(null, "up");
            s.Queue.Dispatch();
            Assert.Same(s.Hall, s.Ann.CurrentLocation);
        }

        [Fact]
        public void DownIsNotAnExitBeforeThePress()
        {
            Scene s = NewScene();
            FindTheButton(s);
            s.Ann.Messages.Clear();

            s.Ann.DataReceivedHandler(null, "down");
            s.Queue.Dispatch();

            Assert.Same(s.Hall, s.Ann.CurrentLocation);
            Assert.Contains("Command not recognized.", s.Ann.Messages);
        }

        [Fact]
        public void PressWhileOpenSaysAlreadyOpenAndDoesNotExtendTheTime()
        {
            Scene s = NewScene(60);
            FindTheButton(s);
            s.Ann.DataReceivedHandler(null, "press button");
            _clock.Advance(TimeSpan.FromSeconds(50));
            s.Ann.Messages.Clear();
            s.Bob.Messages.Clear();

            s.Ann.DataReceivedHandler(null, "press button");

            Assert.Equal(new[] { "The way down is already open." }, s.Ann.Messages);
            Assert.Empty(s.Bob.Messages);
            _clock.Advance(TimeSpan.FromSeconds(10));
            Heartbeat(s);
            Assert.False(s.Hall.HasTemporaryExit("down")); // still closed 60 s after the first press
        }

        [Fact]
        public void TheExitClosesAfterOpenForSecondsAndBothRoomsAreTold()
        {
            Scene s = NewScene(60);
            FindTheButton(s);
            s.Ann.DataReceivedHandler(null, "press button");
            s.Bob.Messages.Clear();

            _clock.Advance(TimeSpan.FromSeconds(59));
            Heartbeat(s);
            Assert.True(s.Hall.HasTemporaryExit("down"));
            Assert.Empty(s.Bob.Messages);

            _clock.Advance(TimeSpan.FromSeconds(1));
            Heartbeat(s);

            Assert.False(s.Hall.HasTemporaryExit("down"));
            Assert.Contains("The floor grinds shut.", s.Bob.Messages);
            Assert.Contains("The stairs above seal.", s.Carol.Messages);
        }

        [Fact]
        public void OnceClosedTheExitCannotBeUsedAndCanBeOpenedAgain()
        {
            Scene s = NewScene(5);
            FindTheButton(s);
            s.Ann.DataReceivedHandler(null, "press button");
            _clock.Advance(TimeSpan.FromSeconds(5));
            Heartbeat(s);
            s.Ann.Messages.Clear();

            s.Ann.DataReceivedHandler(null, "down");
            s.Queue.Dispatch();
            Assert.Same(s.Hall, s.Ann.CurrentLocation);
            Assert.Contains("Command not recognized.", s.Ann.Messages);

            s.Ann.DataReceivedHandler(null, "press button");
            Assert.True(s.Hall.HasTemporaryExit("down"));
        }

        [Fact]
        public void TheTemporaryExitIsNeverPartOfTheStoredRoom()
        {
            Scene s = NewScene();
            FindTheButton(s);
            s.Ann.DataReceivedHandler(null, "press button");

            BsonDocument stored = s.Hall.ToBsonDocument();

            Assert.DoesNotContain(stored["Exits"].AsBsonArray, e => e["Name"] == "down");
            Assert.Contains("down", s.Hall.Examine(s.Ann));
            Assert.True(s.Hall.Exits.Count == 0);
        }

        [Fact]
        public void PressIsNotHandledOutsideTheRoomEvenAfterFindingIt()
        {
            Scene s = NewScene();
            FindTheButton(s);
            // Moved to a room that has no such behavior: nothing takes the line.
            s.Hall.ExitRoom(s.Ann);
            s.Vault.EnterRoom(s.Ann);
            s.Ann.CurrentLocation = s.Vault;
            s.Ann.CurrentLocationId = s.Vault.Id;
            s.Ann.Messages.Clear();

            s.Ann.DataReceivedHandler(null, "press button");

            Assert.Equal(new[] { "Command not recognized." }, s.Ann.Messages);
            Assert.False(s.Hall.HasTemporaryExit("down"));
        }

        [Fact]
        public void SampleAreaHallHasBothSearchEntriesTheButtonBehaviorAndTheOwnersTexts()
        {
            string path = null;
            for (var d = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory()); d != null && path == null; d = d.Parent)
            {
                var candidate = System.IO.Path.Combine(d.FullName, "MUD.Main", "Content", "sample_area.yaml");
                if (System.IO.File.Exists(candidate)) { path = candidate; }
            }
            Assert.NotNull(path);
            var store = new FakeStore();
            var result = new MUD.Core.Import.AreaImporter(store).Import(System.IO.File.ReadAllText(path));
            Assert.True(result.Success, string.Join("; ", result.Errors));

            BsonDocument hall = store.Docs["sample_area.hall"];
            Assert.Equal("Pale light spills through a high window onto a worn stone floor.", hall["LongDescription"].AsString);
            var entries = hall["search"]["entries"].AsBsonArray;
            Assert.Equal(new[] { "table", "floor" }, entries.Select(e => e["target"].AsString).ToArray());
            Assert.Equal("hidden_button", entries[1]["discovery"]["id"].AsString);
            Assert.Contains(hall["behaviors"].AsBsonArray, b => b["hook"] == "command.entered" && b["config"]["discovery"] == "hidden_button");

            BsonDocument vault = store.Docs["sample_area.secret_vault"];
            Assert.Equal("Inside a Dark Stone Vault", vault["ShortDescription"].AsString);
            Assert.StartsWith("This is the inside of a stone vault, left abandoned for countless years.", vault["LongDescription"].AsString);
            Assert.EndsWith("looms oppressively above.", vault["LongDescription"].AsString);
            Assert.Equal(-1, vault["Z"].ToInt32());
        }
    }
}
