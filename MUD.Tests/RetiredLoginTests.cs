using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MUD.Core;
using MUD.Core.GameObjects;
using MUD.CoreApi;
using MUD.Core.Repositories;
using MUD.Core.Repositories.Interfaces;
using Xunit;

namespace MUD.Tests
{
    // Telnet side of retirement and the login-into-a-missing-room rule (specs/WEB_EDITING.md section 15).
    [Collection("World construction")] // World registers BSON class maps; constructing it in parallel races
    public class RetiredLoginTests
    {
        private class Quiet : Player
        {
            public List<string> Messages = new List<string>();
            public Quiet(World world, IPlayerRepository repo) : base(world, new MUD.Core.Commands.CommandQueue(new FakeClock()), repo) { }
            public override void ReceiveMessage(string message) { Messages.Add(message); }
        }

        private class Fixture
        {
            public World World;
            public SyncPlayerRepo Players = new SyncPlayerRepo();
            public SyncRoomRepo Rooms = new SyncRoomRepo();
            public Quiet Ann;
            public FakeSink Sink = new FakeSink();
            public SessionId Client;

            public Fixture(string startKey = "t.start")
            {
                World = new World(new MongoDB.Driver.MongoClient("mongodb://127.0.0.1:1"), new WorldSettings { StartRoomKey = startKey }, new FakeClock());
                var services = new ServiceCollection()
                    .AddSingleton<IPlayerRepository>(Players)
                    .AddSingleton<IRoomRepository>(Rooms)
                    .BuildServiceProvider();
                typeof(World).GetField("_serviceProvider", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(World, services);

                Client = World.OpenSession("test", "test", Sink);
                Ann = new Quiet(World, Players) { Id = "p.ann", PlayerName = "Ann", NameKey = "ann", Password = "secret", ShortDescription = "Ann", Race = "human" };
                Players.Stored[Ann.Id] = Ann.ToBsonDocument();
                Players.Live.Add(Ann);
            }

            public Room AddRoom(string id)
            {
                var room = new Room(Rooms) { Id = id, ShortDescription = "short " + id, LongDescription = "long " + id, Area = "t" };
                Rooms.Cache[id] = room;
                Rooms.Stored[id] = room.ToBsonDocument();
                return room;
            }
        }

        [Fact]
        public void RetiredCharacter_CannotLogIn_AndIsToldWhy()
        {
            var f = new Fixture();
            f.AddRoom("t.start");
            f.Ann.CurrentLocationId = "t.start";
            f.Players.Stored[f.Ann.Id]["Retired"] = true;
            f.Players.Stored[f.Ann.Id]["Rev"] = 1;

            Assert.False(f.World.DoLogin("Ann", "secret", f.Client));

            Assert.Contains(PlayerAccounts.RetiredMessage, f.Sink.Sent);
            Assert.Equal("This character has been retired and can no longer be played.", PlayerAccounts.RetiredMessage);
            Assert.DoesNotContain(f.Ann, f.World.Players);
            Assert.Equal(PlayerConnectionStatuses.Guest, f.Ann.ConnectionStatus); // never logged in
        }

        [Fact]
        public void RetiredCharacter_WrongPassword_IsNotToldItIsRetired()
        {
            var f = new Fixture();
            f.Players.Stored[f.Ann.Id]["Retired"] = true;

            Assert.False(f.World.DoLogin("Ann", "wrong", f.Client));

            Assert.DoesNotContain(PlayerAccounts.RetiredMessage, f.Sink.Sent);
        }

        [Fact]
        public void RetiredFlagSetWithoutARevBump_IsStillCaughtAtLogin()
        {
            var f = new Fixture();
            f.Players.Stored[f.Ann.Id]["Retired"] = true; // Rev unchanged: a direct database edit

            Assert.False(f.World.DoLogin("Ann", "secret", f.Client));
            Assert.Contains(PlayerAccounts.RetiredMessage, f.Sink.Sent);
        }

        [Fact]
        public void ActiveCharacter_StillLogsIn()
        {
            var f = new Fixture();
            f.AddRoom("t.start");
            f.Ann.CurrentLocationId = "t.start";

            Assert.True(f.World.DoLogin("Ann", "secret", f.Client));

            Assert.Contains(f.Ann, f.World.Players);
            Assert.DoesNotContain(PlayerAccounts.RetiredMessage, f.Sink.Sent);
        }

        [Fact]
        public void UnretiredByAnAdmin_CanLogInAgain()
        {
            var f = new Fixture();
            f.AddRoom("t.start");
            f.Ann.CurrentLocationId = "t.start";
            f.Ann.Retired = true;
            f.Players.Stored[f.Ann.Id]["Retired"] = false;
            f.Players.Stored[f.Ann.Id]["Rev"] = 2;

            Assert.True(f.World.DoLogin("Ann", "secret", f.Client));
            Assert.False(f.Ann.Retired);
        }

        // ---- login into a room that no longer exists ----

        [Fact]
        public void LoginIntoAMissingRoom_MovesToTheDefaultRoom_WithAMessage()
        {
            var f = new Fixture();
            var start = f.AddRoom("t.start");
            f.Ann.CurrentLocationId = "t.deleted";

            Assert.True(f.World.DoLogin("Ann", "secret", f.Client));

            Assert.Equal("t.start", f.Ann.CurrentLocationId);
            Assert.Same(start, f.Ann.CurrentLocation);
            Assert.Contains(f.Ann, start.Occupants);
            Assert.Contains(World.LostLocationMessage, f.Ann.Messages);
        }

        [Fact]
        public void LoginIntoAnExistingRoom_DoesNotMoveOrAnnounce()
        {
            var f = new Fixture();
            f.AddRoom("t.start");
            var hall = f.AddRoom("t.hall");
            f.Ann.CurrentLocationId = "t.hall";

            Assert.True(f.World.DoLogin("Ann", "secret", f.Client));

            Assert.Equal("t.hall", f.Ann.CurrentLocationId);
            Assert.DoesNotContain(World.LostLocationMessage, f.Ann.Messages);
            Assert.Contains(f.Ann, hall.Occupants);
        }

        [Fact]
        public void LoginIntoAMissingRoom_WhenTheDefaultRoomIsMissingToo_LeavesTheLocationAlone()
        {
            var f = new Fixture();
            f.Ann.CurrentLocationId = "t.deleted";

            f.World.DoLogin("Ann", "secret", f.Client);

            Assert.Equal("t.deleted", f.Ann.CurrentLocationId);
            Assert.DoesNotContain(World.LostLocationMessage, f.Ann.Messages);
        }

        [Fact]
        public void LostLocationMessage_IsOneOrTwoSentences()
        {
            var sentences = World.LostLocationMessage.Split(new[] { ". ", "." }, System.StringSplitOptions.RemoveEmptyEntries);
            Assert.InRange(sentences.Length, 1, 2);
        }
    }
}
