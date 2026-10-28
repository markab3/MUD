using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using MUD.Core;
using MUD.Core.Commands;
using MUD.Core.GameObjects;
using MUD.CoreApi;
using MUD.Telnet;
using Xunit;

namespace MUD.Tests
{
    /// <summary>Records every event core sends to it as (session id, kind, channel, text) so tests can assert on them.</summary>
    public class FakeSink : IFrontEndSink
    {
        public readonly List<string> Events = new List<string>();
        public readonly List<(SessionId Id, OutputChannel Channel, string Text)> Outputs = new List<(SessionId, OutputChannel, string)>();
        public readonly List<SessionEndReason> Ended = new List<SessionEndReason>();

        public void Output(SessionId id, OutputChannel channel, string markupText, bool flush)
        {
            lock (Events) { Outputs.Add((id, channel, markupText)); Events.Add("output:" + markupText); }
        }

        public void SetEchoHidden(SessionId id, bool hidden)
        {
            lock (Events) { Events.Add("echo:" + hidden); }
        }

        public void Prompt(SessionId id, string text)
        {
            lock (Events) { Events.Add("prompt:" + text); }
        }

        public void Close(SessionId id, string farewellMarkup, bool flush)
        {
            lock (Events) { Events.Add("close:" + farewellMarkup); }
        }

        public void SessionEnded(SessionId id, SessionEndReason reason)
        {
            lock (Events) { Ended.Add(reason); Events.Add("ended:" + reason); }
        }
    }

    /// <summary>
    /// A deliberately slow front end (decision D42). Its writer is stuck until the test opens the gate, but Output only
    /// enqueues and returns, as the interface requires, so core is never held up.
    /// </summary>
    public class SlowFrontEnd : IFrontEndSink, IDisposable
    {
        private readonly BlockingCollection<string> _pending = new BlockingCollection<string>();
        private readonly ManualResetEventSlim _gate = new ManualResetEventSlim(false);
        private readonly Thread _writer;
        private int _written;

        public SlowFrontEnd()
        {
            _writer = new Thread(WriteLoop) { IsBackground = true, Name = "slow front end writer" };
            _writer.Start();
        }

        public int Written { get { return Volatile.Read(ref _written); } }

        public void OpenGate() { _gate.Set(); }

        private void WriteLoop()
        {
            _gate.Wait(); // the slow part: nothing is written until the test opens the gate
            foreach (string text in _pending.GetConsumingEnumerable()) { Interlocked.Increment(ref _written); }
        }

        public void Output(SessionId id, OutputChannel channel, string markupText, bool flush) { _pending.Add(markupText); }
        public void SetEchoHidden(SessionId id, bool hidden) { }
        public void Prompt(SessionId id, string text) { _pending.Add(text); }
        public void Close(SessionId id, string farewellMarkup, bool flush) { _pending.Add(farewellMarkup); }
        public void SessionEnded(SessionId id, SessionEndReason reason) { }

        public void Dispose()
        {
            _gate.Set();
            _pending.CompleteAdding();
        }
    }

    [Collection("World construction")] // World registers BSON class maps; constructing it in parallel races
    public class CoreApiSessionTests
    {
        private class Fixture
        {
            public readonly FakeClock Clock = new FakeClock();
            public readonly World World;
            public readonly FakePlayerRepo Repository = new FakePlayerRepo();
            public readonly List<(string Session, string Line)> PreLoginLines = new List<(string, string)>();

            public Fixture()
            {
                World = new World(new MongoDB.Driver.MongoClient("mongodb://127.0.0.1:1"), new WorldSettings(), Clock);
                World.PreLoginHandler = (connection, line) => PreLoginLines.Add((((MUD.Core.Sessions.SessionConnection)connection).Id.Value, line));
            }

            public GameLoop Loop
            {
                get { return (GameLoop)typeof(World).GetField("_loop", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(World); }
            }

            // Runs the game thread's work for everything queued so far.
            public int Pump() { return Loop.RunOnce(TimeSpan.Zero); }

            public Player LogIn(SessionId id, string name)
            {
                var player = new Player(World, new CommandQueue(Clock), Repository) { Id = "p." + name, PlayerName = name, ShortDescription = name, Password = "x" };
                player.Connection = World.GetSessionConnection(id);
                player.ConnectionStatus = PlayerConnectionStatuses.LoggedIn;
                World.AddPlayer(player);
                return player;
            }
        }

        // (a) registry: open, close and line routing -------------------------------------------------------------

        [Fact]
        public void OpenSessionReturnsOpaqueRandomUniqueIdsGeneratedByCore()
        {
            var f = new Fixture();
            var sink = new FakeSink();
            var ids = Enumerable.Range(0, 200).Select(i => f.World.OpenSession("test", "key", sink)).ToList();

            Assert.Equal(200, ids.Select(i => i.Value).Distinct().Count());
            Assert.All(ids, id => Assert.False(id.IsEmpty));
            Assert.All(ids, id => Assert.Matches("^[0-9a-f]{32}$", id.Value)); // 128 random bits, hex
            Assert.Equal(200, f.World.OpenSessionCount);
        }

        [Fact]
        public void ClosedSessionIdsAreNeverHandedOutAgain()
        {
            var f = new Fixture();
            var sink = new FakeSink();
            var seen = new HashSet<string>();
            for (int i = 0; i < 300; i++)
            {
                SessionId id = f.World.OpenSession("test", "key", sink);
                Assert.True(seen.Add(id.Value));
                f.World.CloseSession(id, SessionEndReason.ClientClosed);
            }
            Assert.Equal(0, f.World.OpenSessionCount);
        }

        [Fact]
        public void LinesFromASessionBeforeLoginGoToThePreLoginHandlerWithThatSessionsConnection()
        {
            var f = new Fixture();
            SessionId a = f.World.OpenSession("test", "key", new FakeSink());
            SessionId b = f.World.OpenSession("test", "key", new FakeSink());

            f.World.SubmitLine(a, "a1");
            f.World.SubmitLine(b, "b1");
            f.World.SubmitLine(a, "a2");
            f.Pump();

            Assert.Equal(new[] { (a.Value, "a1"), (b.Value, "b1"), (a.Value, "a2") }, f.PreLoginLines.ToArray());
        }

        [Fact]
        public void LinesFromALoggedInSessionGoToItsPlayerAndRepliesGoToThatSessionsSink()
        {
            var f = new Fixture();
            var sinkA = new FakeSink();
            var sinkB = new FakeSink();
            SessionId a = f.World.OpenSession("test", "key", sinkA);
            SessionId b = f.World.OpenSession("test", "key", sinkB);
            f.LogIn(a, "alice");
            f.LogIn(b, "bob");
            sinkA.Events.Clear();
            sinkB.Events.Clear();

            f.World.SubmitLine(a, "nosuchcommand");
            f.Pump();

            Assert.Contains("output:Command not recognized.", sinkA.Events);
            Assert.Empty(sinkB.Events);
            Assert.Empty(f.PreLoginLines);
        }

        [Fact]
        public void CloseSessionRunsTheLogoutOnTheGameThreadAndCoreStopsCallingTheSink()
        {
            var f = new Fixture();
            var sink = new FakeSink();
            SessionId id = f.World.OpenSession("test", "key", sink);
            Player player = f.LogIn(id, "alice");
            sink.Events.Clear();

            f.World.CloseSession(id, SessionEndReason.ClientClosed);
            Assert.Equal(0, f.World.OpenSessionCount);
            Assert.Contains(player, f.World.Players); // enqueue only: nothing has run yet

            f.Pump();

            Assert.DoesNotContain(player, f.World.Players);
            Assert.Equal(PlayerConnectionStatuses.Offline, player.ConnectionStatus);
            Assert.Empty(sink.Events); // the front end closed the session, so it hears nothing more about it
            Assert.Empty(sink.Ended);
        }

        [Fact]
        public void UnknownAndClosedSessionIdsAreIgnored()
        {
            var f = new Fixture();
            var sink = new FakeSink();
            SessionId id = f.World.OpenSession("test", "key", sink);
            f.World.CloseSession(id, SessionEndReason.ClientClosed);

            f.World.SubmitLine(id, "late");
            f.World.SubmitLine(new SessionId("not-a-session"), "x");
            f.World.SubmitLine(default(SessionId), "x");
            f.World.CloseSession(id, SessionEndReason.ClientClosed);
            f.World.CloseSession(new SessionId("not-a-session"), SessionEndReason.ClientClosed);
            f.Pump();

            Assert.Empty(f.PreLoginLines);
            Assert.Empty(sink.Events);
        }

        [Fact]
        public void QuitSendsTheFarewellThenClosesThenReportsTheSessionEnded()
        {
            var f = new Fixture();
            var sink = new FakeSink();
            SessionId id = f.World.OpenSession("test", "key", sink);
            Player player = f.LogIn(id, "alice");
            sink.Events.Clear();

            player.Quit();

            int farewell = sink.Events.IndexOf("output:Thanks for playing!");
            int close = sink.Events.IndexOf("close:");
            int ended = sink.Events.IndexOf("ended:" + SessionEndReason.Quit);
            Assert.True(farewell >= 0 && close > farewell && ended > close, string.Join(" | ", sink.Events));
        }

        [Fact]
        public void ASecondLoginTakesTheSessionOverAndTheOldSinkIsToldItWasTakenOver()
        {
            var f = new Fixture();
            var oldSink = new FakeSink();
            var newSink = new FakeSink();
            SessionId oldId = f.World.OpenSession("test", "key", oldSink);
            SessionId newId = f.World.OpenSession("test", "key", newSink);
            Player player = f.LogIn(oldId, "alice");
            oldSink.Events.Clear();

            f.World.TakeOverSession(player, f.World.GetSessionConnection(newId));

            Assert.Contains("output:You have been logged in from another location.", oldSink.Events);
            Assert.Equal(new[] { SessionEndReason.TakenOver }, oldSink.Ended.ToArray());
            Assert.Contains(newSink.Events, e => e.StartsWith("output:Reconnecting"));
        }

        // Output channel tagging ----------------------------------------------------------------------------------

        private static (Fixture Fixture, Room Room, Player Alice, FakeSink AliceSink, Player Bob, FakeSink BobSink) TwoPlayersInARoom()
        {
            var f = new Fixture();
            var aliceSink = new FakeSink();
            var bobSink = new FakeSink();
            SessionId aliceId = f.World.OpenSession("test", "key", aliceSink);
            SessionId bobId = f.World.OpenSession("test", "key", bobSink);
            var room = new Room(null);
            Player alice = f.LogIn(aliceId, "Alice");
            Player bob = f.LogIn(bobId, "Bob");
            room.EnterRoom(alice);
            room.EnterRoom(bob);
            alice.CurrentLocation = room;
            bob.CurrentLocation = room;
            aliceSink.Events.Clear(); aliceSink.Outputs.Clear();
            bobSink.Events.Clear(); bobSink.Outputs.Clear();
            return (f, room, alice, aliceSink, bob, bobSink);
        }

        [Fact]
        public void UntaggedMessagesAreOnTheSystemChannel()
        {
            var t = TwoPlayersInARoom();
            t.Alice.ReceiveMessage("plain");
            Assert.Equal((OutputChannel.System, "plain"), (t.AliceSink.Outputs[0].Channel, t.AliceSink.Outputs[0].Text));
        }

        [Fact]
        public void SayIsTaggedSayForTheSpeakerAndTheRoom()
        {
            var t = TwoPlayersInARoom();
            var say = new SayCommand();
            say.DoCommand(t.Alice, say.ParseCommand(t.Alice, "say hello"));

            Assert.Equal((OutputChannel.Say, "You say: hello"), (t.AliceSink.Outputs.Single().Channel, t.AliceSink.Outputs.Single().Text));
            Assert.Equal((OutputChannel.Say, "Alice says: hello"), (t.BobSink.Outputs.Single().Channel, t.BobSink.Outputs.Single().Text));
        }

        [Fact]
        public void TellIsTaggedTellForBothSides()
        {
            var t = TwoPlayersInARoom();
            var tell = new TellCommand(t.Fixture.World);
            tell.DoCommand(t.Alice, tell.ParseCommand(t.Alice, "tell bob hi"));

            Assert.Equal(OutputChannel.Tell, t.AliceSink.Outputs.Single().Channel);
            Assert.Equal(OutputChannel.Tell, t.BobSink.Outputs.Single().Channel);
            Assert.Equal("Alice tells you: hi", t.BobSink.Outputs.Single().Text);
        }

        [Fact]
        public void ChatHasItsOwnChannelTag()
        {
            var t = TwoPlayersInARoom();
            var chat = new ChatCommand(t.Fixture.World);
            chat.DoCommand(t.Alice, chat.ParseCommand(t.Alice, "chat hi all"));

            Assert.Equal((OutputChannel.Chat, "(Chat) Alice: hi all"), (t.BobSink.Outputs.Single().Channel, t.BobSink.Outputs.Single().Text));
            Assert.Equal(OutputChannel.Chat, t.AliceSink.Outputs.Single().Channel);
        }

        [Fact]
        public void TellRoomDefaultsToTheRoomChannel()
        {
            var t = TwoPlayersInARoom();
            t.Room.TellRoom("A bell rings.");
            Assert.Equal((OutputChannel.Room, "A bell rings."), (t.BobSink.Outputs.Single().Channel, t.BobSink.Outputs.Single().Text));
        }

        [Fact]
        public void ATaggedMessageReachesALivingThatOnlyOverridesTheUntaggedMethod()
        {
            var recorder = new UntaggedRecorder();
            recorder.ReceiveMessage("hello", OutputChannel.Tell);
            Assert.Equal(new[] { "hello" }, recorder.Messages.ToArray());
        }

        private class UntaggedRecorder : Living
        {
            public readonly List<string> Messages = new List<string>();
            public UntaggedRecorder() : base(null) { }
            public override void ReceiveMessage(string message) { Messages.Add(message); }
        }

        // (b) per-session pending cap behaves as World.EnqueueLine does today ---------------------------------------

        [Fact]
        public void LinesBeyondThePendingCapAreDroppedWithAnInputNoticeAndTheRestAreHandled()
        {
            var f = new Fixture();
            var sink = new FakeSink();
            SessionId id = f.World.OpenSession("test", "key", sink);

            for (int i = 0; i < World.MaxQueuedLinesPerClient + 5; i++) { f.World.SubmitLine(id, "line" + i); }

            Assert.Equal(5, sink.Events.Count(e => e == "output:Too much input; that line was ignored."));
            f.Pump();
            Assert.Equal(World.MaxQueuedLinesPerClient, f.PreLoginLines.Count);
            Assert.Equal("line0", f.PreLoginLines.First().Line);

            // Once handled, the session has room again.
            f.World.SubmitLine(id, "again");
            f.Pump();
            Assert.Equal("again", f.PreLoginLines.Last().Line);
        }

        [Fact]
        public void KeepingOnFloodingClosesTheSessionWithTheGoodbyeAndReportsFlood()
        {
            var f = new Fixture();
            var sink = new FakeSink();
            SessionId id = f.World.OpenSession("test", "key", sink);

            for (int i = 0; i < World.MaxQueuedLinesPerClient + World.MaxDroppedLinesBeforeDisconnect + 10; i++) { f.World.SubmitLine(id, "x"); }

            Assert.Equal(World.MaxDroppedLinesBeforeDisconnect - 1, sink.Events.Count(e => e == "output:Too much input; that line was ignored."));
            Assert.Equal(1, sink.Events.Count(e => e == "close:You are sending too much input. Goodbye."));
            Assert.Equal(new[] { SessionEndReason.Flood }, sink.Ended.ToArray());
        }

        [Fact]
        public void SubmitLineAndEnqueueLineApplyTheSameCap()
        {
            var f = new Fixture();
            var connection = new FakeConnection();
            var handledLegacy = new List<string>();
            f.World.PreLoginHandler = (c, line) => { if (c == connection) { handledLegacy.Add(line); } };
            SessionId id = f.World.OpenSession("test", "key", new FakeSink());

            for (int i = 0; i < World.MaxQueuedLinesPerClient + 5; i++)
            {
                f.World.EnqueueLine(connection, "line" + i);
                f.World.SubmitLine(id, "line" + i);
            }
            f.Pump();

            Assert.Equal(World.MaxQueuedLinesPerClient, handledLegacy.Count);
            Assert.Equal(5, connection.Sent.Count(s => s == "Too much input; that line was ignored."));
        }

        // (c) a slow front end does not block the game thread -----------------------------------------------------------

        [Fact]
        public void ASlowFrontEndDoesNotHoldUpTheGameThread()
        {
            var f = new Fixture();
            using (var slow = new SlowFrontEnd())
            {
                SessionId id = f.World.OpenSession("test", "key", slow);
                Player player = f.LogIn(id, "alice");
                int before = slow.Written;
                const int messages = 5000;

                // The game thread produces a lot of output for a front end whose writer is stuck.
                f.World.Post(() => { for (int i = 0; i < messages; i++) { player.ReceiveMessage("message " + i); } });
                var clock = Stopwatch.StartNew();
                f.Pump();
                clock.Stop();

                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "the game thread was held up for " + clock.Elapsed);
                Assert.Equal(before, slow.Written); // and the slow writer really has not written anything yet

                // The game keeps ticking while the front end is stuck.
                int ticks = f.Loop.TickCount;
                f.Clock.Advance(GameLoop.TickInterval);
                f.Pump();
                f.Clock.Advance(GameLoop.TickInterval);
                f.Pump();
                Assert.True(f.Loop.TickCount >= ticks + 2);

                // Once the front end catches up, everything it was given is delivered.
                slow.OpenGate();
                SpinWait.SpinUntil(() => slow.Written >= messages, TimeSpan.FromSeconds(10));
                Assert.True(slow.Written >= messages);
            }
        }
    }
}
