using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MUD.Telnet;
using Xunit;

namespace MUD.Tests
{
    public class ServerLifecycleTests
    {
        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        // The server thread may not be listening yet, so retry until the connection succeeds.
        private static TcpClient ConnectWithRetry(int port)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                var tcp = new TcpClient();
                try
                {
                    tcp.Connect(IPAddress.Loopback, port);
                    return tcp;
                }
                catch (SocketException) when (DateTime.UtcNow < deadline)
                {
                    tcp.Close();
                    Thread.Sleep(50);
                }
            }
        }

        [Fact]
        public void StopDisconnectsEveryClientWithoutThrowing()
        {
            int port = FreePort();
            var server = new Server(IPAddress.Loopback, port);
            var allConnected = new CountdownEvent(3);
            var stopped = new ManualResetEventSlim(false);
            int disconnected = 0;

            server.ClientConnected += (s, c) => allConnected.Signal();
            server.ClientDisconnected += (s, c) => Interlocked.Increment(ref disconnected);
            server.ServerStopped += (s, e) => stopped.Set();

            var serverThread = new Thread(server.Start) { IsBackground = true };
            serverThread.Start();

            var peers = new List<TcpClient>();
            try
            {
                for (int i = 0; i < 3; i++)
                {
                    peers.Add(ConnectWithRetry(port));
                }

                Assert.True(allConnected.Wait(TimeSpan.FromSeconds(10)), "server did not register all three clients");

                Exception stopException = Record.Exception(() => server.Stop());

                Assert.Null(stopException);
                Assert.True(stopped.Wait(TimeSpan.FromSeconds(10)), "accept loop did not exit after Stop");
                // Stop disconnects with a flush (the shutdown text is written first), so the closes complete just after it returns.
                for (int i = 0; i < 200 && Volatile.Read(ref disconnected) < 3; i++) { Thread.Sleep(25); }
                Assert.Equal(3, Volatile.Read(ref disconnected));
            }
            finally
            {
                foreach (var peer in peers)
                {
                    peer.Close();
                }
            }
        }

        [Fact]
        public void StopClosesTheListenerSoNewConnectionsAreRefused()
        {
            int port = FreePort();
            var server = new Server(IPAddress.Loopback, port);
            var stopped = new ManualResetEventSlim(false);
            server.ServerStopped += (s, e) => stopped.Set();

            var serverThread = new Thread(server.Start) { IsBackground = true };
            serverThread.Start();

            // Wait until the server is accepting, then stop it with no clients connected.
            ConnectWithRetry(port).Close();

            server.Stop();

            Assert.True(stopped.Wait(TimeSpan.FromSeconds(10)), "accept loop did not exit after Stop");
            Assert.ThrowsAny<SocketException>(() =>
            {
                using (var refused = new TcpClient())
                {
                    refused.Connect(IPAddress.Loopback, port);
                }
            });
        }
    }
}
