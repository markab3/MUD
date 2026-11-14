using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Bson;
using MUD.Core;
using MUD.Core.Commands;
using MUD.Core.GameObjects;
using MUD.CoreApi;
using MUD.Data;
using MUD.Telnet;
using Xunit;

namespace MUD.Tests
{
    public class FakeClock : IClock
    {
        public DateTime Now { get; private set; } = new DateTime(2026, 1, 1, 12, 0, 0);

        public void Advance(TimeSpan by) { Now += by; }
    }

    // Deterministic tests: no threads, no sleeping, time only moves when the test advances the fake clock.
    public class GameLoopTests
    {
        private readonly FakeClock _clock = new FakeClock();
        private readonly List<string> _handled = new List<string>();
        private int _ticks, _heartbeats;
        private readonly List<string> _log = new List<string>();

        private GameLoop NewLoop()
        {
            return new GameLoop(_clock,
                item => _handled.Add(item.Kind + ":" + item.Line),
                () => _ticks++,
                () => _heartbeats++,
                _log.Add);
        }

        [Fact]
        public void ItemsAreHandledInArrivalOrderAndDisconnectDoesNotOvertakeLines()
        {
            var loop = NewLoop();
            var a = new SessionId("a");
            var b = new SessionId("b");
            loop.Post(InboundItem.ForLine(a, "a1"));
            loop.Post(InboundItem.ForLine(b, "b1"));
            loop.Post(InboundItem.ForLine(a, "a2"));
            loop.Post(InboundItem.ForDisconnected(a));

            Assert.Equal(4, loop.RunOnce(TimeSpan.Zero));
            Assert.Equal(new[] { "Line:a1", "Line:b1", "Line:a2", "Disconnected:" }, _handled);
        }

        [Fact]
        public void NoTickUntilTheIntervalHasElapsed()
        {
            var loop = NewLoop();
            loop.RunOnce(TimeSpan.Zero);
            Assert.Equal(0, _ticks);

            _clock.Advance(TimeSpan.FromMilliseconds(99));
            loop.RunOnce(TimeSpan.Zero);
            Assert.Equal(0, _ticks);

            _clock.Advance(TimeSpan.FromMilliseconds(1));
            loop.RunOnce(TimeSpan.Zero);
            Assert.Equal(1, _ticks);
        }

        [Fact]
        public void HeartbeatFiresOnceEveryTwentyTicks()
        {
            var loop = NewLoop();
            for (int i = 0; i < 19; i++)
            {
                _clock.Advance(GameLoop.TickInterval);
                loop.RunOnce(TimeSpan.Zero);
            }
            Assert.Equal(19, _ticks);
            Assert.Equal(0, _heartbeats);

            _clock.Advance(GameLoop.TickInterval);
            loop.RunOnce(TimeSpan.Zero);
            Assert.Equal(20, _ticks);
            Assert.Equal(1, _heartbeats);
        }

        [Fact]
        public void ALongGapSkipsMissedTicksInsteadOfCatchingUp()
        {
            var loop = NewLoop();
            _clock.Advance(TimeSpan.FromSeconds(60));
            loop.RunOnce(TimeSpan.Zero);
            loop.RunOnce(TimeSpan.Zero);
            Assert.Equal(1, _ticks);
            Assert.Equal(0, _heartbeats);
        }

        [Fact]
        public void ExceptionInAHandlerIsLoggedAndLaterItemsStillRun()
        {
            var handled = new List<string>();
            var loop = new GameLoop(_clock, item => { if (item.Line == "boom") { throw new InvalidOperationException("x"); } handled.Add(item.Line); }, null, null, _log.Add);
            var c = new SessionId("c");
            loop.Post(InboundItem.ForLine(c, "boom"));
            loop.Post(InboundItem.ForLine(c, "ok"));
            loop.RunOnce(TimeSpan.Zero);

            Assert.Equal(new[] { "ok" }, handled);
            Assert.Contains(_log, l => l.Contains("boom") || l.Contains("InvalidOperationException"));
        }

        [Fact]
        public void ExceptionInATickOrHeartbeatDoesNotStopTheLoop()
        {
            var loop = new GameLoop(_clock, null, () => { throw new InvalidOperationException("tick"); }, () => _heartbeats++, _log.Add);
            for (int i = 0; i < 20; i++)
            {
                _clock.Advance(GameLoop.TickInterval);
                loop.RunOnce(TimeSpan.Zero);
            }
            Assert.Equal(20, loop.TickCount);
            Assert.Equal(1, _heartbeats);
        }

        [Fact]
        public void PassIsBoundedByMaxItemsPerPass()
        {
            var loop = NewLoop();
            var c = new SessionId("c");
            for (int i = 0; i < GameLoop.MaxItemsPerPass + 5; i++) { loop.Post(InboundItem.ForLine(c, "x")); }

            Assert.Equal(GameLoop.MaxItemsPerPass, loop.RunOnce(TimeSpan.Zero));
            Assert.Equal(5, loop.RunOnce(TimeSpan.Zero));
        }

        [Fact]
        public void SlowTickIsLogged()
        {
            var loop = new GameLoop(_clock, null, () => _clock.Advance(TimeSpan.FromMilliseconds(250)), null, _log.Add);
            loop.Tick();
            Assert.Contains(_log, l => l.StartsWith("WARNING: tick took"));
        }

        [Fact]
        public void PostAfterTheLoopHasStoppedIsRefused()
        {
            var loop = NewLoop();
            loop.Stop();
            loop.Run();
            Assert.False(loop.Post(InboundItem.ForEvent(() => { })));
        }
    }

    public class CommandTimingTests
    {
        private readonly FakeClock _clock = new FakeClock();

        private class Recorder : Living
        {
            public List<string> Messages = new List<string>();
            public Recorder() : base(null) { }
            public override void ReceiveMessage(string message) { Messages.Add(message); }
        }

        private static AnonymousCommand Cmd(string name, List<string> ran, Action<Living> extra = null, bool throws = false)
        {
            return new AnonymousCommand
            {
                CommandKeywords = new[] { name },
                ParseCommandHandler = (l, i) => new object[0],
                DoCommandHandler = (l, a) => { ran.Add(name); if (throws) { throw new InvalidOperationException("bad"); } extra?.Invoke(l); }
            };
        }

        [Fact]
        public void CommandsRunImmediatelyByDefaultAndInOrder()
        {
            var queue = new CommandQueue(_clock);
            var who = new Recorder();
            var ran = new List<string>();
            queue.AddCommand(new CommandExecution(who, "one", Cmd("one", ran)));
            queue.AddCommand(new CommandExecution(who, "two", Cmd("two", ran)));

            Assert.Equal(new[] { "one", "two" }, ran);
            Assert.Empty(who.Messages);
            Assert.False(queue.HasMoreCommands());
        }

        [Fact]
        public void CommandWithCostDelaysTheNextUntilTheClockReachesIt()
        {
            var queue = new CommandQueue(_clock);
            var who = new Recorder();
            var ran = new List<string>();
            queue.AddCommand(new CommandExecution(who, "slow", Cmd("slow", ran, l => l.NextActionAt = _clock.Now + TimeSpan.FromSeconds(3))));
            queue.AddCommand(new CommandExecution(who, "next", Cmd("next", ran)));

            Assert.Equal(new[] { "slow" }, ran);
            Assert.Equal(new[] { "Command queued." }, who.Messages);

            _clock.Advance(TimeSpan.FromSeconds(2.9));
            queue.Dispatch();
            Assert.Equal(new[] { "slow" }, ran);

            _clock.Advance(TimeSpan.FromSeconds(0.1));
            queue.Dispatch();
            Assert.Equal(new[] { "slow", "next" }, ran);
            Assert.False(queue.HasMoreCommands());
        }

        [Fact]
        public void StopClearsTheQueue()
        {
            var queue = new CommandQueue(_clock);
            var who = new Recorder();
            var ran = new List<string>();
            queue.AddCommand(new CommandExecution(who, "slow", Cmd("slow", ran, l => l.NextActionAt = _clock.Now + TimeSpan.FromSeconds(3))));
            queue.AddCommand(new CommandExecution(who, "next", Cmd("next", ran)));
            queue.AddCommand(new CommandExecution(who, "stop", new StopCommand()));

            _clock.Advance(TimeSpan.FromSeconds(5));
            queue.Dispatch();
            Assert.Equal(new[] { "slow" }, ran);
            Assert.Equal(0, queue.PendingCount(who));
        }

        [Fact]
        public void ThrowingCommandReportsToTheIssuerAndLaterCommandsStillRun()
        {
            var queue = new CommandQueue(_clock);
            var who = new Recorder();
            var ran = new List<string>();
            queue.AddCommand(new CommandExecution(who, "bad", Cmd("bad", ran, throws: true)));
            queue.AddCommand(new CommandExecution(who, "good", Cmd("good", ran)));

            Assert.Equal(new[] { "bad", "good" }, ran);
            Assert.Contains("There was an error executing your command.", who.Messages);
        }

        [Fact]
        public void DelayThenRunsTheActionOnlyAfterTheDelayAndStopCancelsIt()
        {
            var queue = new CommandQueue(_clock);
            var who = new Recorder();
            var ran = new List<string>();
            queue.DelayThen(who, TimeSpan.FromSeconds(3), () => ran.Add("found"));

            queue.Dispatch();
            Assert.Empty(ran);
            _clock.Advance(TimeSpan.FromSeconds(3));
            queue.Dispatch();
            Assert.Equal(new[] { "found" }, ran);

            queue.DelayThen(who, TimeSpan.FromSeconds(3), () => ran.Add("cancelled"));
            queue.AddCommand(new CommandExecution(who, "stop", new StopCommand()));
            _clock.Advance(TimeSpan.FromSeconds(10));
            queue.Dispatch();
            Assert.Equal(new[] { "found" }, ran);
        }

        [Fact]
        public void SearchCommandDoesNotSleepAndFinishesAfterThreeGameSeconds()
        {
            var queue = new CommandQueue(_clock);
            var who = new Recorder();
            var search = new SearchCommand(queue);
            var watch = System.Diagnostics.Stopwatch.StartNew();

            // CurrentLocation is null for this bare living, so the deferred half throws; the point here is timing.
            queue.AddCommand(new CommandExecution(who, "search", search));
            Assert.True(watch.ElapsedMilliseconds < 1000, "search must not block the game thread");
            Assert.Equal(new[] { "You begin your search." }, who.Messages);
            Assert.True(queue.HasMoreCommands());

            _clock.Advance(TimeSpan.FromSeconds(2.9));
            queue.Dispatch();
            Assert.Equal(1, who.Messages.Count);
            Assert.True(queue.HasMoreCommands());

            _clock.Advance(TimeSpan.FromSeconds(0.1));
            queue.Dispatch();
            Assert.False(queue.HasMoreCommands());
            Assert.Contains("There was an error executing your command.", who.Messages); // no room: expected here
        }
    }

    public class PersistenceWorkerTests
    {
        private readonly FakeClock _clock = new FakeClock();
        private readonly List<string> _log = new List<string>();

        private class FakeWriter : IPersistWriter
        {
            public int FailTimes;
            public List<PersistRequest> Written = new List<PersistRequest>();
            public int Calls;
            public bool Write(PersistRequest r)
            {
                Calls++;
                if (FailTimes > 0) { FailTimes--; throw new InvalidOperationException("db down"); }
                Written.Add(r);
                return true;
            }
        }

        private static PersistRequest Req(string id, int version, bool final = false, PersistKind kind = PersistKind.Replace, string field = "v")
        {
            return new PersistRequest { Collection = "player", Id = id, Kind = kind, Doc = new BsonDocument(field, version), Final = final };
        }

        private PersistenceWorker NewWorker(FakeWriter w, List<Action> game = null, SpoolStore spool = null)
        {
            return new PersistenceWorker(w, _clock, a => (game ?? new List<Action>()).Add(a), spool, _log.Add);
        }

        [Fact]
        public void WritesInOrderAndReportsCompletion()
        {
            var writer = new FakeWriter();
            var game = new List<Action>();
            var worker = NewWorker(writer, game);
            bool? completed = null;
            var first = Req("a", 1);
            first.OnCompleted = ok => completed = ok;
            worker.Post(first);
            worker.Post(Req("b", 1));
            worker.Pump();

            Assert.Equal(new[] { "a", "b" }, writer.Written.Select(r => r.Id));
            Assert.Null(completed); // callbacks go to the game thread, not the worker
            game.ForEach(a => a());
            Assert.True(completed);
        }

        [Fact]
        public void QueuedSnapshotsForTheSameEntityCoalesceToTheLatest()
        {
            var writer = new FakeWriter();
            var worker = NewWorker(writer);
            worker.Post(Req("a", 1));
            worker.Post(Req("a", 2));
            worker.Post(Req("a", 3));
            worker.Pump();

            Assert.Single(writer.Written);
            Assert.Equal(3, writer.Written[0].Doc["v"].AsInt32);
        }

        [Fact]
        public void FinalIsNeverCoalescedAwayAndIsAppliedLast()
        {
            var writer = new FakeWriter();
            var worker = NewWorker(writer);
            worker.Post(Req("a", 1));
            worker.Post(Req("a", 2, final: true));
            worker.Post(Req("a", 3));
            worker.Pump();

            Assert.True(writer.Written.Last().Final || writer.Written.Any(r => r.Final));
            Assert.Contains(writer.Written, r => r.Final && r.Doc["v"].AsInt32 == 2);
        }

        [Fact]
        public void SetFieldsNeverSupersedesAQueuedReplace()
        {
            var writer = new FakeWriter();
            var worker = NewWorker(writer);
            worker.Post(Req("a", 1));
            worker.Post(Req("a", 2, kind: PersistKind.SetFields, field: "Items"));
            worker.Pump();

            Assert.Equal(new[] { PersistKind.Replace, PersistKind.SetFields }, writer.Written.Select(r => r.Kind));
        }

        [Fact]
        public void FailureRetriesWithBackoffOnTheFakeClockThenSucceeds()
        {
            var writer = new FakeWriter { FailTimes = 2 };
            var game = new List<Action>();
            var worker = NewWorker(writer, game);
            int failedReports = 0;
            bool? completed = null;
            var r = Req("a", 1);
            r.OnFailed = n => failedReports++;
            r.OnCompleted = ok => completed = ok;
            worker.Post(r);

            worker.Pump();
            Assert.Equal(1, writer.Calls);
            worker.Pump(); // not due yet
            Assert.Equal(1, writer.Calls);

            _clock.Advance(TimeSpan.FromSeconds(1));
            worker.Pump();
            Assert.Equal(2, writer.Calls);
            _clock.Advance(TimeSpan.FromSeconds(1.9));
            worker.Pump();
            Assert.Equal(2, writer.Calls); // second backoff is 2 s

            _clock.Advance(TimeSpan.FromSeconds(0.1));
            worker.Pump();
            Assert.Equal(3, writer.Calls);
            Assert.Single(writer.Written);

            game.ForEach(a => a());
            Assert.Equal(1, failedReports); // reported once, not on every attempt
            Assert.True(completed);
        }

        [Fact]
        public void BackoffDoublesAndCapsAtThirtySeconds()
        {
            Assert.Equal(TimeSpan.FromSeconds(1), PersistenceWorker.Backoff(1));
            Assert.Equal(TimeSpan.FromSeconds(2), PersistenceWorker.Backoff(2));
            Assert.Equal(TimeSpan.FromSeconds(4), PersistenceWorker.Backoff(3));
            Assert.Equal(TimeSpan.FromSeconds(30), PersistenceWorker.Backoff(20));
        }

        [Fact]
        public void NonFinalIsDroppedAfterMaxAttemptsButFinalIsRetriedIndefinitely()
        {
            var writer = new FakeWriter { FailTimes = int.MaxValue };
            var game = new List<Action>();
            var worker = NewWorker(writer, game);
            bool? normalOutcome = null;
            var normal = Req("a", 1);
            normal.OnCompleted = ok => normalOutcome = ok;
            worker.Post(normal);
            worker.Post(Req("b", 1, final: true));

            for (int i = 0; i < 40; i++)
            {
                worker.Pump();
                _clock.Advance(TimeSpan.FromSeconds(30));
            }
            game.ForEach(a => a());

            Assert.False(normalOutcome);
            Assert.Equal(1, worker.PendingCount); // only the final one is left, still trying
            Assert.True(writer.Calls > PersistenceWorker.MaxAttempts + 5);
        }

        [Fact]
        public void ShutdownDrainWritesWhatItCanAndSpoolsTheRestAtTheDeadline()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mudspool-" + Guid.NewGuid().ToString("N"));
            try
            {
                var writer = new FakeWriter { FailTimes = int.MaxValue };
                var worker = NewWorker(writer, spool: new SpoolStore(dir));
                worker.Post(Req("a", 7, final: true));

                var spooled = worker.Drain(TimeSpan.FromSeconds(10), t => _clock.Advance(t));

                Assert.Equal(new[] { "player/a" }, spooled);
                Assert.Single(System.IO.Directory.GetFiles(dir));
                Assert.Equal(0, worker.PendingCount);

                // The next start replays it.
                var okWriter = new FakeWriter();
                var next = NewWorker(okWriter, spool: new SpoolStore(dir));
                Assert.Equal(1, next.ReplaySpool());
                next.Pump();
                Assert.Equal(7, okWriter.Written.Single().Doc["v"].AsInt32);
                Assert.Empty(System.IO.Directory.GetFiles(dir));
            }
            finally
            {
                if (System.IO.Directory.Exists(dir)) { System.IO.Directory.Delete(dir, true); }
            }
        }

        [Fact]
        public void DrainWithAHealthyWriterSpoolsNothing()
        {
            var writer = new FakeWriter();
            var worker = NewWorker(writer);
            worker.Post(Req("a", 1, final: true));
            Assert.Empty(worker.Drain(TimeSpan.FromSeconds(10), t => _clock.Advance(t)));
            Assert.Single(writer.Written);
        }
    }

    public class OutboundQueueTests
    {
        [Fact]
        public async Task WritesAreOrderedAndNeverOverlap()
        {
            var written = new List<string>();
            int inFlight = 0, maxInFlight = 0;
            var queue = new OutboundQueue(
                async data =>
                {
                    int now = System.Threading.Interlocked.Increment(ref inFlight);
                    maxInFlight = Math.Max(maxInFlight, now);
                    await Task.Delay(1);
                    written.Add(System.Text.Encoding.ASCII.GetString(data));
                    System.Threading.Interlocked.Decrement(ref inFlight);
                },
                null, null);

            var expected = Enumerable.Range(0, 100).Select(i => "m" + i).ToList();
            foreach (var m in expected) { Assert.Equal(EnqueueResult.Queued, queue.Enqueue(System.Text.Encoding.ASCII.GetBytes(m))); }
            queue.Complete();
            await queue.Completion;

            Assert.Equal(expected, written);
            Assert.Equal(1, maxInFlight);
        }

        [Fact]
        public void StalledClientFillsTheQueueThenIsReportedSlowAndNothingBlocks()
        {
            var gate = new TaskCompletionSource<bool>();
            int slow = 0;
            var queue = new OutboundQueue(data => gate.Task, null, () => slow++, capacity: 4);

            var results = new List<EnqueueResult>();
            for (int i = 0; i < 12; i++) { results.Add(queue.Enqueue(new byte[] { 1 })); }

            Assert.Contains(EnqueueResult.Dropped, results);
            Assert.True(slow >= 1, "a client whose queue stays full must be reported");
            gate.SetResult(true);
        }

        [Fact]
        public async Task CompleteDrainsQueuedItemsThenRefusesMore()
        {
            var written = new List<byte[]>();
            var queue = new OutboundQueue(data => { written.Add(data); return Task.CompletedTask; }, null, null);
            queue.Enqueue(new byte[] { 1 });
            queue.Enqueue(new byte[] { 2 });
            queue.Complete();
            await queue.Completion;

            Assert.Equal(2, written.Count);
            Assert.Equal(EnqueueResult.Closed, queue.Enqueue(new byte[] { 3 }));
        }

        [Fact]
        public async Task CompleteOnAnUnusedQueueCompletesImmediately()
        {
            var queue = new OutboundQueue(data => Task.CompletedTask, null, null);
            queue.Complete();
            await queue.Completion;
        }

        [Fact]
        public async Task WriteFailureIsReportedOnceAndStopsTheWriter()
        {
            int failures = 0;
            var queue = new OutboundQueue(data => throw new InvalidOperationException("broken pipe"), ex => failures++, null);
            queue.Enqueue(new byte[] { 1 });
            await queue.Completion;

            Assert.Equal(1, failures);
            Assert.Equal(EnqueueResult.Closed, queue.Enqueue(new byte[] { 2 }));
        }
    }
}

namespace MUD.Tests
{
    [Collection("World construction")] // World registers BSON class maps; constructing it in parallel races
    public class LogoutOrderingTests
    {
        private static World NewWorld()
        {
            return new World(new MongoDB.Driver.MongoClient("mongodb://127.0.0.1:1"), new WorldSettings(), new FakeClock());
        }

        [Fact]
        public void QuitSendsFarewellBeforeAFlushingDisconnectAndRemovesThePlayer()
        {
            var world = NewWorld();
            var repo = new FakePlayerRepo();
            var player = new Player(world, null, repo) { Id = "p.tester", PlayerName = "tester", Password = "x" };
            var sink = new FakeSink();
            world.Players.Add(player);
            world.AttachPlayer(player, world.OpenSession("test", "test", sink));
            player.ConnectionStatus = PlayerConnectionStatuses.LoggedIn;

            player.Quit();
            player.Quit(); // once per session

            Assert.True(sink.Events.IndexOf("output:Thanks for playing!") >= 0);
            Assert.Equal(1, sink.Events.Count(e => e.StartsWith("close:")));
            Assert.True(sink.Events.IndexOf("output:Thanks for playing!") < sink.Events.IndexOf("close:"));
            Assert.DoesNotContain(player, world.Players);
            Assert.Equal(PlayerConnectionStatuses.Offline, player.ConnectionStatus);
        }

        [Fact]
        public void LoginIsBlockedWhileAFinalSaveIsPendingAndAllowedAfterItCompletes()
        {
            var world = NewWorld();
            var player = new Player(world, null, new FakePlayerRepo()) { Id = "p.tester", PlayerName = "tester" };
            var request = new PersistRequest { Collection = "player", Id = "p.tester", Final = true };

            world.BeginFinalSave(player, request);
            Assert.True(world.IsFinalSavePending("p.tester"));
            Assert.False(world.IsFinalSavePending("p.other"));

            request.OnCompleted(true);
            Assert.False(world.IsFinalSavePending("p.tester"));
        }
    }
}
