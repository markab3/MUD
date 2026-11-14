using System;
using System.Collections.Generic;
using System.Linq;
using MUD.Core;
using MUD.Core.Commands;
using MUD.Core.Formatting;
using MUD.Core.GameObjects;
using MUD.CoreApi;
using MUD.Telnet;
using Xunit;

namespace MUD.Tests
{
    public class TelnetSessionSinkTests
    {
        private class RecordingConnection : ILoginConnection
        {
            public readonly List<string> Sent = new List<string>();
            public readonly List<string> Closes = new List<string>();
            public string RemoteKey { get { return "test"; } }
            public void Send(string text) { Sent.Add(text); }
            public void SendPrompt(string text) { Sent.Add(text); }
            public void SetEchoHidden(bool hidden) { }
            public void DisconnectWithMessage(string message) { Closes.Add("message:" + message); }
            public void Disconnect(bool flush) { Closes.Add("disconnect:" + flush); }
        }

        private static readonly SessionId Id = new SessionId("test");

        private static string Converted(string terminalName, string markup)
        {
            var connection = new RecordingConnection();
            var sink = new TelnetSessionSink(connection);
            sink.SetTerminal(Id, terminalName);
            sink.Output(Id, OutputChannel.System, markup, false);
            return Assert.Single(connection.Sent);
        }

        [Fact]
        public void MarkupTokenNamesMatchCoreFormatOptionsInOrder()
        {
            Assert.Equal(Enum.GetNames(typeof(EFormatOptions)), MarkupTokens.Names);
        }

        [Fact]
        public void RedIsConvertedForAnAnsiSession()
        {
            Assert.Equal("\u001b[31m", Converted("ansi", "%^Red%^"));
        }

        [Fact]
        public void RedIsStrippedForADefaultSession()
        {
            Assert.Equal("", Converted("Default", "%^Red%^"));
        }

        [Fact]
        public void EachSessionKeepsItsOwnTerminal()
        {
            var firstConnection = new RecordingConnection();
            var secondConnection = new RecordingConnection();
            var first = new TelnetSessionSink(firstConnection);
            var second = new TelnetSessionSink(secondConnection);
            first.SetTerminal(Id, "ansi");
            second.SetTerminal(Id, "default");
            first.Output(Id, OutputChannel.Say, "%^Red%^x", false);
            second.Output(Id, OutputChannel.Say, "%^Red%^x", false);
            Assert.Equal("\u001b[31mx", firstConnection.Sent.Single());
            Assert.Equal("x", secondConnection.Sent.Single());
        }

        [Fact]
        public void ChangingTheTerminalAffectsLaterOutput()
        {
            var connection = new RecordingConnection();
            var sink = new TelnetSessionSink(connection);
            sink.SetTerminal(Id, "ansi");
            sink.Output(Id, OutputChannel.System, "%^Red%^a", false);
            sink.SetTerminal(Id, "default");
            sink.Output(Id, OutputChannel.System, "%^Red%^b", false);
            Assert.Equal(new[] { "\u001b[31ma", "b" }, connection.Sent);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("nosuchterminal")]
        public void UnknownOrMissingTerminalLeavesMarkupAlone(string terminalName)
        {
            Assert.Equal("%^Red%^a", Converted(terminalName, "%^Red%^a"));
        }

        [Fact]
        public void PromptsAreNotConverted()
        {
            var connection = new RecordingConnection();
            var sink = new TelnetSessionSink(connection);
            sink.SetTerminal(Id, "ansi");
            sink.Prompt(Id, "%^Red%^> ");
            Assert.Equal("%^Red%^> ", connection.Sent.Single());
        }

        [Fact]
        public void SessionEndedForAStoppingWorldTellsTheClientTheWorldIsRestartingThenCloses()
        {
            var connection = new RecordingConnection();
            var sink = new TelnetSessionSink(connection);
            sink.Close(Id, string.Empty, true);
            sink.SessionEnded(Id, SessionEndReason.WorldStopping);
            Assert.Equal(new[] { TelnetSessionSink.WorldRestartingMessage }, connection.Sent.ToArray());
            Assert.Equal(new[] { "disconnect:True" }, connection.Closes.ToArray());
        }

        [Theory]
        [InlineData(SessionEndReason.Quit)]
        [InlineData(SessionEndReason.TakenOver)]
        [InlineData(SessionEndReason.Flood)]
        [InlineData(SessionEndReason.Idle)]
        public void OtherEndReasonsDoNotSendTheRestartMessage(SessionEndReason reason)
        {
            var connection = new RecordingConnection();
            var sink = new TelnetSessionSink(connection);
            sink.Close(Id, string.Empty, true);
            sink.SessionEnded(Id, reason);
            Assert.Empty(connection.Sent);
            Assert.Equal(new[] { "disconnect:True" }, connection.Closes.ToArray());
        }

        [Fact]
        public void AFarewellIsWrittenBeforeTheCloseAndTheConnectionClosesOnce()
        {
            var connection = new RecordingConnection();
            var sink = new TelnetSessionSink(connection);
            sink.Close(Id, "Goodbye.", false);
            Assert.Empty(connection.Closes);
            sink.SessionEnded(Id, SessionEndReason.Flood);
            sink.SessionEnded(Id, SessionEndReason.Flood);
            Assert.Equal(new[] { "Goodbye." }, connection.Sent.ToArray());
            Assert.Equal(new[] { "disconnect:True" }, connection.Closes.ToArray());
        }

        [Fact]
        public void EveryTerminalNameTheTermCommandOffersIsKnownToTheSink()
        {
            foreach (string name in TermCommand.TerminalTypes)
            {
                var sink = new TelnetSessionSink(new RecordingConnection());
                sink.SetTerminal(Id, name);
                Assert.True(sink.CurrentHandler != null, "terminal name not known to the telnet sink: " + name);
            }
        }

        [Fact]
        public void ChangeTerminalStoresTheChoiceAndPushesItToTheSink()
        {
            var player = new Player(null, null, null);
            var recorder = new TerminalRecorder();
            player.Attach(Id, recorder);
            player.ChangeTerminal("gmud");
            Assert.Equal("gmud", player.SelectedTerm);
            Assert.Equal("gmud", recorder.Terminals.Last());
        }

        [Fact]
        public void AttachPushesTheSavedTerminalToTheSink()
        {
            var player = new Player(null, null, null) { SelectedTerm = "ansi" };
            var recorder = new TerminalRecorder();
            player.Attach(Id, recorder);
            Assert.Equal(new[] { "ansi" }, recorder.Terminals);
        }

        private class TerminalRecorder : IFrontEndSink
        {
            public readonly List<string> Terminals = new List<string>();
            public void Output(SessionId id, OutputChannel channel, string markupText, bool flush) { }
            public void SetEchoHidden(SessionId id, bool hidden) { }
            public void Prompt(SessionId id, string text) { }
            public void Close(SessionId id, string farewellMarkup, bool flush) { }
            public void SetTerminal(SessionId id, string terminalName) { Terminals.Add(terminalName); }
            public void SessionEnded(SessionId id, SessionEndReason reason) { }
        }
    }
}
