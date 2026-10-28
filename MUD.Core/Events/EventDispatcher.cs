using System;
using System.Collections.Generic;
using MUD.Core.Components;

namespace MUD.Core.Events
{
    // Receives every event after it has been delivered to the behavior entries of its parties. A tap for tests,
    // logging and the later module adapter; it is not a hook, so it can neither veto nor change anything.
    public interface IEventObserver
    {
        void Observe(GameEvent gameEvent);
    }

    // The game thread's event queue and After-phase dispatcher (owner decisions D4, D9, D59).
    //
    // All of it runs on the game thread. Emit only queues; Pump processes queued events in order, at most the per-pass
    // budget at a time, and never inside another Pump. So a handler that emits (or that changes a component, which
    // emits component.set) cannot recurse: its events wait their turn, one cascade level deeper.
    //
    // Fast path: delivering an event whose parties carry no matching behavior entries does no allocation and
    // no handler lookup (see EventDispatcherFastPathTests for what is measured).
    //
    // Change events (component.set, component.remove, behavior.add, behavior.remove) have no hook name in version 1,
    // so no behavior entry can intercept them (D59); they reach observers only.
    public class EventDispatcher
    {
        [ThreadStatic]
        private static EventDispatcher _current;

        // The dispatcher bound to the calling thread (the game thread, see Bind), or null.
        public static EventDispatcher Current { get { return _current; } }

        private readonly Queue<GameEvent> _queue = new Queue<GameEvent>();
        private readonly List<IEventObserver> _observers = new List<IEventObserver>();
        private readonly IEventHandlerResolver _resolver;
        private readonly Action<string> _log;
        private long _lastEventId;
        private bool _pumping;
        private GameEvent _currentEvent;
        private EventOrigin _currentOrigin;

        public EventLimits Limits { get; }

        // Counters for the owner to read (D34: logs and counts are the record for rejections).
        public long RejectedCascadeDepth { get; private set; }

        public long RejectedReentrancy { get; private set; }

        public long Processed { get; private set; }

        public long HandlerFailures { get; private set; }

        public int QueuedCount { get { return _queue.Count; } }

        public EventDispatcher(IEventHandlerResolver resolver = null, EventLimits limits = null, Action<string> log = null)
        {
            _resolver = resolver ?? NoEventHandlers.Instance;
            Limits = limits ?? new EventLimits();
            _log = log ?? Console.WriteLine;
        }

        // Makes this the dispatcher that GameObject.SetComponent and the behavior-list helpers emit change events
        // to, for the calling thread only. Dispose the result to unbind. World does this on the game thread.
        public IDisposable Bind()
        {
            EventDispatcher previous = _current;
            _current = this;
            return new Binding(previous);
        }

        private sealed class Binding : IDisposable
        {
            private readonly EventDispatcher _previous;

            public Binding(EventDispatcher previous) { _previous = previous; }

            public void Dispose() { _current = _previous; }
        }

        public void AddObserver(IEventObserver observer)
        {
            if (observer == null) { throw new ArgumentNullException(nameof(observer)); }
            _observers.Add(observer);
        }

        // Change events are only built when someone can receive them, so unobserved world edits (imports, tests)
        // cost nothing.
        public bool WantsChangeEvents { get { return _observers.Count > 0; } }

        // Queues a new event. While a handler is running the event is caused by the handler's delivery; otherwise it is
        // a root event. Returns the queued event, or null when the cascade depth or the re-entrancy guard refuses it.
        // Only the After phase exists until card P3-2.
        public GameEvent Emit(string name, EventPhase phase, IReadOnlyList<EventParty> parties, IReadOnlyDictionary<string, object> payload = null)
        {
            if (string.IsNullOrEmpty(name)) { throw new ArgumentException("An event needs a name.", nameof(name)); }
            if (phase != EventPhase.After) { throw new NotSupportedException("The Before phase is not processed yet (card P3-2)."); }

            GameEvent cause = _currentEvent;
            EventOrigin origin = _currentOrigin;
            if (cause != null)
            {
                if (cause.Depth + 1 > Limits.MaxCascadeDepth)
                {
                    RejectedCascadeDepth++;
                    LogRejection("cascade depth " + Limits.MaxCascadeDepth + " exceeded", name, origin, cause, parties);
                    return null;
                }
                if (origin != null && CauseChainContains(cause, name, origin))
                {
                    RejectedReentrancy++;
                    LogRejection("re-entrancy guard", name, origin, cause, parties);
                    return null;
                }
            }

            long id = ++_lastEventId;
            string trace = cause == null ? id.ToString() : cause.TraceId;
            var created = new GameEvent(name, phase, id, trace, cause, origin, parties, payload);
            _queue.Enqueue(created);
            return created;
        }

        // The rule of owner decision D9: reject an event whose cause chain already contains the same behavior (module
        // and type), entity and event name.
        private static bool CauseChainContains(GameEvent cause, string name, EventOrigin origin)
        {
            for (GameEvent ancestor = cause; ancestor != null; ancestor = ancestor.Cause)
            {
                if (string.Equals(ancestor.Name, name, StringComparison.Ordinal) && origin.Matches(ancestor.Origin)) { return true; }
            }
            return false;
        }

        private void LogRejection(string reason, string name, EventOrigin origin, GameEvent cause, IReadOnlyList<EventParty> parties)
        {
            _log("Event refused (" + reason + "): " + name
                + ", origin " + (origin == null ? "core" : origin.ToString())
                + ", trace " + cause.TraceId + ", cause " + cause.EventId
                + ", targets " + (parties == null ? "none" : string.Join(",", parties)) + ".");
        }

        // Processes queued events, at most maxEvents of them, and returns how many it processed. Events beyond the
        // budget stay queued for the next call. A call made while a pump is already running does nothing.
        public int Pump(int maxEvents)
        {
            if (_pumping || maxEvents <= 0) { return 0; }
            _pumping = true;
            int processed = 0;
            try
            {
                while (processed < maxEvents && _queue.Count > 0)
                {
                    Process(_queue.Dequeue());
                    processed++;
                }
            }
            finally
            {
                _pumping = false;
            }
            return processed;
        }

        // Pump with the configured per-pass budget.
        public int Pump()
        {
            return Pump(Limits.MaxEventsPerPass);
        }

        private void Process(GameEvent gameEvent)
        {
            Processed++;
            DeliverToParties(gameEvent);
            for (int index = 0; index < _observers.Count; index++)
            {
                try { _observers[index].Observe(gameEvent); }
                catch (Exception ex) { HandlerFailures++; _log("Exception in event observer for " + gameEvent + ": " + ex); }
            }
        }

        // The After phase: each party's own list is filtered by the hook names the event gives that party, and each
        // matching entry is resolved to a handler by (Module, Type). No loop here allocates.
        private void DeliverToParties(GameEvent gameEvent)
        {
            IReadOnlyList<EventParty> parties = gameEvent.Parties;
            for (int partyIndex = 0; partyIndex < parties.Count; partyIndex++)
            {
                EventParty party = parties[partyIndex];
                string[] hooks = party.Hooks;
                if (hooks.Length == 0) { continue; }
                var holder = party.Entity as IBehaviorHolder;
                if (holder == null) { continue; }
                List<BehaviorRef> entries = holder.Behaviors;
                if (entries == null || entries.Count == 0) { continue; }

                for (int hookIndex = 0; hookIndex < hooks.Length; hookIndex++)
                {
                    for (int entryIndex = 0; entryIndex < entries.Count; entryIndex++)
                    {
                        BehaviorRef entry = entries[entryIndex];
                        if (entry == null || !string.Equals(entry.Hook, hooks[hookIndex], StringComparison.Ordinal)) { continue; }
                        Deliver(gameEvent, party, entry, hooks[hookIndex]);
                    }
                }
            }
        }

        private void Deliver(GameEvent gameEvent, EventParty party, BehaviorRef entry, string hook)
        {
            IEventHandler handler;
            // Unresolved: the entry is dormant. After verdicts are ignored, so skipping is all there is to do here;
            // dormant counting and the once-per-interval log belong to card P3-3 (D10).
            if (!_resolver.TryResolve(entry.Module, entry.Type, out handler) || handler == null) { return; }

            GameEvent previousEvent = _currentEvent;
            EventOrigin previousOrigin = _currentOrigin;
            _currentEvent = gameEvent;
            _currentOrigin = new EventOrigin(entry.Module, entry.Type, party.Entity.Id);
            try
            {
                handler.Handle(new EventDelivery(this, gameEvent, party, entry, hook));
            }
            catch (Exception ex)
            {
                HandlerFailures++;
                _log("Exception in handler " + entry.Module + "/" + entry.Type + " for " + gameEvent + ": " + ex);
            }
            finally
            {
                _currentEvent = previousEvent;
                _currentOrigin = previousOrigin;
            }
        }
    }
}
