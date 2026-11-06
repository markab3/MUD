using System.Collections.Generic;
using MongoDB.Bson;
using MUD.Core;
using MUD.Core.GameObjects;
using MUD.Data;
using Xunit;

namespace MUD.Tests
{
    // Core is the only writer of the player and room documents (D30), so a save writes the whole live object. These tests replace the
    // ones that pinned the website-owned fields and Rev, which no longer exist.
    public class EntitySaveTests
    {
        private class SavedPlayer : Player
        {
            public SavedPlayer(string name, SyncPlayerRepo repo) : base(null, null, repo)
            {
                Id = "p." + name.ToLowerInvariant();
                PlayerName = name; NameKey = name.ToLowerInvariant(); ShortDescription = name; Password = "secret";
                Race = "human"; Gender = "female"; Class = "fighter";
            }
            public override void ReceiveMessage(string message) { }
        }

        private static SavedPlayer StoredPlayer(SyncPlayerRepo repo, string name)
        {
            var player = new SavedPlayer(name, repo);
            repo.Stored[player.Id] = player.ToBsonDocument();
            repo.Live.Add(player);
            return player;
        }

        [Fact]
        public void PlayerSave_SetsEveryFieldExceptTheId_AndNeverWritesRev()
        {
            var repo = new SyncPlayerRepo();
            var player = StoredPlayer(repo, "Ann");
            player.Retired = true;
            player.Roles.Creator = true;
            player.Race = "elf";

            Assert.True(player.Save(true));

            var set = Assert.Single(repo.FieldWrites);
            Assert.False(set.Contains("_id"));
            Assert.False(set.Contains("Rev"));
            Assert.Equal("elf", set["Race"].AsString);
            Assert.True(set["Retired"].AsBoolean);
            Assert.True(set["roles"].AsBsonDocument["creator"].AsBoolean);
            Assert.True(set.Contains("Password"));
            Assert.True(set.Contains("Inventory"));
            Assert.Equal(0, repo.Replaces);
        }

        [Fact]
        public void PlayerSave_AfterAnInGamePromotion_WritesTheRolesWithoutAnyExtraCall()
        {
            var repo = new SyncPlayerRepo();
            var player = StoredPlayer(repo, "Ann");
            player.Roles.Creator = true;
            player.Roles.Admin = true;

            player.Save(true);

            var roles = repo.Stored[player.Id]["roles"].AsBsonDocument;
            Assert.True(roles["creator"].AsBoolean);
            Assert.True(roles["admin"].AsBoolean);
        }

        [Fact]
        public void FirstSaveOfANewPlayer_WritesTheWholeDocument()
        {
            var repo = new SyncPlayerRepo();
            var player = new SavedPlayer("Newbie", repo);
            Assert.True(player.Save(true));
            Assert.Equal(1, repo.Replaces);
            Assert.True(repo.Stored.ContainsKey(player.Id));
        }

        [Fact]
        public void PlayerAndRoomDocuments_CarryNoRev()
        {
            Assert.Null(typeof(Player).GetProperty("Rev"));
            Assert.Null(typeof(Room).GetProperty("Rev"));
            var repo = new SyncRoomRepo();
            var room = new Room(repo) { Id = "t.hall", ShortDescription = "s", LongDescription = "l" };
            Assert.False(room.ToBsonDocument().Contains("Rev"));
        }

        [Fact]
        public void RoomSave_WritesTheLiveRoom_ExceptTheId()
        {
            var repo = new SyncRoomRepo();
            var room = new Room(repo) { Id = "t.hall", ShortDescription = "old", LongDescription = "l" };
            repo.Cache["t.hall"] = room;
            repo.Stored["t.hall"] = room.ToBsonDocument();

            room.LongDescription = "game";
            Assert.True(room.Save());

            Assert.Equal("game", repo.Stored["t.hall"]["LongDescription"].AsString);
            Assert.Equal("t.hall", repo.Stored["t.hall"]["_id"].AsString);
        }

        [Fact]
        public void RoomSave_OfANewRoom_StillInsertsTheWholeDocument()
        {
            var repo = new SyncRoomRepo();
            var room = new Room(repo) { Id = "t.fresh", ShortDescription = "s", LongDescription = "l" };
            Assert.True(room.Save());
            Assert.True(repo.Stored.ContainsKey("t.fresh"));
        }
    }
}
