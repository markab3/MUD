using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using MUD.Core;
using MUD.Core.Commands;
using MUD.Core.GameObjects;
using MUD.Telnet;
using Xunit;

namespace MUD.Tests
{
    public class StripControlCharactersTests
    {
        [Fact]
        public void RemovesEscapeSequencesBellBackspaceNulDelAndCsi()
        {
            string input = "a\u001b[31mb\u0007c\u0008d\u0000e\u007ff\u009bg\u001b";
            Assert.Equal("a[31mbcdefg", Client.StripControlCharacters(input));
        }

        [Fact]
        public void KeepsTabAndOrdinaryText()
        {
            Assert.Equal("say\thello %^Red%^there%^Reset%^!", Client.StripControlCharacters("say\thello %^Red%^there%^Reset%^!"));
        }

        [Fact]
        public void RemovesEveryC0ControlExceptTab()
        {
            for (int c = 0; c < 0x20; c++)
            {
                string result = Client.StripControlCharacters("x" + (char)c + "y");
                Assert.Equal(c == 9 ? "x\ty" : "xy", result);
            }
        }
    }

    public class ClientInputStripTests : IDisposable
    {
        private readonly Socket _listener;
        private readonly Socket _peer;
        private readonly Socket _serverSide;

        public ClientInputStripTests()
        {
            _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _listener.Listen(1);
            _peer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _peer.Connect((IPEndPoint)_listener.LocalEndPoint);
            _serverSide = _listener.Accept();
            _peer.ReceiveTimeout = 3000;
        }

        public void Dispose()
        {
            _peer.Dispose();
            _serverSide.Dispose();
            _listener.Dispose();
        }

        private static void WaitFor(Func<bool> condition)
        {
            for (int i = 0; i < 300 && !condition(); i++) { Thread.Sleep(10); }
        }

        private List<string> Receive(byte[] bytes, int expectedLines)
        {
            var lines = new List<string>();
            var client = new Client(_serverSide);
            client.DataReceived += (s, l) => { lock (lines) { lines.Add(l); } };
            client.Read();
            _peer.Send(bytes);
            WaitFor(() => { lock (lines) { return lines.Count >= expectedLines; } });
            return lines;
        }

        [Fact]
        public void EscapeSequencesAreRemovedFromReceivedLines()
        {
            var lines = Receive(Encoding.ASCII.GetBytes("say \u001b[2Jhi\u0007\u0008!\r\n"), 1);
            Assert.Equal("say [2Jhi!", Assert.Single(lines));
        }

        [Fact]
        public void TabIsKept()
        {
            var lines = Receive(Encoding.ASCII.GetBytes("say a\tb\r\n"), 1);
            Assert.Equal("say a\tb", Assert.Single(lines));
        }

        [Fact]
        public void TelnetNegotiationIsUnaffectedAndStillConsumed()
        {
            // IAC DO ECHO (255 253 1) between text; the option byte 1 must not leak, and text is still delivered.
            var bytes = new List<byte>(Encoding.ASCII.GetBytes("he"));
            bytes.AddRange(new byte[] { 255, 253, 1 });
            bytes.AddRange(Encoding.ASCII.GetBytes("llo\u001b\r\n"));
            var lines = Receive(bytes.ToArray(), 1);
            Assert.Equal("hello", Assert.Single(lines));
        }

        [Fact]
        public void RawEscapeNeverReachesAnotherClientsSocket()
        {
            // Full path: attacker line -> Client strip -> say -> recipient Player.ReceiveMessage -> recipient's socket.
            var lines = Receive(Encoding.ASCII.GetBytes("say \u001b[31mevil\u001b[0m %^Red%^ok%^Reset%^\r\n"), 1);

            var world = TestWorld.Create();
            var room = new Room(null);
            var speaker = new RecordingLiving(world) { ShortDescription = "Bob" };
            var listener = new TestPlayer(world, new Client(_serverSide)) { ShortDescription = "Alice", PlayerName = "Alice" };
            room.EnterRoom(speaker);
            room.EnterRoom(listener);
            speaker.CurrentLocation = room;

            var say = new SayCommand();
            say.DoCommand(speaker, say.ParseCommand(speaker, lines[0]));

            var received = new List<byte>();
            var buf = new byte[256];
            while (!Encoding.ASCII.GetString(received.ToArray()).Contains("\r\n"))
            {
                int n = _peer.Receive(buf);
                if (n == 0) { break; }
                received.AddRange(buf.Take(n));
            }
            string text = Encoding.ASCII.GetString(received.ToArray());
            Assert.True(text.IndexOf((char)27) < 0, "received: " + text.Replace("\u001b", "<ESC>"));
            Assert.Contains("Bob says: [31mevil[0m %^Red%^ok%^Reset%^", text);
        }
    }

    public static class TestWorld
    {
        // World's constructor loads assemblies and needs Mongo; commands only need Players/TerminalHandlers.
        public static World Create(params Player[] players)
        {
            var world = (World)FormatterServices.GetUninitializedObject(typeof(World));
            world.Players = new List<Player>(players);
            world.TerminalHandlers = new List<MUD.Core.Formatting.ITerminalHandler>();
            return world;
        }
    }

    public class RecordingLiving : Living
    {
        public readonly List<string> Messages = new List<string>();

        public RecordingLiving(World world) : base(world) { }

        public override void ReceiveMessage(string message) { Messages.Add(message); }
    }

    public class RecordingPlayer : Player
    {
        public readonly List<string> Messages = new List<string>();

        public RecordingPlayer(World world, string name) : base(world, null, null)
        {
            PlayerName = name;
            ShortDescription = name;
        }

        public override void ReceiveMessage(string message) { Messages.Add(message); }
    }

    // Uses the real Player.ReceiveMessage so output goes through the connection.
    public class TestPlayer : Player
    {
        public TestPlayer(World world, Client connection) : base(world, null, null)
        {
            Attach(new MUD.CoreApi.SessionId("test"), new ClientSink(connection));
        }
    }

    public class MessageLengthTests
    {
        private static string Long(int n) { return new string('x', n); }

        [Fact]
        public void TruncateMessage_LeavesExactly500Alone_CutsLonger()
        {
            string result;
            Assert.False(CommandExtensions.TruncateMessage(Long(500), out result));
            Assert.Equal(500, result.Length);
            Assert.True(CommandExtensions.TruncateMessage(Long(501), out result));
            Assert.Equal(500, result.Length);
        }

        [Fact]
        public void Say_Over500_IsTruncatedAndSpeakerIsTold()
        {
            var world = TestWorld.Create();
            var room = new Room(null);
            var speaker = new RecordingLiving(world) { ShortDescription = "Bob" };
            var other = new RecordingLiving(world) { ShortDescription = "Al" };
            room.EnterRoom(speaker);
            room.EnterRoom(other);
            speaker.CurrentLocation = room;
            speaker.Messages.Clear();
            other.Messages.Clear();

            var say = new SayCommand();
            say.DoCommand(speaker, say.ParseCommand(speaker, "say " + Long(600)));

            Assert.Equal("Bob says: " + Long(500), Assert.Single(other.Messages));
            Assert.Equal(2, speaker.Messages.Count);
            Assert.Equal("You say: " + Long(500), speaker.Messages[0]);
            Assert.Equal(CommandExtensions.MessageShortenedNotice, speaker.Messages[1]);
        }

        [Fact]
        public void Say_Exactly500_NoNotice()
        {
            var world = TestWorld.Create();
            var room = new Room(null);
            var speaker = new RecordingLiving(world) { ShortDescription = "Bob" };
            room.EnterRoom(speaker);
            speaker.CurrentLocation = room;
            speaker.Messages.Clear();

            var say = new SayCommand();
            say.DoCommand(speaker, say.ParseCommand(speaker, "say " + Long(500)));

            Assert.Equal("You say: " + Long(500), Assert.Single(speaker.Messages));
        }

        [Fact]
        public void Say_ColourTokensSurviveEndToEnd()
        {
            var world = TestWorld.Create();
            var room = new Room(null);
            var speaker = new RecordingLiving(world) { ShortDescription = "Bob" };
            var other = new RecordingLiving(world) { ShortDescription = "Al" };
            room.EnterRoom(speaker);
            room.EnterRoom(other);
            speaker.CurrentLocation = room;
            speaker.Messages.Clear();

            string line = Client.StripControlCharacters("say %^Red%^hello%^Reset%^");
            var say = new SayCommand();
            say.DoCommand(speaker, say.ParseCommand(speaker, line));

            Assert.Equal("Bob says: %^Red%^hello%^Reset%^", Assert.Single(other.Messages));
            Assert.Equal("You say: %^Red%^hello%^Reset%^", Assert.Single(speaker.Messages));
        }

        [Fact]
        public void Tell_Over500_IsTruncatedAndSenderIsTold()
        {
            var world = TestWorld.Create();
            var sender = new RecordingPlayer(world, "Bob");
            var target = new RecordingPlayer(world, "Alice");
            world.Players.Add(sender);
            world.Players.Add(target);

            var tell = new TellCommand(world);
            tell.DoCommand(sender, tell.ParseCommand(sender, "tell alice " + Long(700)));

            Assert.Equal("Bob tells you: " + Long(500), Assert.Single(target.Messages));
            Assert.Equal(2, sender.Messages.Count);
            Assert.Equal("You tell Alice: " + Long(500), sender.Messages[0]);
            Assert.Equal(CommandExtensions.MessageShortenedNotice, sender.Messages[1]);
        }

        [Fact]
        public void Tell_Exactly500_NoNotice()
        {
            var world = TestWorld.Create();
            var sender = new RecordingPlayer(world, "Bob");
            var target = new RecordingPlayer(world, "Alice");
            world.Players.Add(sender);
            world.Players.Add(target);

            var tell = new TellCommand(world);
            tell.DoCommand(sender, tell.ParseCommand(sender, "tell alice " + Long(500)));

            Assert.Single(sender.Messages);
        }

        [Fact]
        public void Chat_Over500_IsTruncatedAndSpeakerIsTold()
        {
            var world = TestWorld.Create();
            var sender = new RecordingPlayer(world, "Bob");
            var other = new RecordingPlayer(world, "Alice");
            world.Players.Add(sender);
            world.Players.Add(other);

            var chat = new ChatCommand(world);
            chat.DoCommand(sender, chat.ParseCommand(sender, "chat " + Long(900)));

            Assert.Equal("(Chat) Bob: " + Long(500), Assert.Single(other.Messages));
            Assert.Equal(2, sender.Messages.Count);
            Assert.Equal("(Chat) Bob: " + Long(500), sender.Messages[0]);
            Assert.Equal(CommandExtensions.MessageShortenedNotice, sender.Messages[1]);
        }

        [Fact]
        public void Chat_Exactly500_NoNotice()
        {
            var world = TestWorld.Create();
            var sender = new RecordingPlayer(world, "Bob");
            world.Players.Add(sender);

            var chat = new ChatCommand(world);
            chat.DoCommand(sender, chat.ParseCommand(sender, "chat " + Long(500)));

            Assert.Single(sender.Messages);
        }
    }
}
