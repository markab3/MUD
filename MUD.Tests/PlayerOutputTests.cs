using MUD.Core;
using MUD.Core.GameObjects;
using MUD.Telnet;
using Xunit;

namespace MUD.Tests
{
    // Characterization of what a player's telnet client receives for markup, by selected terminal. Written before
    // the output moved onto the front-end sink, so the conversion that moves out of Player.ReceiveMessage keeps its text.
    [Collection("World construction")] // World registers BSON class maps; constructing it in parallel races
    public class PlayerOutputTests
    {
        private const string Markup = "%^Red%^hi%^Reset%^";

        private static string Received(string selectedTerminal)
        {
            var world = new World(new MongoDB.Driver.MongoClient("mongodb://127.0.0.1:1"), new WorldSettings(), new FakeClock());
            var connection = new RecordingConnection();
            var sink = new TelnetSessionSink(connection);
            var player = new Player(world, null, new FakePlayerRepo()) { PlayerName = "tester", SelectedTerm = selectedTerminal };
            world.AttachPlayer(player, world.OpenSession("test", "test", sink));

            player.ReceiveMessage(Markup);

            return Assert.Single(connection.Sent);
        }

        [Theory]
        [InlineData("Ansi")]
        [InlineData("ansi")]
        [InlineData("Mudlet")]
        public void AnsiTerminalTurnsMarkupIntoEscapeSequences(string terminal)
        {
            Assert.Equal("\u001b[31mhi\u001b[0m", Received(terminal));
        }

        [Fact]
        public void DefaultTerminalRemovesMarkup()
        {
            Assert.Equal("hi", Received("Default"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("nosuchterminal")]
        public void NoMatchingTerminalLeavesMarkupAlone(string terminal)
        {
            Assert.Equal(Markup, Received(terminal));
        }

        private class RecordingConnection : ILoginConnection
        {
            public readonly System.Collections.Generic.List<string> Sent = new System.Collections.Generic.List<string>();
            public string RemoteKey { get { return "test"; } }
            public void Send(string text) { Sent.Add(text); }
            public void SendPrompt(string text) { Sent.Add(text); }
            public void SetEchoHidden(bool hidden) { }
            public void DisconnectWithMessage(string message) { }
            public void Disconnect(bool flush) { }
        }
    }
}
