using System.Linq;
using System.Net;
using System.Net.Sockets;
using MongoDB.Driver;
using MUD.Core;
using MUD.Core.GameObjects;
using MUD.Telnet;
using Xunit;

namespace MUD.Tests
{
    public class PlayerQuitTests
    {
        private static World NewWorld()
        {
            // Nothing contacts MongoDB here: the repository is a fake.
            return new World(new MongoClient("mongodb://127.0.0.1:1"), new WorldSettings());
        }

        // A Client wrapping a real connected loopback socket, so Disconnect() really closes it.
        // The peer is returned too and must stay open for the connection to count as connected.
        private static Client NewConnectedClient(out TcpClient peer)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            peer = new TcpClient();
            peer.Connect(IPAddress.Loopback, port);
            Socket serverSide = listener.AcceptSocket();
            listener.Stop();
            return new Client(serverSide);
        }

        [Fact]
        public void QuitTwiceInOneSessionSavesRemovesAndDisconnectsOnce()
        {
            var world = NewWorld();
            var repo = new FakePlayerRepo();
            var player = new Player(world, null, repo) { Id = "p.tester", PlayerName = "tester", Password = "x" };
            var client = NewConnectedClient(out TcpClient peer);
            int clientDisconnects = 0;
            client.ClientDisconnected += (s, c) => clientDisconnects++;

            try
            {
                world.Players.Add(player);
                player.Connection = client;

                player.Quit();
                player.Quit();

                // Quit's own Disconnect() re-enters Quit through ClientDisconnectedHandler; that re-entry must be a no-op.
                Assert.Equal(1, repo.ReplaceCount);
                Assert.Equal(1, clientDisconnects);
                Assert.DoesNotContain(player, world.Players);
            }
            finally
            {
                peer.Close();
            }
        }

        [Fact]
        public void QuitRunsAgainAfterANewConnectionStartsASession()
        {
            var world = NewWorld();
            var repo = new FakePlayerRepo();
            var player = new Player(world, null, repo) { Id = "p.tester", PlayerName = "tester", Password = "x" };

            var first = NewConnectedClient(out TcpClient firstPeer);
            var second = NewConnectedClient(out TcpClient secondPeer);
            try
            {
                player.Connection = first;
                player.Quit();
                Assert.Equal(1, repo.ReplaceCount);

                // A new session (new connection on the same player object) must be able to quit.
                player.Connection = second;
                player.Quit();

                Assert.Equal(2, repo.ReplaceCount);
            }
            finally
            {
                firstPeer.Close();
                secondPeer.Close();
            }
        }
    }
}
