using System;
using System.Collections.Generic;
using MUD.Core.Components;
using MUD.Core.GameObjects;
using MUD.Data;

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
        private readonly Action<string> _log;
        private long _lastEventId;
        private long _lastDeliveryId;
        private bool _pumping;
        private GameEvent _currentEvent;
        private EventOrigin _currentOrigin;

        // Before chains that are waiting, and the chain each outstanding delivery belongs to (P3-2).
        private readonly List<BeforeChain> _chains = new List<BeforeChain>();
        private readonly Dictionary<long, BeforeChain> _deliveries = new Dictionary<long, BeforeChain>();

        // Actions a handler may request by emitting an event with a registered name (for example entity.move). A request
        // is queued and run on the game thread with the emitting delivery as its cause.
        private readonly Dictionary<string, Func<EmittedEvent, bool>> _actions = new Dictionary<string, Func<EmittedEvent, bool>>(StringComparer.Ordinal);
        private readonly Queue<PendingRequest> _requests = new Queue<PendingRequest>();

        private sealed class PendingRequest
        {
            public Func<EmittedEvent, bool> Run;
            public EmittedEvent Emitted;
            public GameEvent Cause;
            public EventOrigin Origin;
        }

        // Maps (Module, Type) to handlers. Replaceable; the default is the in-process registry (owner decision D49).
        public IEventHandlerResolver Resolver { get; set; }

        // The entities that carry a heartbeat entry (D56).
        public HeartbeatIndex Heartbeats { get; } = new HeartbeatIndex();

        // The tick number the next heartbeat deliveries carry in their payload (set by the world once per heartbeat).
        public long HeartbeatTick { get; set; }

        // Dormant entries (D10): behavior entries skipped because nothing is registered for their (Module, Type). The total
        // counts every skip since start; the per-interval counts are summarised in the log at most once per
        // EventLimits.DormantLogInterval and then reset.
        public long DormantSkips { get; private set; }

        public long DormantLogLines { get; private set; }

        private readonly Dictionary<(string Module, string Type), long> _dormantSinceLastLog = new Dictionary<(string Module, string Type), long>();
        private DateTime _lastDormantLog = DateTime.MinValue;
        private readonly List<GameObject> _heartbeatBuffer = new List<GameObject>();

        // The game clock the Before chain deadline is measured on (a fake clock in tests).
        public IClock Clock { get; }

        // Which (Module, Type) entries fail closed (owner decision D60); empty, so everything fails open.
        public FailPolicy FailPolicy { get; } = new FailPolicy();

        public EventLimits Limits { get; }

        // Before-phase counters (D34: logs and counts are the record).
        public long BeforeMisses { get; private set; }

        public long BeforeClosedMisses { get; private set; }

        public long LateVerdicts { get; private set; }

        public int WaitingChains { get { return _chains.Count; } }

        // Counters for the owner to read (D34: logs and counts are the record for rejections).
        public long RejectedCascadeDepth { get; private set; }

        public long RejectedReentrancy { get; private set; }

        public long Processed { get; private set; }

        public long HandlerFailures { get; private set; }

        public int QueuedCount { get { return _queue.Count + _requests.Count; } }

        public EventDispatcher(IEventHandlerResolver resolver = null, EventLimits limits = null, Action<string> log = null, IClock clock = null)
        {
            Resolver = resolver ?? InProcessBehaviorRegistry.Shared;
            Limits = limits ?? new EventLimits();
            _log = log ?? Console.WriteLine;
            Clock = clock ?? SystemClock.Instance;
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
            if (phase != EventPhase.After) { throw new NotSupportedException("A Before event is not queued: start its chain with BeginBefore."); }

            GameEvent created = Create(name, phase, parties, payload, _currentEvent, _currentOrigin);
            if (created != null) { _queue.Enqueue(created); }
            return created;
        }

        // Applies the cascade depth limit and the re-entrancy guard (D9) and builds the event. Null when refused.
        private GameEvent Create(string name, EventPhase phase, IReadOnlyList<EventParty> parties, IReadOnlyDictionary<string, object> payload, GameEvent cause, EventOrigin origin)
        {
            if (cause != null)
            {
                string reason = CheckCascade(name, cause, origin);
                if (reason != null)
                {
                    LogRejection(reason, name, origin, cause, parties);
                    return null;
                }
            }

            long id = ++_lastEventId;
            string trace = cause == null ? id.ToString() : cause.TraceId;
            return new GameEvent(name, phase, id, trace, cause, origin, parties, payload);
        }

        // Null when allowed, otherwise the reason it is refused (and counted).
        private string CheckCascade(string name, GameEvent cause, EventOrigin origin)
        {
            if (cause.Depth + 1 > Limits.MaxCascadeDepth)
            {
                RejectedCascadeDepth++;
                return "cascade depth " + Limits.MaxCascadeDepth + " exceeded";
            }
            if (origin != null && CauseChainContains(cause, name, origin))
            {
                RejectedReentrancy++;
                return "re-entrancy guard";
            }
            return null;
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
                // Chains whose deadline has passed end first (their handlers become misses, D15); finishing a chain may
                // commit an action and queue events, which the loop below then processes.
                ExpireChains();
                ReportDormant();
                while (processed < maxEvents && (_requests.Count > 0 || _queue.Count > 0))
                {
                    if (_requests.Count > 0) { RunRequest(_requests.Dequeue()); }
                    else { Process(_queue.Dequeue()); }
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

        // ---- The Before phase (card P3-2; owner decisions D6, D7, D8, D15, D60, D61, D63) ----

        // Lets a handler's emitted event with this name ask core to do something (for example entity.move: a redirect is a
        // Deny plus a new move, D6). The request runs on the game thread with the emitting delivery as its cause, so it
        // goes through the cascade depth limit and the re-entrancy guard, and runs its own Before chain.
        public void RegisterAction(string name, Func<EmittedEvent, bool> run)
        {
            if (string.IsNullOrEmpty(name)) { throw new ArgumentException("An action needs a name.", nameof(name)); }
            if (run == null) { throw new ArgumentNullException(nameof(run)); }
            _actions[name] = run;
        }

        // True when the entity carries at least one behavior entry under the hook. Allocation free; this is the whole cost of
        // an unhooked action (D9 fast path).
        public static bool HasEntry(object entity, string hook)
        {
            var holder = entity as IBehaviorHolder;
            if (holder == null) { return false; }
            List<BehaviorRef> entries = holder.Behaviors;
            if (entries == null) { return false; }
            for (int index = 0; index < entries.Count; index++)
            {
                BehaviorRef entry = entries[index];
                if (entry != null && string.Equals(entry.Hook, hook, StringComparison.Ordinal)) { return true; }
            }
            return false;
        }

        // True when any party carries a behavior entry under one of its hooks. The caller can skip building anything when not.
        public static bool AnyEntries(IReadOnlyList<EventParty> parties)
        {
            for (int partyIndex = 0; partyIndex < parties.Count; partyIndex++)
            {
                if (HasEntryFor(parties[partyIndex])) { return true; }
            }
            return false;
        }

        private static bool HasEntryFor(EventParty party)
        {
            string[] hooks = party.Hooks;
            if (hooks.Length == 0) { return false; }
            var holder = party.Entity as IBehaviorHolder;
            if (holder == null) { return false; }
            List<BehaviorRef> entries = holder.Behaviors;
            if (entries == null) { return false; }
            for (int entryIndex = 0; entryIndex < entries.Count; entryIndex++)
            {
                BehaviorRef entry = entries[entryIndex];
                if (entry == null) { continue; }
                for (int hookIndex = 0; hookIndex < hooks.Length; hookIndex++)
                {
                    if (string.Equals(entry.Hook, hooks[hookIndex], StringComparison.Ordinal)) { return true; }
                }
            }
            return false;
        }

        // Queues an After notification for an action that has committed. Does nothing (and allocates no event) when no party
        // carries a matching entry and nobody observes events, so unhooked actions pay nothing (D9 fast path).
        public GameEvent EmitAfter(string name, IReadOnlyList<EventParty> parties, IReadOnlyDictionary<string, object> payload = null)
        {
            if (_observers.Count == 0 && !AnyEntries(parties)) { return null; }
            return Emit(name, EventPhase.After, parties, payload);
        }

        // Starts a Before chain for an action and calls onComplete exactly once with the verdict: before this method returns
        // when the answer is immediate (fast path: nothing is hooked; or every handler replied inline), otherwise later from
        // the game thread, when the last handler replies, the deadline passes or the chain is cancelled.
        //
        // sequential false (D15): every matching entry of every party is asked at once and all are waited for. sequential
        // true (D63, command.entered only): entries are asked one at a time, in group order then ascending Priority then
        // stored order, until one handles the line.
        //
        // Returns the chain while it waits, otherwise null or a completed chain. The caller parks the acting entity while
        // chain.IsComplete is false.
        public BeforeChain BeginBefore(string name, IReadOnlyList<EventParty> parties, IReadOnlyDictionary<string, object> payload, bool sequential, Action<BeforeOutcome> onComplete)
        {
            if (string.IsNullOrEmpty(name)) { throw new ArgumentException("An event needs a name.", nameof(name)); }
            if (parties == null) { throw new ArgumentNullException(nameof(parties)); }
            if (onComplete == null) { throw new ArgumentNullException(nameof(onComplete)); }

            List<BeforeCandidate> candidates = CollectCandidates(parties, sequential);
            if (candidates == null)
            {
                onComplete(BeforeOutcome.AllowedWithoutHandlers);
                return null;
            }

            GameEvent cause = _currentEvent;
            EventOrigin origin = _currentOrigin;
            GameEvent created = Create(name, EventPhase.Before, parties, payload, cause, origin);
            if (created == null)
            {
                onComplete(new BeforeOutcome(BeforeResult.Refused, null, false, 0, null));
                return null;
            }

            var chain = new BeforeChain(this, created, sequential, onComplete, cause, origin);
            _chains.Add(chain);
            chain.Start(candidates);
            return chain;
        }

        // The entries a chain will ask, or null when there are none (nothing hooked: commit at once).
        private static List<BeforeCandidate> CollectCandidates(IReadOnlyList<EventParty> parties, bool sequential)
        {
            List<BeforeCandidate> found = null;
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
                        if (found == null) { found = new List<BeforeCandidate>(); }
                        found.Add(new BeforeCandidate { Party = party, Entry = entry, Hook = hooks[hookIndex] });
                    }
                }
            }
            if (found == null) { return null; }

            if (sequential)
            {
                // Group (closeness to the player), then ascending Priority, then the stored order (D64). OrderBy is stable.
                var ordered = new List<BeforeCandidate>(found.Count);
                foreach (BeforeCandidate candidate in System.Linq.Enumerable.ThenBy(
                    System.Linq.Enumerable.OrderBy(found, c => c.Party.Group), c => c.Entry.Priority))
                {
                    ordered.Add(candidate);
                }
                return ordered;
            }
            return found;
        }

        internal bool TryResolveHandler(BehaviorRef entry, out IEventHandler handler)
        {
            handler = null;
            return Resolver.TryResolve(entry.Module, entry.Type, out handler) && handler != null;
        }

        internal BeforeDelivery RegisterDelivery(BeforeChain chain, BeforeCandidate candidate, IReadOnlyList<EntitySnapshot> snapshots)
        {
            long id = ++_lastDeliveryId;
            _deliveries[id] = chain;
            return new BeforeDelivery(this, id, chain.Event, candidate.Party, candidate.Entry, candidate.Hook, snapshots);
        }

        internal void ForgetDelivery(long deliveryId)
        {
            _deliveries.Remove(deliveryId);
        }

        internal void ChainFinished(BeforeChain chain)
        {
            _chains.Remove(chain);
        }

        // Runs code as if it were the entry's handler: events it emits get the delivery's event as their cause and the
        // entry as their origin, which is what the cascade and re-entrancy guards read.
        internal void RunAsHandler(GameEvent gameEvent, EventParty party, BehaviorRef entry, Action action)
        {
            RunInContext(gameEvent, new EventOrigin(entry.Module, entry.Type, party.Entity.Id), action);
        }

        internal void RunInContext(GameEvent gameEvent, EventOrigin origin, Action action)
        {
            GameEvent previousEvent = _currentEvent;
            EventOrigin previousOrigin = _currentOrigin;
            _currentEvent = gameEvent;
            _currentOrigin = origin;
            try { action(); }
            finally
            {
                _currentEvent = previousEvent;
                _currentOrigin = previousOrigin;
            }
        }

        internal void HandlerFailed(BehaviorRef entry, GameEvent gameEvent, Exception ex)
        {
            HandlerFailures++;
            _log("Exception in Before handler " + (entry == null ? "(chain completion)" : entry.Module + "/" + entry.Type) + " for " + gameEvent + ": " + ex);
        }

        // A handler that could not answer (D10, D60). A miss is counted; an unresolved entry is also counted as dormant and
        // reported once per interval (ReportDormant) instead of being logged each time.
        internal void CountMiss(BehaviorRef entry, string hook, string why, bool failClosed)
        {
            BeforeMisses++;
            if (failClosed) { BeforeClosedMisses++; }
            if (why.StartsWith("unresolved", StringComparison.Ordinal))
            {
                NoteDormant(entry);
                return;
            }
            _log("Before handler " + entry.Module + "/" + entry.Type + " (" + hook + ") " + why + "; " + (failClosed ? "failing closed." : "failing open."));
        }

        // Applies the events a handler emitted in its reply (D8: whether or not the action is denied). An event that core
        // refuses (cascade depth, re-entrancy guard) is skipped, listed and logged; the rest apply and the verdict counts (D61).
        internal int ApplyEmitted(GameEvent cause, EventParty party, BehaviorRef entry, IReadOnlyList<EmittedEvent> emitted, out List<string> refused, out List<int> refusedPositions)
        {
            var origin = new EventOrigin(entry.Module, entry.Type, party.Entity.Id);
            return ApplyEmittedFrom(cause, origin, emitted, out refused, out refusedPositions);
        }

        // The same rules for events that a module emits through the Emit call (card P4-4): the cause may be null (a root event,
        // for example one that follows a timer) and the origin names the module. Refused events are listed with their reason and
        // with their position in the submitted list; the rest are applied (D61). Game thread only.
        public int ApplyEmittedFrom(GameEvent cause, EventOrigin origin, IReadOnlyList<EmittedEvent> emitted, out List<string> refused, out List<int> refusedPositions)
        {
            refused = null;
            refusedPositions = null;
            if (emitted == null || emitted.Count == 0) { return 0; }
            int applied = 0;
            for (int index = 0; index < emitted.Count; index++)
            {
                EmittedEvent request = emitted[index];
                if (request == null || string.IsNullOrEmpty(request.Name) || request.Parties == null)
                {
                    if (refused == null) { refused = new List<string>(); refusedPositions = new List<int>(); }
                    refused.Add((request == null ? "(null)" : request.Name ?? "(no name)") + ": missing name or targets");
                    refusedPositions.Add(index);
                    continue;
                }
                string reason = cause == null ? null : CheckCascade(request.Name, cause, origin);
                if (reason != null)
                {
                    LogRejection(reason, request.Name, origin, cause, request.Parties);
                    if (refused == null) { refused = new List<string>(); refusedPositions = new List<int>(); }
                    refused.Add(request.Name + ": " + reason);
                    refusedPositions.Add(index);
                    continue;
                }

                Func<EmittedEvent, bool> action;
                if (_actions.TryGetValue(request.Name, out action))
                {
                    _requests.Enqueue(new PendingRequest { Run = action, Emitted = request, Cause = cause, Origin = origin });
                }
                else
                {
                    GameEvent created = Create(request.Name, EventPhase.After, request.Parties, request.Payload, cause, origin);
                    if (created == null) { continue; }
                    _queue.Enqueue(created);
                }
                applied++;
            }
            return applied;
        }

        private void RunRequest(PendingRequest request)
        {
            Processed++;
            try
            {
                RunInContext(request.Cause, request.Origin, () =>
                {
                    if (!request.Run(request.Emitted))
                    {
                        _log("Event request " + request.Emitted.Name + " could not be carried out (cause " + (request.Cause == null ? 0 : request.Cause.EventId) + ").");
                    }
                });
            }
            catch (Exception ex)
            {
                HandlerFailures++;
                _log("Exception running event request " + request.Emitted.Name + ": " + ex);
            }
        }

        // A handler's answer (from the game thread: an in-process handler calls it inline, an out-of-process verdict
        // arrives as an inbound item). A verdict for a chain that has ended, expired or been cancelled is dropped and
        // counted (CORE_API 2.5).
        public VerdictAcknowledgement SubmitVerdict(long deliveryId, Verdict verdict)
        {
            if (verdict == null) { throw new ArgumentNullException(nameof(verdict)); }
            BeforeChain chain;
            if (_deliveries.TryGetValue(deliveryId, out chain) && !chain.IsComplete && Clock.Now >= chain.Deadline)
            {
                // The deadline has passed but the loop has not processed it yet: this reply is late.
                chain.Expire();
                _deliveries.TryGetValue(deliveryId, out chain);
            }
            if (chain == null || chain.IsComplete || !_deliveries.ContainsKey(deliveryId))
            {
                LateVerdicts++;
                _log("Late or unknown verdict dropped for delivery " + deliveryId + ".");
                return new VerdictAcknowledgement(false, "late, cancelled or unknown delivery", null, 0);
            }
            return chain.Record(deliveryId, verdict);
        }

        // Ends every waiting chain whose deadline has passed (called at the start of each Pump).
        private void ExpireChains()
        {
            if (_chains.Count == 0) { return; }
            DateTime now = Clock.Now;
            BeforeChain[] waiting = _chains.ToArray();
            for (int index = 0; index < waiting.Length; index++)
            {
                BeforeChain chain = waiting[index];
                if (!chain.IsComplete && now >= chain.Deadline) { chain.Expire(); }
            }
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

        // ---- Dormant entries (D10) ----

        // Counts one skipped entry. The log line is written by ReportDormant, not here, so a flood of skips costs a
        // dictionary update and nothing else.
        private void NoteDormant(BehaviorRef entry)
        {
            DormantSkips++;
            var key = (entry.Module ?? string.Empty, entry.Type ?? string.Empty);
            long seen;
            _dormantSinceLastLog.TryGetValue(key, out seen);
            _dormantSinceLastLog[key] = seen + 1;
        }

        // The skips counted since the last report, per (Module, Type). A copy.
        public IReadOnlyDictionary<(string Module, string Type), long> DormantSinceLastLog
        {
            get { return new Dictionary<(string Module, string Type), long>(_dormantSinceLastLog); }
        }

        // Writes one log line summarising the dormant entries skipped since the last line, when there are any and at least
        // DormantLogInterval has passed since the last line; then resets the per-interval counts. Called on every Pump.
        private void ReportDormant()
        {
            if (_dormantSinceLastLog.Count == 0) { return; }
            DateTime now = Clock.Now;
            if (now - _lastDormantLog < Limits.DormantLogInterval) { return; }

            long total = 0;
            var parts = new List<string>(_dormantSinceLastLog.Count);
            foreach (KeyValuePair<(string Module, string Type), long> pair in _dormantSinceLastLog)
            {
                total += pair.Value;
                parts.Add(pair.Key.Module + "/" + pair.Key.Type + " x" + pair.Value);
            }
            parts.Sort(StringComparer.Ordinal);
            _dormantSinceLastLog.Clear();
            _lastDormantLog = now;
            DormantLogLines++;
            _log("Dormant behavior entries skipped (" + total + " in this interval, " + DormantSkips + " since start; no handler is registered for them): " + string.Join(", ", parts) + ".");
        }

        // ---- Heartbeat (G8, D56) ----

        private static readonly string[] HeartbeatHooks = { BehaviorHooks.Heartbeat };

        // Delivers one heartbeat event to every heartbeat entry the entity carries, now, on the game thread. Entries nothing
        // is registered for are dormant and skipped. A handler that throws is logged and does not stop the others. Events a
        // handler emits are caused by the heartbeat event and are queued like any other. Does nothing, and allocates nothing,
        // for an entity with no heartbeat entry.
        public void DeliverHeartbeat(GameObject entity)
        {
            if (entity == null || !HasEntry(entity, BehaviorHooks.Heartbeat)) { return; }

            // Copied first: a handler may remove its own entry (bleeding does when it ends).
            List<BehaviorRef> entries = ((IBehaviorHolder)entity).Behaviors;
            var matching = new List<BehaviorRef>(entries.Count);
            for (int index = 0; index < entries.Count; index++)
            {
                BehaviorRef entry = entries[index];
                if (entry != null && string.Equals(entry.Hook, BehaviorHooks.Heartbeat, StringComparison.Ordinal)) { matching.Add(entry); }
            }

            var party = new EventParty("self", entity, HeartbeatHooks);
            GameEvent created = Create(EventNames.Heartbeat, EventPhase.After, new[] { party },
                new Dictionary<string, object> { { "tick", HeartbeatTick } }, _currentEvent, _currentOrigin);
            if (created == null) { return; }
            for (int index = 0; index < matching.Count; index++)
            {
                Deliver(created, party, matching[index], BehaviorHooks.Heartbeat);
            }
        }

        // Delivers the heartbeat to every indexed entity (players are ticked by the world and deliver their own entries).
        // An entity that has lost its last heartbeat entry is dropped from the index. One entity's failure does not stop
        // the rest. Returns how many entities were delivered to.
        public int DeliverIndexedHeartbeats()
        {
            if (Heartbeats.Count == 0) { return 0; }
            Heartbeats.CopyTo(_heartbeatBuffer);
            int delivered = 0;
            for (int index = 0; index < _heartbeatBuffer.Count; index++)
            {
                GameObject entity = _heartbeatBuffer[index];
                if (!HasEntry(entity, BehaviorHooks.Heartbeat))
                {
                    Heartbeats.Untrack(entity);
                    continue;
                }
                try
                {
                    DeliverHeartbeat(entity);
                    delivered++;
                }
                catch (Exception ex)
                {
                    HandlerFailures++;
                    _log("Exception delivering the heartbeat to " + entity.Id + ": " + ex);
                }
            }
            _heartbeatBuffer.Clear();
            return delivered;
        }

        private void Deliver(GameEvent gameEvent, EventParty party, BehaviorRef entry, string hook)
        {
            IEventHandler handler;
            // Unresolved: the entry is dormant. After verdicts are ignored, so skipping and counting is all there is to do
            // here (D10).
            if (!Resolver.TryResolve(entry.Module, entry.Type, out handler) || handler == null)
            {
                NoteDormant(entry);
                return;
            }

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
