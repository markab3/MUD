using System;
using System.Collections.Concurrent;
using System.Threading;
using MUD.Data;
using MUD.CoreApi;

namespace MUD.Core
{
    public enum InboundKind
    {
        /// <summary>A line of text typed in a session.</summary>
        Line,
        /// <summary>A session has been opened (before login).</summary>
        Connected,
        /// <summary>A session has closed, for any reason.</summary>
        Disconnected,
        /// <summary>A callback to run on the game thread (persistence outcome, shutdown request, ...).</summary>
        WorldEvent
    }

    /// <summary>One message from the outside world to the game thread. Immutable after construction.</summary>
    public sealed class InboundItem
    {
        public InboundKind Kind { get; private set; }
        public SessionId Session { get; private set; }
        public string Line { get; private set; }
        public Action Action { get; private set; }

        private InboundItem() { }

        public static InboundItem ForLine(SessionId session, string line)
        {
            return new InboundItem { Kind = InboundKind.Line, Session = session, Line = line };
        }

        public static InboundItem ForConnected(SessionId session)
        {
            return new InboundItem { Kind = InboundKind.Connected, Session = session };
        }

        public static InboundItem ForDisconnected(SessionId session)
        {
            return new InboundItem { Kind = InboundKind.Disconnected, Session = session };
        }

        public static InboundItem ForEvent(Action action)
        {
            return new InboundItem { Kind = InboundKind.WorldEvent, Action = action };
        }
    }

    /// <summary>
    /// The single game thread's loop (specs/GAME_LOOP.md sections 3 and 5). Everything that mutates the world runs
    /// inside the handlers passed in, on whichever thread calls Run/RunOnce/Tick. Other threads only call Post.
    ///
    /// Inbound items are processed as they arrive; once per TickInterval Tick() runs the per-tick work and, every
    /// HeartbeatEvery ticks, the heartbeat. Missed ticks are skipped, never caught up.
    ///
    /// RunOnce and Tick contain no thread of their own, so tests drive them with a fake clock and never sleep.
    /// </summary>
    public class GameLoop
    {
        public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

        /// <summary>Heartbeat cadence in ticks: 20 x 100 ms = 2 s, as before.</summary>
        public const int HeartbeatEvery = 20;

        /// <summary>Most inbound items handled in one pass, so input floods cannot starve ticks.</summary>
        public const int MaxItemsPerPass = 100;

        /// <summary>
        /// Default for the most module calls waiting for the game thread. Not set by the owner (CORE_API 1); chosen by card P4-2
        /// and flagged in its pull request. Configurable through ModuleHostOptions.
        /// </summary>
        public const int DefaultModuleCallCapacity = 1000;

        /// <summary>
        /// Default for the most module calls run in one pass, on top of MaxItemsPerPass. Smaller than MaxItemsPerPass so a chatty
        /// module cannot starve player input (CORE_API 1). Not set by the owner; chosen by card P4-2 and flagged in its pull request.
        /// </summary>
        public const int DefaultModuleCallsPerPass = 20;

        private readonly IClock _clock;
        private readonly Action<InboundItem> _handle;
        private readonly Action _onTick;
        private readonly Action _onHeartbeat;
        private readonly Action<string> _log;
        private readonly Action _processEvents;
        private readonly BlockingCollection<InboundItem> _inbound = new BlockingCollection<InboundItem>(new ConcurrentQueue<InboundItem>());
        // Module calls (gRPC handler threads) wait here, apart from the player and persistence items above. Bounded by count.
        private readonly ConcurrentQueue<Action> _moduleCalls = new ConcurrentQueue<Action>();
        private int _moduleCallCount;
        private int _wakePending;
        private DateTime _nextTick;
        private int _tickCount;
        private volatile bool _stopRequested;

        /// <param name="processEvents">Optional. Called after the inbound items and again after a tick; it processes queued events up to its own per-pass budget (EventLimits.MaxEventsPerPass), which is the event queue's share of the pass (owner decision D9).</param>
        public GameLoop(IClock clock, Action<InboundItem> handle, Action onTick, Action onHeartbeat, Action<string> log = null, Action processEvents = null)
        {
            _processEvents = processEvents;
            _clock = clock;
            _handle = handle;
            _onTick = onTick;
            _onHeartbeat = onHeartbeat;
            _log = log ?? Console.WriteLine;
            _nextTick = _clock.Now + TickInterval;
        }

        /// <summary>Most module calls that may wait for the game thread. A call over the limit is refused by PostModuleCall.</summary>
        public int ModuleCallCapacity { get; set; } = DefaultModuleCallCapacity;

        /// <summary>Most module calls run in one pass.</summary>
        public int ModuleCallsPerPass { get; set; } = DefaultModuleCallsPerPass;

        /// <summary>Module calls waiting now.</summary>
        public int PendingModuleCalls { get { return Volatile.Read(ref _moduleCallCount); } }

        /// <summary>
        /// Thread-safe and non-blocking. Queues a module call to run on the game thread, in the separate bounded queue with its own
        /// per-pass share. Returns false when that queue is full or the loop has stopped accepting input.
        /// </summary>
        public bool PostModuleCall(Action action)
        {
            if (action == null) { throw new ArgumentNullException(nameof(action)); }
            if (Interlocked.Increment(ref _moduleCallCount) > ModuleCallCapacity)
            {
                Interlocked.Decrement(ref _moduleCallCount);
                return false;
            }
            _moduleCalls.Enqueue(action);
            if (!Wake())
            {
                // The loop has stopped: take the call back out if it is still there so it does not hang a caller.
                return false;
            }
            return true;
        }

        // Makes a blocked RunOnce return so it sees the module calls. One wake-up item at a time, not one per call.
        private bool Wake()
        {
            if (Interlocked.Exchange(ref _wakePending, 1) == 0)
            {
                if (!Post(InboundItem.ForEvent(() => { })))
                {
                    Volatile.Write(ref _wakePending, 0);
                    return false;
                }
            }
            return true;
        }

        // Runs queued module calls, at most ModuleCallsPerPass of them. The rest wait for the next pass.
        private void RunModuleCalls()
        {
            Volatile.Write(ref _wakePending, 0);
            int run = 0;
            Action action;
            while (run < ModuleCallsPerPass && !_stopRequested && _moduleCalls.TryDequeue(out action))
            {
                Interlocked.Decrement(ref _moduleCallCount);
                run++;
                try { action(); }
                catch (Exception ex) { _log("Exception in module call: " + ex); }
            }
            if (!_moduleCalls.IsEmpty) { Wake(); }
        }

        /// <summary>Called on the loop's thread if the loop machinery itself fails (not a command): save and shut down.</summary>
        public Action OnFatal { get; set; }

        public int TickCount { get { return _tickCount; } }

        public bool IsStopRequested { get { return _stopRequested; } }

        /// <summary>Thread-safe and non-blocking. Returns false once the loop has stopped accepting input.</summary>
        public bool Post(InboundItem item)
        {
            if (item == null) { throw new ArgumentNullException(nameof(item)); }
            try { return _inbound.TryAdd(item); }
            catch (InvalidOperationException) { return false; }
        }

        /// <summary>Asks the loop to return from Run after the current pass.</summary>
        public void Stop()
        {
            _stopRequested = true;
        }

        /// <summary>Runs until Stop. Blocks in the inbound queue (no spinning) for at most the time to the next tick.</summary>
        public void Run()
        {
            try
            {
                while (!_stopRequested)
                {
                    TimeSpan wait = _nextTick - _clock.Now;
                    RunOnce(wait < TimeSpan.Zero ? TimeSpan.Zero : wait);
                }
            }
            catch (Exception ex)
            {
                _log("FATAL: game loop failed: " + ex);
                try { OnFatal?.Invoke(); }
                catch (Exception ex2) { _log("FATAL: shutdown after loop failure also failed: " + ex2); }
            }
            finally
            {
                _inbound.CompleteAdding();
            }
        }

        /// <summary>
        /// One pass: wait up to timeout for input, handle it (and whatever else is already queued, up to
        /// MaxItemsPerPass), then run a tick if one is due. Returns the number of inbound items handled.
        /// </summary>
        public int RunOnce(TimeSpan timeout)
        {
            int handled = 0;
            InboundItem item;
            if (_inbound.TryTake(out item, timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout))
            {
                do
                {
                    handled++;
                    Handle(item);
                }
                while (handled < MaxItemsPerPass && !_stopRequested && _inbound.TryTake(out item, TimeSpan.Zero));
            }
            RunModuleCalls();
            ProcessEvents();

            DateTime now = _clock.Now;
            if (now >= _nextTick)
            {
                Tick();
                _nextTick += TickInterval;
                if (_nextTick < now) { _nextTick = now + TickInterval; } // skip missed ticks, no catch-up burst
                ProcessEvents();
            }
            return handled;
        }

        /// <summary>The per-tick work. Public so tests can step ticks directly.</summary>
        public void Tick()
        {
            DateTime started = _clock.Now;
            try { _onTick?.Invoke(); }
            catch (Exception ex) { _log("Exception in tick: " + ex); }

            _tickCount++;
            if (_tickCount % HeartbeatEvery == 0)
            {
                try { _onHeartbeat?.Invoke(); }
                catch (Exception ex) { _log("Exception in heartbeat: " + ex); }
            }

            TimeSpan took = _clock.Now - started;
            if (took > TickInterval)
            {
                _log(string.Format("WARNING: tick took {0:0} ms (interval {1:0} ms).", took.TotalMilliseconds, TickInterval.TotalMilliseconds));
            }
        }

        // Events queued by what the inbound items and the tick just did. Events over the budget wait for the next call.
        private void ProcessEvents()
        {
            if (_processEvents == null) { return; }
            try { _processEvents(); }
            catch (Exception ex) { _log("Exception processing events: " + ex); }
        }

        private void Handle(InboundItem item)
        {
            try { _handle(item); }
            catch (Exception ex) { _log("Exception handling " + item.Kind + ": " + ex); }
        }
    }
}
