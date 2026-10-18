using System.Linq;
using MUD.Core;
using MUD.Core.Commands;
using MUD.Core.GameObjects;
using MUD.Core.Properties.PlayerProperties;
using Xunit;

namespace MUD.Tests
{
    public class BuildFeatureTests
    {
        private class Rig
        {
            public FakeRoomRepo Repo = new FakeRoomRepo();
            public Room Room;
            public RecordingLiving Creator;
            public BuildCommand Command;

            public Rig(bool creator = true)
            {
                Room = new Room(Repo) { Id = "t.hall", ShortDescription = "a hall", LongDescription = "A bare hall." };
                Repo.Insert(Room);
                var world = TestWorld.Create();
                Creator = new RecordingLiving(world) { CurrentLocation = Room };
                if (creator) { Creator.ExtendedProperties.Add(new CreatorProperty()); }
                Command = new BuildCommand(Repo, new WorldSettings());
            }

            public string Run(string input)
            {
                Creator.Messages.Clear();
                Command.DoCommand(Creator, Command.ParseCommand(Creator, input));
                return string.Join("\n", Creator.Messages);
            }
        }

        [Fact]
        public void AddCreatesFeatureAndSaves()
        {
            var rig = new Rig();
            rig.Run("build feature add stone table");
            var f = Assert.Single(rig.Room.Features);
            Assert.Equal("stone table", f.ShortDescription);
            Assert.Equal(new[] { "table" }, f.Handles);
            Assert.Equal("t.hall#stone table", f.Id);
            // DELIBERATE CHANGE (specs/WEB_EDITING.md section 9): Room.Save of a stored room is now a field-level $set that
            // leaves Rev alone, instead of a whole-document replace that could roll back a website edit.
            Assert.Equal(0, rig.Repo.ReplaceCount);
            Assert.Equal(1, rig.Repo.FieldUpdateCount);
            Assert.Single(rig.Repo.Docs["t.hall"]["Features"].AsBsonArray);
        }

        [Fact]
        public void AddDuplicateIsRejected()
        {
            var rig = new Rig();
            rig.Run("build feature add stone table");
            var msg = rig.Run("build feature add STONE TABLE");
            Assert.Contains("already has", msg);
            Assert.Single(rig.Room.Features);
        }

        [Fact]
        public void SetUpdatesEachField()
        {
            var rig = new Rig();
            rig.Run("build feature add stone table");
            rig.Run("build feature set stone table = roomline A stone table stands here.");
            rig.Run("build feature set stone table = detail Cold and heavy.");
            rig.Run("build feature set stone table = long A big table.");
            rig.Run("build feature set stone table = handles table slab");
            rig.Run("build feature set stone table = adjectives stone");
            rig.Run("build feature set stone table = plurals tables");
            var f = rig.Room.Features.Single();
            Assert.Equal("A stone table stands here.", f.RoomLine);
            Assert.Equal("Cold and heavy.", f.DetailDescription);
            Assert.Equal("A big table.", f.LongDescription);
            Assert.Equal(new[] { "table", "slab" }, f.Handles);
            Assert.Equal(new[] { "stone" }, f.Adjectives);
            Assert.Equal(new[] { "tables" }, f.Plurals);
            Assert.Contains("A stone table stands here.", rig.Room.Examine(null));
            Assert.Equal("A stone table stands here.", rig.Repo.Docs["t.hall"]["Features"][0]["RoomLine"].AsString);
        }

        [Fact]
        public void SetRoomLineWithNoTextClearsIt()
        {
            var rig = new Rig();
            rig.Run("build feature add rock");
            rig.Run("build feature set rock = roomline Rocks.");
            rig.Run("build feature set rock = roomline");
            Assert.Null(rig.Room.Features.Single().RoomLine);
        }

        [Fact]
        public void SetShortRenamesAndRejectsCollision()
        {
            var rig = new Rig();
            rig.Run("build feature add rock");
            rig.Run("build feature add tree");
            Assert.Contains("already has", rig.Run("build feature set rock = short tree"));
            rig.Run("build feature set rock = short boulder");
            Assert.Equal("boulder", rig.Room.Features[0].ShortDescription);
            Assert.Equal("t.hall#boulder", rig.Room.Features[0].Id);
        }

        [Fact]
        public void SetUnknownFeatureOrFieldReportsError()
        {
            var rig = new Rig();
            rig.Run("build feature add rock");
            Assert.Contains("no feature", rig.Run("build feature set tree = long x"));
            Assert.Contains("Unknown field", rig.Run("build feature set rock = colour red"));
            Assert.Contains("Format", rig.Run("build feature set rock"));
        }

        [Fact]
        public void RemoveDeletesFeatureAndSaves()
        {
            var rig = new Rig();
            rig.Run("build feature add rock");
            rig.Run("build feature add tree");
            rig.Run("build feature remove ROCK");
            Assert.Equal("tree", rig.Room.Features.Single().ShortDescription);
            Assert.Single(rig.Repo.Docs["t.hall"]["Features"].AsBsonArray);
            Assert.Contains("no feature", rig.Run("build feature remove rock"));
        }

        [Fact]
        public void ListShowsFeatures()
        {
            var rig = new Rig();
            Assert.Contains("no features", rig.Run("build feature list"));
            rig.Run("build feature add rock");
            Assert.Contains("rock", rig.Run("build feature list"));
        }

        [Fact]
        public void NonCreatorCannotEdit()
        {
            var rig = new Rig(creator: false);
            rig.Run("build feature add rock");
            Assert.Empty(rig.Room.Features);
        }

        [Fact]
        public void UnknownVerbShowsUsage()
        {
            var rig = new Rig();
            Assert.Contains("Usage", rig.Run("build feature"));
        }
    }
}
