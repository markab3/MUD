using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using MUD.Telnet;
using Xunit;

namespace MUD.Tests
{
    public class TelnetClientTests
    {
        private static (Client client, List<string> lines) NewClient()
        {
            var client = new Client(new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp));
            var lines = new List<string>();
            client.DataReceived += (s, line) => lines.Add(line);
            return (client, lines);
        }

        private static void Feed(Client client, params byte[] bytes)
        {
            client.ProcessReceivedBytes(bytes, bytes.Length);
        }

        private static byte[] Ascii(string s) { return Encoding.ASCII.GetBytes(s); }

        [Fact]
        public void OptionByteAfterDoIsNotLeakedIntoText()
        {
            var (client, lines) = NewClient();
            Feed(client, 0xFF, 0xFD, 0x18);
            Feed(client, Ascii("look\r\n"));
            Assert.Equal(new[] { "look" }, lines);
        }

        [Theory]
        [InlineData(0xFB)]
        [InlineData(0xFC)]
        [InlineData(0xFD)]
        [InlineData(0xFE)]
        public void OptionByteIsConsumedForAllNegotiationVerbs(byte verb)
        {
            var (client, lines) = NewClient();
            Feed(client, 0xFF, verb, 0x18, (byte)'a', (byte)'\r', (byte)'\n');
            Assert.Equal(new[] { "a" }, lines);
        }

        [Fact]
        public void NegotiationSplitAcrossReadsIsConsumed()
        {
            var (client, lines) = NewClient();
            Feed(client, 0xFF);
            Feed(client, 0xFD);
            Feed(client, 0x18);
            Feed(client, Ascii("look\r\n"));
            Assert.Equal(new[] { "look" }, lines);
        }

        [Fact]
        public void CrNulIsBareCrAndNulIsDropped()
        {
            var (client, lines) = NewClient();
            Feed(client, (byte)'a', (byte)'\r', 0x00, (byte)'b', (byte)'\r', 0x00);
            Assert.Equal(new[] { "a", "b" }, lines);
        }

        [Fact]
        public void CrNulSplitAcrossReadsDropsNul()
        {
            var (client, lines) = NewClient();
            Feed(client, (byte)'a', (byte)'\r');
            Feed(client, 0x00, (byte)'b', (byte)'\r', 0x00);
            Assert.Equal(new[] { "a", "b" }, lines);
        }

        [Fact]
        public void CrLfSplitAcrossReadsYieldsNoExtraEmptyLine()
        {
            var (client, lines) = NewClient();
            Feed(client, (byte)'h', (byte)'i', (byte)'\r');
            Feed(client, (byte)'\n');
            Feed(client, Ascii("there\r\n"));
            Assert.Equal(new[] { "hi", "there" }, lines);
        }

        [Fact]
        public void CrLfInOneReadYieldsOneLine()
        {
            var (client, lines) = NewClient();
            Feed(client, Ascii("one\r\n\r\ntwo\r\n"));
            Assert.Equal(new[] { "one", "", "two" }, lines);
        }

        [Fact]
        public void IacIacIsLiteral0xFFData()
        {
            var (client, lines) = NewClient();
            Feed(client, (byte)'a', 0xFF, 0xFF, (byte)'b', (byte)'\r', (byte)'\n');
            Assert.Equal(new[] { "a" + Encoding.ASCII.GetString(new byte[] { 0xFF }) + "b" }, lines);
        }

        [Fact]
        public void IacIacSplitAcrossReadsIsLiteral0xFFData()
        {
            var (client, lines) = NewClient();
            Feed(client, (byte)'a', 0xFF);
            Feed(client, 0xFF, (byte)'b', (byte)'\r', (byte)'\n');
            Assert.Equal(new[] { "a" + Encoding.ASCII.GetString(new byte[] { 0xFF }) + "b" }, lines);
        }

        [Fact]
        public void DisconnectRaisesClientDisconnectedExactlyOnce()
        {
            // Never-connected socket: Connected is false, which used to suppress the event entirely.
            var (client, _) = NewClient();
            int count = 0;
            client.ClientDisconnected += (s, c) => count++;
            client.Disconnect();
            client.Disconnect();
            Assert.Equal(1, count);
        }

        [Fact]
        public void DisconnectClosesSocket()
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var client = new Client(socket);
            client.Disconnect();
            Assert.Throws<System.ObjectDisposedException>(() => socket.Send(new byte[1]));
        }

        [Fact]
        public void RemoteCloseDisconnectsOnceAndDoesNotReadAgain()
        {
            var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
                var remote = new TcpClient();
                remote.Connect(System.Net.IPAddress.Loopback, port);
                var accepted = listener.AcceptSocket();

                var client = new Client(accepted);
                int count = 0;
                var signal = new System.Threading.ManualResetEventSlim(false);
                client.ClientDisconnected += (s, c) => { count++; signal.Set(); };
                client.Read();

                remote.Close();

                Assert.True(signal.Wait(5000), "ClientDisconnected was not raised after remote close");
                System.Threading.Thread.Sleep(200);
                Assert.Equal(1, count);
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
