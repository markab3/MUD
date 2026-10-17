using System;
using System.Collections.Concurrent;
using MUD.Data;
using MUD.Telnet;

namespace MUD.Core
{
    public enum InboundKind
    {
        /// <summary>A line of text typed by a client.</summary>
        Line,
        /// <summary>A client has connected (before login).</summary>
        Connected,
        /// <summary>A client's connection has closed, for any reason.</summary>
        Disconnected,
        /// <summary>A callback to run on the game thread (persistence outcome, shutdown request, ...).</summary>
        WorldEvent
    }

    /// <summary>One message from the outside world to the game thread. Immutable after construction.</summary>
    public sealed class InboundItem
    {
        public InboundKind Kind { get; private set; }
        public IClientConnection Client { get; private set; }
        public string Line { get; private set; }
        public Action Action { get; private set; }

        private InboundItem() { }

        public static InboundItem ForLine(IClientConnection client, string line)
        {
            return new InboundItem { Kind = InboundKind.Line, Client = client, Line = line };
        }

        public static InboundItem ForConnected(IClientConnection client)
        {
            return new InboundItem { Kind = InboundKind.Connected, Client = client };
        }

        public static InboundItem ForDisconnected(IClientConnection client)
        {
            return new InboundItem { Kind = InboundKind.Disconnected, Client = client };
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

        private readonly IClock _clock;
        private readonly Action<InboundItem> _handle;
        private readonly Action _onTick;
        private readonly Action _onHeartbeat;
        private readonly Action<string> _log;
        private readonly BlockingCollection<InboundItem> _inbound = new BlockingCollection<InboundItem>(new ConcurrentQueue<InboundItem>());
        private DateTime _nextTick;
        private int _tickCount;
        private volatile bool _stopRequested;

        public GameLoop(IClock clock, Action<InboundItem> handle, Action onTick, Action onHeartbeat, Action<string> log = null)
        {
            _clock = clock;
            _handle = handle;
            _onTick = onTick;
            _onHeartbeat = onHeartbeat;
            _log = log ?? Console.WriteLine;
            _nextTick = _clock.Now + TickInterval;
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

            DateTime now = _clock.Now;
            if (now >= _nextTick)
            {
                Tick();
                _nextTick += TickInterval;
                if (_nextTick < now) { _nextTick = now + TickInterval; } // skip missed ticks, no catch-up burst
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

        private void Handle(InboundItem item)
        {
            try { _handle(item); }
            catch (Exception ex) { _log("Exception handling " + item.Kind + ": " + ex); }
        }
    }
}
