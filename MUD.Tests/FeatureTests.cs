using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MUD.Core.Commands;
using MUD.Core.GameObjects;
using Xunit;

namespace MUD.Tests
{
    public class FeatureTests
    {
        private static Feature Rocks(string roomLine = "Loose rocks litter the ground.")
        {
            return new Feature
            {
                ShortDescription = "a pile of rocks",
                Handles = new List<string> { "rock" },
                Plurals = new List<string> { "rocks" },
                RoomLine = roomLine,
                DetailDescription = "Grey, jagged rocks."
            };
        }

        private static Feature Table()
        {
            return new Feature
            {
                ShortDescription = "an oak table",
                Handles = new List<string> { "table" },
                Plurals = new List<string> { "tables" },
                RoomLine = "An oak table stands here.",
                DetailDescription = "A scarred oak table."
            };
        }

        private static Room NewRoom(FakeRoomRepo repo, params Feature[] features)
        {
            var room = new Room(repo) { Id = "t.hall", ShortDescription = "a hall", LongDescription = "A bare hall." };
            room.Features = features.ToList();
            return room;
        }

        private static string Describe(Room room)
        {
            return room.Examine(null);
        }

        [Fact]
        public void NoFeaturesLeavesLongDescriptionUnchanged()
        {
            var room = NewRoom(new FakeRoomRepo());
            Assert.StartsWith("a hall (t.hall)\r\nA bare hall.\r\n", Describe(room));
        }

        [Fact]
        public void OneFeatureRoomLineIsAppendedAfterLongDescription()
        {
            var room = NewRoom(new FakeRoomRepo(), Rocks());
            Assert.StartsWith("a hall (t.hall)\r\nA bare hall. Loose rocks litter the ground.\r\n", Describe(room));
        }

        [Fact]
        public void ManyFeaturesAppendInListOrderBeforeExitLine()
        {
            var room = NewRoom(new FakeRoomRepo(), Rocks(), Table());
            var text = Describe(room);
            Assert.StartsWith("a hall (t.hall)\r\nA bare hall. Loose rocks litter the ground. An oak table stands here.\r\n", text);
            Assert.True(text.IndexOf("table stands") < text.IndexOf("obvious exit"));
        }

        [Fact]
        public void EmptyRoomLineAddsNothingButFeatureIsStillLookable()
        {
            var room = NewRoom(new FakeRoomRepo(), Rocks(""), Table());
            Assert.StartsWith("a hall (t.hall)\r\nA bare hall. An oak table stands here.\r\n", Describe(room));
            Assert.True(room.Features[0].IsMatchForString("rock"));
            Assert.Equal("Grey, jagged rocks.", room.Features[0].Examine());
        }

        [Fact]
        public void NullRoomLineAddsNothing()
        {
            var room = NewRoom(new FakeRoomRepo(), Rocks(null));
            Assert.StartsWith("a hall (t.hall)\r\nA bare hall.\r\n", Describe(room));
        }

        [Fact]
        public void FeatureExamineReturnsDetailDescriptionIndependentOfRoomLine()
        {
            var rocks = Rocks();
            Assert.Equal("Grey, jagged rocks.", rocks.Examine());
        }

        [Fact]
        public void ExamineFallsBackToLongDescriptionWhenNoDetail()
        {
            var f = new Feature { LongDescription = "from long" };
            Assert.Equal("from long", f.Examine());
        }

        private static string Look(Player player, string noun)
        {
            var sent = new List<string>();
            var cmd = new LookCommand();
            ((CapturePlayer)player).Sent = sent;
            cmd.DoCommand(player, new object[] { noun });
            return string.Join("|", sent);
        }

        public class CapturePlayer : QuietPlayer
        {
            public List<string> Sent = new List<string>();
            public CapturePlayer(MUD.Core.Repositories.Interfaces.IPlayerRepository repo) : base(repo) { }
            public override void ReceiveMessage(string message) { Sent.Add(message); }
        }

        [Fact]
        public void LookAtFeatureByKeywordAndByPlural()
        {
            var room = NewRoom(new FakeRoomRepo(), Rocks(), Table());
            var player = new CapturePlayer(new FakePlayerRepo()) { CurrentLocation = room };

            Assert.Equal("Grey, jagged rocks.", Look(player, "rock"));
            Assert.Equal("Grey, jagged rocks.", Look(player, "rocks"));
            Assert.Equal("A scarred oak table.", Look(player, "table"));
        }

        [Fact]
        public void SwappingAFeatureChangesBothRoomTextAndLookTarget()
        {
            var room = NewRoom(new FakeRoomRepo(), Rocks());
            var player = new CapturePlayer(new FakePlayerRepo()) { CurrentLocation = room };

            room.Features[0] = Table();

            var text = Describe(room);
            Assert.Contains("An oak table stands here.", text);
            Assert.DoesNotContain("Loose rocks", text);
            Assert.Equal("A scarred oak table.", Look(player, "table"));
            Assert.Equal("There is no rock to look at.", Look(player, "rock"));
        }

        // Shows a different line/detail to a given examiner, and hides entirely from another.
        public class ExaminerAwareFeature : Feature
        {
            public Living Hidden;

            public override string GetRoomLine(Living examiner)
            {
                if (examiner != null && examiner == Hidden) { return ""; }
                return examiner == null ? RoomLine : RoomLine + " (seen by " + examiner.ShortDescription + ")";
            }

            public override string GetDetailDescription(Living examiner)
            {
                if (examiner != null && examiner == Hidden) { return null; }
                return examiner == null ? DetailDescription : DetailDescription + " Examined by " + examiner.ShortDescription + ".";
            }
        }

        [Fact]
        public void FeatureSubclassVariesRoomTextAndLookByExaminer()
        {
            var seer = new CapturePlayer(new FakePlayerRepo()) { ShortDescription = "Seer" };
            var blind = new CapturePlayer(new FakePlayerRepo()) { ShortDescription = "Blind" };
            var feature = new ExaminerAwareFeature
            {
                ShortDescription = "a glyph",
                Handles = new List<string> { "glyph" },
                Plurals = new List<string>(),
                LongDescription = "A faint glyph.",
                RoomLine = "A glyph glows on the floor.",
                DetailDescription = "An intricate glyph.",
                Hidden = blind
            };
            var room = NewRoom(new FakeRoomRepo(), feature);
            seer.CurrentLocation = room;
            blind.CurrentLocation = room;

            Assert.StartsWith("a hall (t.hall)\r\nA bare hall. A glyph glows on the floor. (seen by Seer)\r\n", room.Examine(seer));
            Assert.StartsWith("a hall (t.hall)\r\nA bare hall.\r\n", room.Examine(blind));
            Assert.DoesNotContain("glyph", room.Examine(blind));

            Assert.Equal("An intricate glyph. Examined by Seer.", Look(seer, "glyph"));
            // Hidden detail falls back to LongDescription.
            Assert.Equal("A faint glyph.", Look(blind, "glyph"));
            // Examiner-less Examine() still works.
            Assert.Equal("An intricate glyph.", feature.Examine());
        }

        [Fact]
        public void BaseGettersReturnStoredText()
        {
            var rocks = Rocks();
            Assert.Equal("Loose rocks litter the ground.", rocks.GetRoomLine(null));
            Assert.Equal("Grey, jagged rocks.", rocks.GetDetailDescription(null));
        }

        [Fact]
        public void TowerOfAellioc1RendersFeaturesAsOneParagraphAndIsLookable()
        {
            var room = new MUD.PlanarRealms.Planes.Neutral.Rooms.TowerOfAellioc_1(new FakeRoomRepo()) { Id = "t.tower", ShortDescription = "tower", LongDescription = "A round chamber." };
            Assert.True(room.Features.Count >= 1);
            var firstLine = room.Examine(null).Split(new[] { "\r\n" }, System.StringSplitOptions.None)[1];
            Assert.StartsWith("A round chamber. ", firstLine);
            foreach (var f in room.Features)
            {
                Assert.False(string.IsNullOrWhiteSpace(f.GetRoomLine(null)));
                Assert.EndsWith(".", f.RoomLine);
                Assert.Contains(f.RoomLine, firstLine);
                Assert.False(string.IsNullOrWhiteSpace(f.DetailDescription));
                Assert.NotEmpty(f.Handles);
            }
            var player = new CapturePlayer(new FakePlayerRepo()) { CurrentLocation = room };
            Assert.Equal(room.Features[0].DetailDescription, Look(player, room.Features[0].Handles[0]));
        }

        [Fact]
        public void SavePersistsRuntimeFeatureSwapAndRoundTrips()
        {
            var repo = new FakeRoomRepo();
            var room = NewRoom(repo, Rocks());
            room.Save();

            room.Features[0] = Table();
            Assert.True(room.Save());

            var stored = repo.Docs["t.hall"]["Features"].AsBsonArray;
            Assert.Single(stored);
            Assert.Equal("An oak table stands here.", stored[0].AsBsonDocument["RoomLine"].AsString);

            var reloaded = BsonSerializer.Deserialize<Room>(repo.Docs["t.hall"]);
            var feature = Assert.Single(reloaded.Features);
            Assert.Equal("An oak table stands here.", feature.RoomLine);
            Assert.Equal("A scarred oak table.", feature.DetailDescription);
            Assert.True(feature.IsMatchForString("table"));
        }
    }
}
