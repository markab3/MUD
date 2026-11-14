using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using MUD.Telnet;
using Xunit;

namespace MUD.Tests
{
    /// <summary>
    /// Covers Client behaviour that was lost or unverified when the telnet-client-fixes branch was merged by hand:
    /// outgoing line-ending normalisation, 0xFF escaping, subnegotiation handling and the cached remote address.
    /// </summary>
    public class TelnetClientReviewTests
    {
        private static (Socket server, Socket peer) ConnectedPair()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var peer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                peer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                var server = listener.AcceptSocket();
                peer.ReceiveTimeout = 5000;
                return (server, peer);
            }
            finally
            {
                listener.Stop();
            }
        }

        private static byte[] ReceiveExactly(Socket peer, int count)
        {
            var result = new byte[count];
            int got = 0;
            while (got < count)
            {
                int n = peer.Receive(result, got, count - got, SocketFlags.None);
                if (n == 0) { break; }
                got += n;
            }
            return result;
        }

        private static void Feed(Client client, params byte[] bytes)
        {
            client.ProcessReceivedBytes(bytes, bytes.Length);
        }

        [Theory]
        [InlineData("hello\nworld", "hello\r\nworld\r\n")]
        [InlineData("hello\rworld", "hello\r\nworld\r\n")]
        [InlineData("hello\r\nworld", "hello\r\nworld\r\n")]
        [InlineData("hello\n", "hello\r\n")]
        [InlineData("hello", "hello\r\n")]
        public void SendStringNormalisesLineEndings(string text, string expected)
        {
            var (server, peer) = ConnectedPair();
            using (peer)
            {
                var client = new Client(server);
                client.Send(text);
                var bytes = ReceiveExactly(peer, Encoding.ASCII.GetByteCount(expected));
                Assert.Equal(expected, Encoding.ASCII.GetString(bytes));
                client.Disconnect();
            }
        }

        [Fact]
        public void SendStringEscapesLiteral0xFFAsIacIac()
        {
            var (server, peer) = ConnectedPair();
            using (peer)
            {
                var client = new Client(server);
                // ASCII cannot encode 0xFF, so switch to an 8-bit encoding to exercise the escaping.
                typeof(Client).GetField("_clientEncoding", BindingFlags.NonPublic | BindingFlags.Instance)
                    .SetValue(client, Encoding.Latin1);
                client.Send("a\u00FFb");
                var bytes = ReceiveExactly(peer, 6);
                Assert.Equal(new byte[] { (byte)'a', 0xFF, 0xFF, (byte)'b', 0x0D, 0x0A }, bytes);
                client.Disconnect();
            }
        }

        [Fact]
        public void SubnegotiationIsConsumedAndNotLeakedIntoText()
        {
            var client = new Client(new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp));
            var lines = new List<string>();
            client.DataReceived += (s, line) => lines.Add(line);
            // IAC SB TERMINAL-TYPE IS "xterm" IAC SE, then a normal line.
            Feed(client, 0xFF, 0xFA, 0x18, 0x00, (byte)'x', (byte)'t', (byte)'e', (byte)'r', (byte)'m', 0xFF, 0xF0);
            Feed(client, Encoding.ASCII.GetBytes("look\r\n"));
            Assert.Equal(new[] { "look" }, lines);
        }

        [Fact]
        public void SubnegotiationSplitAcrossReadsIsConsumed()
        {
            var client = new Client(new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp));
            var lines = new List<string>();
            client.DataReceived += (s, line) => lines.Add(line);
            Feed(client, 0xFF, 0xFA);
            Feed(client, 0x18, 0x00, (byte)'x');
            Feed(client, 0xFF);
            Feed(client, 0xF0);
            Feed(client, Encoding.ASCII.GetBytes("look\r\n"));
            Assert.Equal(new[] { "look" }, lines);
        }

        [Fact]
        public void RemoteAddressIsCachedAtConstructionAndSurvivesClose()
        {
            var (server, peer) = ConnectedPair();
            using (peer)
            {
                var expected = (IPEndPoint)server.RemoteEndPoint;
                var client = new Client(server);
                Assert.Equal(expected, client.RemoteAddress);
                client.Disconnect();
                Assert.Equal(expected, client.RemoteAddress);
                Assert.Equal(expected.Address.ToString(), client.RemoteKey);
            }
        }
    }
}
