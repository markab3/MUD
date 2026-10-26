using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using MongoDB.Bson;
using MUD.Core;
using MUD.Core.GameObjects;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;
using Xunit;

namespace MUD.Tests
{
    // A room repository with a live cache and a stored copy, like the real one but in memory.
    public class SyncRoomRepo : IRoomRepository, ISnapshotRepository
    {
        public Dictionary<string, Room> Cache = new Dictionary<string, Room>();
        public Dictionary<string, BsonDocument> Stored = new Dictionary<string, BsonDocument>();
        public List<string> Evicted = new List<string>();

        public Room Get(string id) { Room r; return id != null && Cache.TryGetValue(id, out r) ? r : null; }
        public BsonDocument GetRawDocument(string id) { BsonDocument d; return id != null && Stored.TryGetValue(id, out d) ? d.DeepClone().AsBsonDocument : null; }
        public bool UpdateFields(string id, BsonDocument set)
        {
            if (!Stored.ContainsKey(id)) { return false; }
            foreach (var e in set) { Stored[id][e.Name] = e.Value.DeepClone(); }
            return true;
        }
        public void Insert(Room model) { Stored[model.Id] = model.ToBsonDocument(); Cache[model.Id] = model; }
        public bool Update(Room model) { Stored[model.Id] = model.ToBsonDocument(); return true; }
        public bool Delete(Room model) { return Stored.Remove(model.Id); }
        public IEnumerable<Room> GetAll() { return Cache.Values; }
        public IEnumerable<Room> Search(Expression<Func<Room, bool>> m) { return new Room[0]; }
        public List<string> GetIdsByField(string f, string v) { return new List<string>(); }
        public void UpsertFields(string id, BsonDocument set, BsonDocument setOnInsert) { }

        public string CollectionName { get { return "room"; } }
        public IPersistSink Sink { get; set; }
        public bool ReplaceDocument(string id, BsonDocument document) { Stored[id] = document; return true; }
        public bool SetFields(string id, BsonDocument set) { return UpdateFields(id, set); }
        public void Evict(string id) { Evicted.Add(id); Cache.Remove(id); }
        public List<string> CachedIds() { return Cache.Keys.ToList(); }
        public Dictionary<string, int> GetRevisions(IEnumerable<string> ids)
        {
            var result = new Dictionary<string, int>();
            foreach (var id in ids) { if (Stored.ContainsKey(id)) { result[id] = Room.StoredRev(Stored[id]); } }
            return result;
        }
    }

    public class RoomWebSyncTests
    {
        private class Listener : Player
        {
            public List<string> Messages = new List<string>();
            public Listener(string name) : base(null, null, new FakePlayerRepo()) { PlayerName = name; NameKey = name.ToLowerInvariant(); ShortDescription = name; }
            public override void ReceiveMessage(string message) { Messages.Add(message); }
        }

        private class Npc : NPC
        {
            public List<string> Messages = new List<string>();
            public Npc(string name) : base(null) { ShortDescription = name; }
            public override void ReceiveMessage(string message) { Messages.Add(message); }
        }

        private class Fixture
        {
            public SyncRoomRepo Repo = new SyncRoomRepo();
            public RoomWebSync Sync;
            public List<string> Log = new List<string>();
            public string Start = "t.start";

            public Fixture(Func<int, int> random = null)
            {
                Sync = new RoomWebSync(Repo, () => Start, random ?? (n => 0), Log.Add);
            }

            public Room AddRoom(string id, int rev = 0, params string[] exitsTo)
            {
                var room = new Room(Repo)
                {
                    Id = id, ShortDescription = "short " + id, LongDescription = "long " + id, Area = "t", X = 0, Y = 0, Z = 0, Rev = rev,
                    Exits = exitsTo.Select((to, i) => new Exit { Name = "exit" + i, DestinationId = to }).ToList()
                };
                Repo.Cache[id] = room;
                Repo.Stored[id] = room.ToBsonDocument();
                return room;
            }

            public T Place<T>(T living, Room room) where T : Living
            {
                living.CurrentLocation = room;
                living.CurrentLocationId = room.Id;
                room.Occupants.Add(living);
                return living;
            }

            public void PollAndApply() { Sync.Apply(Sync.Poll()); }
        }

        // ---- web edit ----

        [Fact]
        public void ChangedRev_AppliesAllowListedFields_AndTellsTheRoom()
        {
            var f = new Fixture();
            var room = f.AddRoom("t.hall", 0, "t.study");
            f.AddRoom("t.study");
            var player = f.Place(new Listener("Ann"), room);

            var doc = f.Repo.Stored["t.hall"];
            doc["ShortDescription"] = "a new hall";
            doc["LongDescription"] = "New long.";
            doc["X"] = BsonNull.Value;
            doc["Exits"] = new BsonArray { new BsonDocument { { "Name", "west" }, { "DestinationId", "t.study" } } };
            doc["Rev"] = 1;

            f.PollAndApply();

            Assert.Equal("a new hall", room.ShortDescription);
            Assert.Equal("New long.", room.LongDescription);
            Assert.Null(room.X);
            Assert.Equal(0, room.Y);
            Assert.Equal("west", room.Exits.Single().Name);
            Assert.Equal(1, room.Rev);
            Assert.Equal(new[] { RoomWebSync.UpdatedMessage }, player.Messages.ToArray());
        }

        [Fact]
        public void ChangedRev_RebuildsExitCommands()
        {
            var f = new Fixture();
            var room = f.AddRoom("t.hall", 0, "t.study");
            f.AddRoom("t.study");
            Assert.NotNull(room.RoomCommandSource.GetCommandFromInput("exit0"));

            f.Repo.Stored["t.hall"]["Exits"] = new BsonArray { new BsonDocument { { "Name", "up" }, { "DestinationId", "t.study" } } };
            f.Repo.Stored["t.hall"]["Rev"] = 1;
            f.PollAndApply();

            Assert.Null(room.RoomCommandSource.GetCommandFromInput("exit0"));
            Assert.NotNull(room.RoomCommandSource.GetCommandFromInput("up"));
        }

        [Fact]
        public void ChangedRev_NeverTouchesItemsOccupantsFeaturesOrHandles()
        {
            var f = new Fixture();
            var room = f.AddRoom("t.hall");
            var sword = new InventoryItem { ShortDescription = "a sword", Handles = new List<string> { "sword" }, Plurals = new List<string>() };
            room.Items.Add(sword);
            room.Handles = new List<string> { "hall" };
            room.Features = new List<Feature> { new Feature { ShortDescription = "a table" } };
            var player = f.Place(new Listener("Ann"), room);

            // The stored copy is stale or different in every state field; only the allow-list may be applied.
            var doc = f.Repo.Stored["t.hall"];
            doc["Items"] = new BsonArray();
            doc["Handles"] = new BsonArray();
            doc["Features"] = new BsonArray();
            doc["ShortDescription"] = "changed";
            doc["Rev"] = 5;

            f.PollAndApply();

            Assert.Equal("changed", room.ShortDescription);
            Assert.Same(sword, Assert.Single(room.Items));
            Assert.Equal(new[] { "hall" }, room.Handles.ToArray());
            Assert.Single(room.Features);
            Assert.Same(player, Assert.Single(room.Occupants));
        }

        [Fact]
        public void UnchangedRev_AppliesNothing_AndSaysNothing()
        {
            var f = new Fixture();
            var room = f.AddRoom("t.hall", 4);
            var player = f.Place(new Listener("Ann"), room);
            f.Repo.Stored["t.hall"]["ShortDescription"] = "changed without a Rev bump";

            f.PollAndApply();

            Assert.Equal("short t.hall", room.ShortDescription);
            Assert.Empty(player.Messages);
        }

        [Fact]
        public void SecondPollAfterApply_DoesNothingMore()
        {
            var f = new Fixture();
            var room = f.AddRoom("t.hall");
            var player = f.Place(new Listener("Ann"), room);
            f.Repo.Stored["t.hall"]["ShortDescription"] = "changed";
            f.Repo.Stored["t.hall"]["Rev"] = 1;
            f.PollAndApply();
            f.PollAndApply();
            Assert.Single(player.Messages);
        }

        [Fact]
        public void MissingRevInStoredDocument_IsRevZero()
        {
            var f = new Fixture();
            var room = f.AddRoom("t.hall", 0);
            f.Repo.Stored["t.hall"].Remove("Rev");
            f.PollAndApply();
            Assert.Equal(0, room.Rev);
        }

        [Fact]
        public void Poll_ReadsOnlyLiveRooms()
        {
            var f = new Fixture();
            f.AddRoom("t.live");
            f.Repo.Stored["t.cold"] = new BsonDocument { { "_id", "t.cold" }, { "Rev", 9 } };
            var result = f.Sync.Poll();
            Assert.Equal(new[] { "t.live" }, result.Asked.ToArray());
            Assert.DoesNotContain("t.cold", result.Revisions.Keys);
        }

        // ---- poll scheduling ----

        [Fact]
        public void IsDue_FiresOncePerFiveSeconds()
        {
            var f = new Fixture();
            var t = new DateTime(2026, 1, 1, 12, 0, 0);
            Assert.True(f.Sync.IsDue(t));
            Assert.False(f.Sync.IsDue(t.AddSeconds(4.9)));
            Assert.True(f.Sync.IsDue(t.AddSeconds(5)));
            Assert.Equal(TimeSpan.FromSeconds(5), RoomWebSync.PollInterval);
        }

        // ---- deletion ----

        [Fact]
        public void Deleted_MovesEverythingToARandomAdjacentRoom_AndAnnouncesIt()
        {
            var f = new Fixture(n => n - 1); // pick the last candidate
            var dead = f.AddRoom("t.dead", 0, "t.a", "t.b", "t.dead");
            var a = f.AddRoom("t.a");
            var b = f.AddRoom("t.b");
            f.AddRoom("t.start");
            var ann = f.Place(new Listener("Ann"), dead);
            var goblin = f.Place(new Npc("a goblin"), dead);
            var bystander = f.Place(new Listener("Bea"), b);
            dead.Items.Add(new InventoryItem { ShortDescription = "a lamp", Handles = new List<string> { "lamp" }, Plurals = new List<string>() });
            f.Repo.Stored.Remove("t.dead");

            f.PollAndApply();

            Assert.Equal("t.b", ann.CurrentLocationId);
            Assert.Same(b, ann.CurrentLocation);
            Assert.Same(b, goblin.CurrentLocation);
            Assert.Contains(ann, b.Occupants);
            Assert.Contains(goblin, b.Occupants);
            Assert.Empty(dead.Occupants);
            Assert.Equal("a lamp", Assert.Single(b.Items).ShortDescription);
            Assert.Empty(dead.Items);
            Assert.Empty(a.Occupants);
            var expected = "A sudden draft of magical wind deposits Ann, a goblin and a lamp in the room!";
            Assert.Contains(expected, bystander.Messages);
            Assert.Contains(expected, ann.Messages);
            Assert.Contains("t.dead", f.Repo.Evicted);
        }

        [Fact]
        public void Deleted_WithNoUsableExit_GoesToTheDefaultRoom()
        {
            var f = new Fixture();
            var dead = f.AddRoom("t.dead", 0, "t.dead", "t.gone"); // self exit and an exit to a room that does not exist
            var start = f.AddRoom("t.start");
            var ann = f.Place(new Listener("Ann"), dead);
            f.Repo.Stored.Remove("t.dead");

            f.PollAndApply();

            Assert.Same(start, ann.CurrentLocation);
            Assert.Equal("t.start", ann.CurrentLocationId);
        }

        [Fact]
        public void Deleted_WithNoNeighbourAndNoDefaultRoom_MovesNothing()
        {
            var f = new Fixture();
            var dead = f.AddRoom("t.dead");
            var ann = f.Place(new Listener("Ann"), dead);
            f.Repo.Stored.Remove("t.dead");

            f.PollAndApply();
            f.PollAndApply(); // logged once only

            Assert.Same(dead, ann.CurrentLocation);
            Assert.Contains(ann, dead.Occupants);
            Assert.Single(f.Log);
        }

        [Fact]
        public void Deleted_RoomThatIsTheDefaultRoom_NeverMovesPeopleIntoItself()
        {
            var f = new Fixture();
            f.Start = "t.dead";
            var dead = f.AddRoom("t.dead");
            var ann = f.Place(new Listener("Ann"), dead);
            f.Repo.Stored.Remove("t.dead");
            f.PollAndApply();
            Assert.Same(dead, ann.CurrentLocation);
        }

        [Fact]
        public void Deleted_EmptyRoom_SaysNothing()
        {
            var f = new Fixture();
            f.AddRoom("t.dead", 0, "t.a");
            var a = f.AddRoom("t.a");
            var bystander = f.Place(new Listener("Bea"), a);
            f.Repo.Stored.Remove("t.dead");
            f.PollAndApply();
            Assert.Empty(bystander.Messages);
        }

        // ---- Room.Save keeps its hands off Rev ----

        [Fact]
        public void RoomSave_DoesNotWriteRev_SoItCannotRollBackAWebEdit()
        {
            var repo = new SyncRoomRepo();
            var room = new Room(repo) { Id = "t.hall", ShortDescription = "old", LongDescription = "l", Rev = 1 };
            repo.Cache["t.hall"] = room;
            repo.Stored["t.hall"] = room.ToBsonDocument();

            // The website edits and bumps Rev while the game holds a stale copy...
            repo.Stored["t.hall"]["ShortDescription"] = "web";
            repo.Stored["t.hall"]["Rev"] = 2;
            // ...and the game saves an in-game change.
            room.LongDescription = "game";
            Assert.True(room.Save());

            Assert.Equal(2, repo.Stored["t.hall"]["Rev"].AsInt32);
            Assert.Equal("game", repo.Stored["t.hall"]["LongDescription"].AsString);
        }

        [Fact]
        public void RoomSave_OfANewRoom_StillInsertsTheWholeDocument()
        {
            var repo = new SyncRoomRepo();
            var room = new Room(repo) { Id = "t.fresh", ShortDescription = "s", LongDescription = "l" };
            Assert.True(room.Save());
            Assert.True(repo.Stored.ContainsKey("t.fresh"));
        }

        [Fact]
        public void ApplyWebEdit_IgnoresWrongTypedValues()
        {
            var room = new Room(new SyncRoomRepo()) { Id = "t.a", ShortDescription = "keep", LongDescription = "keep" };
            room.ApplyWebEdit(new BsonDocument { { "ShortDescription", 5 }, { "Exits", "oops" }, { "X", "x" }, { "Rev", "nope" } });
            Assert.Equal("keep", room.ShortDescription);
            Assert.Empty(room.Exits);
            Assert.Null(room.X);
            Assert.Equal(0, room.Rev);
        }
    }
}
