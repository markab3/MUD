using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MUD.Core;
using MUD.Core.Commands;
using MUD.Core.GameObjects;
using MUD.CoreApi;
using Xunit;

namespace MUD.Tests
{
    [Collection("World construction")] // World registers BSON class maps; constructing it in parallel races
    public class SessionTakeoverTests
    {
        private static World NewWorld()
        {
            return new World(new MongoDB.Driver.MongoClient("mongodb://127.0.0.1:1"), new WorldSettings(), new FakeClock());
        }

        // Runs the game thread's handling of a Disconnected item without needing the loop thread.
        private static void DeliverDisconnect(World world, SessionId client)
        {
            var method = typeof(World).GetMethod("handleInbound", BindingFlags.Instance | BindingFlags.NonPublic);
            method.Invoke(world, new object[] { InboundItem.ForDisconnected(client) });
        }

        // A fake front end and the id of the session opened for it.
        private class Session
        {
            public readonly FakeSink Sink = new FakeSink();
            public readonly SessionId Id;
            public Session(World world) { Id = world.OpenSession("test", "test", Sink); }
            public List<string> Sent { get { return Sink.Sent; } }
            public int Closes { get { return Sink.Events.Count(e => e.StartsWith("close:")); } }
        }

        private static Player LoggedInPlayer(World world, FakePlayerRepo repo, Session session)
        {
            var player = new Player(world, new CommandQueue(new FakeClock()), repo) { Id = "p.tester", PlayerName = "tester", Password = "x" };
            world.AttachPlayer(player, session.Id);
            player.ConnectionStatus = PlayerConnectionStatuses.LoggedIn;
            world.AddPlayer(player);
            return player;
        }

        [Fact]
        public void SecondLoginTakesOverTheSamePlayerAndClosesTheOldConnection()
        {
            var world = NewWorld();
            var repo = new FakePlayerRepo();
            var oldConnection = new Session(world);
            var newConnection = new Session(world);
            var player = LoggedInPlayer(world, repo, oldConnection);

            world.TakeOverSession(player, newConnection.Id);

            Assert.Equal(1, world.Players.Count(p => p.PlayerName == "tester"));
            Assert.Equal(newConnection.Id, player.SessionId);
            Assert.Equal(PlayerConnectionStatuses.LoggedIn, player.ConnectionStatus);
            Assert.Contains("You have been logged in from another location.", oldConnection.Sent);
            Assert.Equal(1, oldConnection.Closes);
            Assert.Contains(newConnection.Sent, s => s.Contains("Reconnecting"));
            Assert.DoesNotContain("Thanks for playing!", oldConnection.Sent);
            Assert.Equal(0, repo.ReplaceCount); // no logout save
        }

        [Fact]
        public void OldConnectionsDisconnectDoesNotQuitOrRemoveTheTakenOverPlayer()
        {
            var world = NewWorld();
            var repo = new FakePlayerRepo();
            var oldConnection = new Session(world);
            var newConnection = new Session(world);
            var player = LoggedInPlayer(world, repo, oldConnection);

            world.TakeOverSession(player, newConnection.Id);
            DeliverDisconnect(world, oldConnection.Id); // the closed old socket reports its disconnect

            Assert.Contains(player, world.Players);
            Assert.Equal(PlayerConnectionStatuses.LoggedIn, player.ConnectionStatus);
            Assert.Equal(0, repo.ReplaceCount);
            Assert.DoesNotContain("Thanks for playing!", newConnection.Sent);
            Assert.Equal(0, newConnection.Closes);
        }

        [Fact]
        public void TheNewConnectionStillLogsTheTakenOverPlayerOutOnDisconnect()
        {
            var world = NewWorld();
            var repo = new FakePlayerRepo();
            var oldConnection = new Session(world);
            var newConnection = new Session(world);
            var player = LoggedInPlayer(world, repo, oldConnection);

            world.TakeOverSession(player, newConnection.Id);
            DeliverDisconnect(world, newConnection.Id);

            Assert.DoesNotContain(player, world.Players);
            Assert.Equal(PlayerConnectionStatuses.Offline, player.ConnectionStatus);
        }
    }
}
