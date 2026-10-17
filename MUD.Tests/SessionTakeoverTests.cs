using System.Linq;
using System.Reflection;
using MUD.Core;
using MUD.Core.Commands;
using MUD.Core.GameObjects;
using MUD.Telnet;
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
        private static void DeliverDisconnect(World world, IClientConnection client)
        {
            var method = typeof(World).GetMethod("handleInbound", BindingFlags.Instance | BindingFlags.NonPublic);
            method.Invoke(world, new object[] { InboundItem.ForDisconnected(client) });
        }

        private static Player LoggedInPlayer(World world, FakePlayerRepo repo, FakeConnection connection)
        {
            var player = new Player(world, new CommandQueue(new FakeClock()), repo) { Id = "p.tester", PlayerName = "tester", Password = "x" };
            player.Connection = connection;
            player.ConnectionStatus = PlayerConnectionStatuses.LoggedIn;
            world.AddPlayer(player);
            return player;
        }

        [Fact]
        public void SecondLoginTakesOverTheSamePlayerAndClosesTheOldConnection()
        {
            var world = NewWorld();
            var repo = new FakePlayerRepo();
            var oldConnection = new FakeConnection();
            var newConnection = new FakeConnection();
            var player = LoggedInPlayer(world, repo, oldConnection);

            world.TakeOverSession(player, newConnection);

            Assert.Equal(1, world.Players.Count(p => p.PlayerName == "tester"));
            Assert.Same(newConnection, player.Connection);
            Assert.Equal(PlayerConnectionStatuses.LoggedIn, player.ConnectionStatus);
            Assert.Contains("You have been logged in from another location.", oldConnection.Sent);
            Assert.Equal(1, oldConnection.Events.Count(e => e == "disconnect"));
            Assert.Contains(newConnection.Sent, s => s.Contains("Reconnecting"));
            Assert.DoesNotContain("Thanks for playing!", oldConnection.Sent);
            Assert.Equal(0, repo.ReplaceCount); // no logout save
        }

        [Fact]
        public void OldConnectionsDisconnectDoesNotQuitOrRemoveTheTakenOverPlayer()
        {
            var world = NewWorld();
            var repo = new FakePlayerRepo();
            var oldConnection = new FakeConnection();
            var newConnection = new FakeConnection();
            var player = LoggedInPlayer(world, repo, oldConnection);

            world.TakeOverSession(player, newConnection);
            DeliverDisconnect(world, oldConnection); // the closed old socket reports its disconnect

            Assert.Contains(player, world.Players);
            Assert.Equal(PlayerConnectionStatuses.LoggedIn, player.ConnectionStatus);
            Assert.Equal(0, repo.ReplaceCount);
            Assert.DoesNotContain("Thanks for playing!", newConnection.Sent);
            Assert.Equal(0, newConnection.Events.Count(e => e == "disconnect"));
        }

        [Fact]
        public void TheNewConnectionStillLogsTheTakenOverPlayerOutOnDisconnect()
        {
            var world = NewWorld();
            var repo = new FakePlayerRepo();
            var oldConnection = new FakeConnection();
            var newConnection = new FakeConnection();
            var player = LoggedInPlayer(world, repo, oldConnection);

            world.TakeOverSession(player, newConnection);
            DeliverDisconnect(world, newConnection);

            Assert.DoesNotContain(player, world.Players);
            Assert.Equal(PlayerConnectionStatuses.Offline, player.ConnectionStatus);
        }
    }
}
