using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MUD.Core.Commands;
using MUD.Core.GameObjects;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;
using Xunit;

namespace MUD.Tests
{
    // In-memory repository recording every write, with $set semantics for UpdateFields.
    public class FakeRepo<T> : IRepository<T> where T : Entity
    {
        public Dictionary<string, BsonDocument> Docs = new Dictionary<string, BsonDocument>();
        public int ReplaceCount;
        public int FieldUpdateCount;

        public void Insert(T model) { Docs[model.Id] = model.ToBsonDocument(); }

        public bool Update(T model) { ReplaceCount++; Docs[model.Id] = model.ToBsonDocument(); return true; }

        public bool UpdateFields(string id, BsonDocument set)
        {
            FieldUpdateCount++;
            if (!Docs.ContainsKey(id)) { return false; }
            foreach (var e in set) { Docs[id][e.Name] = e.Value.DeepClone(); }
            return true;
        }

        public bool Delete(T model) { return Docs.Remove(model.Id); }
        public T Get(string id) { return null; }
        public IEnumerable<T> GetAll() { return new T[0]; }
        public IEnumerable<T> Search(Expression<Func<T, bool>> m) { return new T[0]; }
        public BsonDocument GetRawDocument(string id) { return Docs.ContainsKey(id) ? Docs[id].DeepClone().AsBsonDocument : null; }
        public List<string> GetIdsByField(string f, string v) { return new List<string>(); }
        public void UpsertFields(string id, BsonDocument set, BsonDocument setOnInsert) { }
    }

    public class FakeRoomRepo : FakeRepo<Room>, IRoomRepository { }

    public class FakePlayerRepo : FakeRepo<Player>, IPlayerRepository { }

    public class QuietPlayer : Player
    {
        public QuietPlayer(IPlayerRepository repo) : base(null, null, repo) { }
        public override void ReceiveMessage(string message) { }
    }

    public class RuntimeStateTests
    {
        private static InventoryItem Item(string name)
        {
            return new InventoryItem { ShortDescription = name, Handles = new List<string> { name }, Plurals = new List<string>() };
        }

        private static Room StoredRoom(FakeRoomRepo repo, string longDescription = "authored")
        {
            var room = new Room(repo) { Id = "t.hall", ShortDescription = "a hall", LongDescription = longDescription };
            repo.Insert(room);
            return room;
        }

        [Fact]
        public void SaveStateWritesOnlyItemsAndNeverTheDefinitionFields()
        {
            var repo = new FakeRoomRepo();
            var room = StoredRoom(repo);

            // Another writer (the importer) changes a definition field in the store...
            repo.Docs["t.hall"]["LongDescription"] = "edited by import";
            // ...while the live room, unaware, has a stale copy and changes its state.
            room.Items.Add(Item("rope"));

            Assert.True(room.SaveState());

            Assert.Equal(0, repo.ReplaceCount);
            Assert.Equal(1, repo.FieldUpdateCount);
            Assert.Equal("edited by import", repo.Docs["t.hall"]["LongDescription"].AsString);
            Assert.Equal(1, repo.Docs["t.hall"]["Items"].AsBsonArray.Count);
        }

        [Fact]
        public void SaveStateFallsBackToWholeDocumentSaveWhenRoomWasNeverStored()
        {
            var repo = new FakeRoomRepo();
            var room = new Room(repo) { Id = "t.new" };
            room.Items.Add(Item("rope"));

            Assert.True(room.SaveState());

            Assert.Equal(1, repo.ReplaceCount);
            Assert.True(repo.Docs.ContainsKey("t.new"));
        }

        [Fact]
        public void DroppedItemIsPersistedInTheRoomAndRemovedFromThePlayer()
        {
            var roomRepo = new FakeRoomRepo();
            var playerRepo = new FakePlayerRepo();
            var room = StoredRoom(roomRepo);
            var player = new QuietPlayer(playerRepo) { CurrentLocation = room };
            player.Inventory.Add(Item("rope"));

            new DropCommand().DoCommand(player, new object[] { "rope" });

            Assert.Equal(1, roomRepo.Docs["t.hall"]["Items"].AsBsonArray.Count);
            Assert.Equal(0, playerRepo.Docs[player.Id]["Inventory"].AsBsonArray.Count);
        }

        [Fact]
        public void GottenItemIsPersistedInThePlayerAndRemovedFromTheRoom()
        {
            var roomRepo = new FakeRoomRepo();
            var playerRepo = new FakePlayerRepo();
            var room = StoredRoom(roomRepo);
            room.Items.Add(Item("rope"));
            room.SaveState();
            var player = new QuietPlayer(playerRepo) { CurrentLocation = room };

            new GetCommand().DoCommand(player, new object[] { "rope" });

            Assert.Equal(0, roomRepo.Docs["t.hall"]["Items"].AsBsonArray.Count);
            Assert.Equal(1, playerRepo.Docs[player.Id]["Inventory"].AsBsonArray.Count);
        }

        [Fact]
        public void DroppedItemSurvivesRestartAndImporterStyleDefinitionUpdate()
        {
            var roomRepo = new FakeRoomRepo();
            var room = StoredRoom(roomRepo);
            var player = new QuietPlayer(new FakePlayerRepo()) { CurrentLocation = room };
            player.Inventory.Add(Item("rope"));
            new DropCommand().DoCommand(player, new object[] { "rope" });

            // Importer $set of a definition field only.
            roomRepo.Docs["t.hall"]["LongDescription"] = "new text";

            // "Restart": a fresh Room materialised from the stored document.
            var reloaded = BsonSerializer.Deserialize<Room>(roomRepo.Docs["t.hall"]);

            Assert.Equal("new text", reloaded.LongDescription);
            Assert.Single(reloaded.Items);
            Assert.Equal("rope", reloaded.Items[0].ShortDescription);
        }
    }
}
