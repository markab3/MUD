using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MUD.Core;
using MUD.Data;
using Xunit;

namespace MUD.Tests
{
    // Fake worker/repository: no Mongo, no sockets. Real time is used only for the timeout test (short timeout).
    public class ShutdownCoordinatorTests
    {
        private readonly List<string> _steps = new List<string>();
        private readonly List<string> _log = new List<string>();

        private ShutdownCoordinator Make(Action save, TimeSpan timeout, Func<string> unsaved = null)
        {
            return new ShutdownCoordinator(
                () => { lock (_steps) _steps.Add("stop-accepting"); },
                () => { lock (_steps) _steps.Add("notify"); },
                () => { lock (_steps) _steps.Add("save-start"); save(); lock (_steps) _steps.Add("save-end"); },
                () => { lock (_steps) _steps.Add("close"); },
                unsaved ?? (() => "nothing"),
                s => { lock (_log) _log.Add(s); }) { Timeout = timeout };
        }

        [Fact]
        public void DefaultTimeoutIsTenSeconds()
        {
            Assert.Equal(TimeSpan.FromSeconds(10), ShutdownCoordinator.DefaultTimeout);
        }

        [Fact]
        public void RunsStepsInOrder()
        {
            var c = Make(() => { }, TimeSpan.FromSeconds(5));
            c.Run();
            Assert.Equal(new[] { "stop-accepting", "notify", "save-start", "save-end", "close" }, _steps);
            Assert.False(c.TimedOut);
        }

        [Fact]
        public void RunsOnlyOnceWhenTriggeredRepeatedly()
        {
            var c = Make(() => { }, TimeSpan.FromSeconds(5));
            c.Run();
            c.Run();
            c.Run();
            Assert.Equal(5, _steps.Count);
        }

        [Fact]
        public void ConcurrentTriggersRunOnceAndLaterCallersWaitForCompletion()
        {
            var release = new ManualResetEventSlim(false);
            var c = Make(() => release.Wait(), TimeSpan.FromSeconds(5));
            var first = new Thread(c.Run);
            first.Start();
            SpinWait.SpinUntil(() => { lock (_steps) return _steps.Contains("save-start"); }, 5000);

            bool secondReturned = false;
            var second = new Thread(() => { c.Run(); secondReturned = true; });
            second.Start();
            Assert.False(second.Join(200)); // still waiting for the first run
            release.Set();
            Assert.True(second.Join(5000));
            first.Join();
            Assert.True(secondReturned);
            Assert.Equal(1, _steps.FindAll(s => s == "save-start").Count);
        }

        [Fact]
        public void TimesOutLogsWhatWasNotSavedAndStillCloses()
        {
            var block = new ManualResetEventSlim(false);
            var c = Make(() => block.Wait(), TimeSpan.FromMilliseconds(200), () => "player Bob (still online)");
            c.Run();
            block.Set();
            Assert.True(c.TimedOut);
            Assert.Contains("close", _steps);
            Assert.Contains(_log, l => l.Contains("timed out") && l.Contains("player Bob (still online)"));
        }

        [Fact]
        public void FailingStepDoesNotStopLaterSteps()
        {
            var c = new ShutdownCoordinator(
                () => throw new InvalidOperationException("boom"),
                () => _steps.Add("notify"),
                () => _steps.Add("save"),
                () => _steps.Add("close"),
                null, s => _log.Add(s));
            c.Run();
            Assert.Equal(new[] { "notify", "save", "close" }, _steps);
            Assert.Contains(_log, l => l.Contains("boom"));
        }

        // The save step as Program wires it is World.Stop(); here the same contract is checked against the real
        // PersistenceWorker with a fake writer: Final snapshots queued before shutdown are written, then it drains.
        private class RecordingWriter : IPersistWriter
        {
            public List<string> Written = new List<string>();
            public bool Write(PersistRequest request) { Written.Add(request.Collection + "/" + request.Id); return true; }
        }

        [Fact]
        public void PersistenceWorkerDrainWritesEveryQueuedFinalSnapshot()
        {
            var writer = new RecordingWriter();
            var clock = new FakeClock();
            var worker = new PersistenceWorker(writer, clock, a => a(), null, s => _log.Add(s));
            for (int i = 0; i < 3; i++)
            {
                worker.Post(new PersistRequest { Collection = "player", Id = "p" + i, Kind = PersistKind.Replace, Final = true });
            }
            var c = new ShutdownCoordinator(null, null,
                () => { worker.Shutdown(TimeSpan.FromSeconds(1)); worker.Run(); },
                null, null, s => _log.Add(s)) { Timeout = TimeSpan.FromSeconds(5) };
            c.Run();
            Assert.False(c.TimedOut);
            Assert.Equal(new[] { "player/p0", "player/p1", "player/p2" }, writer.Written.OrderBy(x => x).ToArray());
        }
    }
}
