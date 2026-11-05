using System;
using System.Collections.Generic;
using System.Linq;
using Google.Protobuf.WellKnownTypes;
using MongoDB.Bson;
using MUD.Contracts;
using MUD.Core.Components;
using MUD.Core.Events;
using MUD.Core.GameObjects;
using MUD.Data;

namespace MUD.Core.Modules
{
    /// <summary>
    /// Connects the (Module, Type) dictionary of phase 3 to the modules that are connected (card P4-4; owner decisions D5, D16, D49,
    /// D61, D65, D74). It is the second adapter next to the in-process registry: the event dispatcher asks it to resolve a behavior
    /// entry, and when the module is connected and ready it hands back a handler that writes a delivery to the module's server stream.
    ///
    /// It also holds what the module's unary calls need: the table of Before deliveries that wait for a verdict, the module's emitted
    /// events (with the capability check and the emission quota), and the module's timers. Timers live in memory only and belong to the
    /// connection (D74): a closed connection loses them.
    ///
    /// Threading: TryResolve, the handlers, Tick, SubmitVerdict and Emit run on the game thread. Timer scheduling, state calls and the
    /// disconnect notification may arrive on any thread, so the shared tables are guarded by one lock.
    /// </summary>
    public sealed class ModuleDispatcher : IEventHandlerResolver
    {
        /// <summary>How long a delivery that nobody answered stays in the table after the chain deadline, before it is forgotten.</summary>
        private static readonly TimeSpan PendingGrace = TimeSpan.FromSeconds(5);

        private readonly ModuleRegistry _registry;
        private readonly EventDispatcher _events;
        private readonly ModuleStateStore _state;
        private readonly ModuleHostOptions _options;
        private readonly IClock _clock;
        private readonly Func<string, GameObject> _findEntity;
        private readonly Action<string> _log;
        private readonly object _lock = new object();
        private readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>(StringComparer.Ordinal);
        private readonly Dictionary<string, RemoteHandler> _handlers = new Dictionary<string, RemoteHandler>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, TimerEntry>> _timers = new Dictionary<string, Dictionary<string, TimerEntry>>(StringComparer.Ordinal);
        private readonly Dictionary<string, TokenBucket> _emitBuckets = new Dictionary<string, TokenBucket>(StringComparer.Ordinal);
        private long _afterCounter;
        private long _timerCounter;

        private sealed class Pending
        {
            public BeforeDelivery Delivery;
            public ModuleConnection Connection;
            public DateTime ForgetAfter;
        }

        private sealed class TimerEntry
        {
            public string Id;
            public DateTime Due;
            public Struct Payload;
        }

        public ModuleDispatcher(ModuleRegistry registry, EventDispatcher events, ModuleStateStore state, ModuleHostOptions options, IClock clock,
            Func<string, GameObject> findEntity, Action<string> log = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _events = events ?? throw new ArgumentNullException(nameof(events));
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _clock = clock ?? SystemClock.Instance;
            _findEntity = findEntity ?? throw new ArgumentNullException(nameof(findEntity));
            _log = log ?? Console.WriteLine;
            _registry.Disconnected += OnDisconnected;
        }

        /// <summary>Deliveries refused because a module's queue was full or its stream had ended (counted; the hook's fail policy applied).</summary>
        public long DroppedDeliveries { get; private set; }

        /// <summary>Verdicts that arrived for a delivery that was unknown, expired or answered already (late verdicts are dropped, D16).</summary>
        public long DroppedVerdicts { get; private set; }

        public int PendingVerdictCount { get { lock (_lock) { return _pending.Count; } } }

        public int TimerCount(ModuleConnection connection)
        {
            lock (_lock)
            {
                Dictionary<string, TimerEntry> held;
                return _timers.TryGetValue(connection.ConnectionId, out held) ? held.Count : 0;
            }
        }

        // ---- the (Module, Type) dictionary ----

        /// <summary>
        /// True when the module is connected, has called Ready, has an open stream and declared the behavior type in its manifest.
        /// Otherwise the entry is dormant and the hook's fail policy applies (D10, D60).
        /// </summary>
        public bool TryResolve(string module, string type, out IEventHandler handler)
        {
            handler = null;
            if (string.IsNullOrEmpty(module) || string.IsNullOrEmpty(type)) { return false; }
            ModuleConnection connection = _registry.FindByModule(module);
            if (connection == null || connection.IsClosed || !connection.IsReady || !connection.IsSubscribed) { return false; }
            if (!Declares(connection.Manifest, type)) { return false; }

            lock (_lock)
            {
                RemoteHandler found;
                if (!_handlers.TryGetValue(connection.ConnectionId, out found))
                {
                    found = new RemoteHandler(this, connection);
                    _handlers[connection.ConnectionId] = found;
                }
                handler = found;
                return true;
            }
        }

        private static bool Declares(Manifest manifest, string type)
        {
            if (manifest == null) { return false; }
            foreach (BehaviorDecl declared in manifest.Behaviors)
            {
                if (string.Equals(declared.Type, type, StringComparison.Ordinal)) { return true; }
            }
            return false;
        }

        private void OnDisconnected(ModuleConnection connection)
        {
            lock (_lock)
            {
                _handlers.Remove(connection.ConnectionId);
                // Timers are scoped to the connection and are not kept across a reconnect (D74).
                _timers.Remove(connection.ConnectionId);
                var forgotten = new List<string>();
                foreach (KeyValuePair<string, Pending> pair in _pending)
                {
                    if (ReferenceEquals(pair.Value.Connection, connection)) { forgotten.Add(pair.Key); }
                }
                // The waiting chains end at their deadline and treat the module as a miss.
                foreach (string key in forgotten) { _pending.Remove(key); }
            }
        }

        // ---- deliveries ----

        private CoreEvent BuildDelivery(string eventId, GameEvent gameEvent, Phase phase, EventParty party, Components.BehaviorRef entry, string hook, uint deadlineMilliseconds)
        {
            var delivery = new CoreEvent
            {
                EventId = eventId,
                Name = gameEvent.Name,
                Phase = phase,
                Hook = hook ?? string.Empty,
                DeadlineMs = deadlineMilliseconds,
                TraceId = gameEvent.TraceId ?? string.Empty,
                CauseId = gameEvent.CauseId == 0 ? string.Empty : gameEvent.CauseId.ToString(),
                Payload = StructConversion.FromDictionary(gameEvent.Payload)
            };
            if (entry != null)
            {
                delivery.Behavior = new Contracts.BehaviorRef
                {
                    Hook = entry.Hook ?? string.Empty,
                    Module = entry.Module ?? string.Empty,
                    Type = entry.Type ?? string.Empty,
                    Priority = entry.Priority,
                    Config = StructConversion.FromBson(entry.Config),
                    SchemaVersion = (uint)Math.Max(0, entry.SchemaVersion)
                };
            }
            foreach (EventParty each in gameEvent.Parties) { delivery.Parties.Add(Snapshot(each)); }
            return delivery;
        }

        // Core components only (D57): what core itself knows about the entity. Module components are not included, not even the
        // receiving module's own; a module reads those with GetEntity (D58).
        private static PartySnapshot Snapshot(EventParty party)
        {
            EntitySnapshot snapshot = EntitySnapshot.From(party);
            var components = new Struct();
            components.Fields["shortDescription"] = Value.ForString(snapshot.ShortDescription ?? string.Empty);
            if (snapshot.LocationId != null) { components.Fields["locationId"] = Value.ForString(snapshot.LocationId); }
            return new PartySnapshot { Role = snapshot.Role ?? string.Empty, EntityId = snapshot.Id ?? string.Empty, Kind = snapshot.Kind ?? string.Empty, Components = components };
        }

        private sealed class RemoteHandler : IEventHandler, IBeforeHandler
        {
            private readonly ModuleDispatcher _owner;
            private readonly ModuleConnection _connection;

            public RemoteHandler(ModuleDispatcher owner, ModuleConnection connection)
            {
                _owner = owner;
                _connection = connection;
            }

            // An After notification or a heartbeat: nothing comes back, the module's work is what it does and emits (D6).
            public void Handle(EventDelivery delivery)
            {
                string eventId = "a" + (++_owner._afterCounter);
                CoreEvent message = _owner.BuildDelivery(eventId, delivery.Event, Phase.After, delivery.Party, delivery.Entry, delivery.Hook, 0);
                if (!_connection.TryEnqueue(message))
                {
                    _owner.DroppedDeliveries++;
                    _owner._log("Delivery " + delivery.Event.Name + " to module '" + _connection.ModuleName + "' dropped: its queue is full or its stream has ended.");
                }
            }

            // A Before delivery: the verdict comes back as SubmitVerdict, matched by event_id (D16, D57). A queue that cannot take the
            // delivery makes the call throw, which the Before chain counts as a miss under the hook's fail policy (D60).
            public void Begin(BeforeDelivery delivery)
            {
                string eventId = delivery.DeliveryId.ToString();
                TimeSpan deadline = _owner._events.Limits.BeforeChainDeadline;
                CoreEvent message = _owner.BuildDelivery(eventId, delivery.Event, Phase.Before, delivery.Party, delivery.Entry, delivery.Hook, (uint)deadline.TotalMilliseconds);
                lock (_owner._lock)
                {
                    _owner._pending[eventId] = new Pending { Delivery = delivery, Connection = _connection, ForgetAfter = _owner._clock.Now + deadline + PendingGrace };
                }
                if (!_connection.TryEnqueue(message))
                {
                    lock (_owner._lock) { _owner._pending.Remove(eventId); }
                    _owner.DroppedDeliveries++;
                    throw new InvalidOperationException("module '" + _connection.ModuleName + "' cannot take the delivery: its queue is full or its stream has ended");
                }
            }
        }

        // ---- verdicts ----

        /// <summary>
        /// Game thread. Applies a module's answer to a Before delivery. A verdict for a delivery that is unknown, expired, already answered or
        /// belongs to another module is dropped and answered with ok = false (D16). Emitted events that core refuses are skipped and listed (D61).
        /// </summary>
        public VerdictReply SubmitVerdict(ModuleConnection connection, VerdictRequest request)
        {
            Pending found;
            lock (_lock)
            {
                if (!_pending.TryGetValue(request.EventId ?? string.Empty, out found) || !ReferenceEquals(found.Connection, connection))
                {
                    found = null;
                }
                else
                {
                    _pending.Remove(request.EventId);
                }
            }
            if (found == null)
            {
                DroppedVerdicts++;
                return new VerdictReply { Ok = false, Error = "The verdict is late or unknown: the delivery was answered already, has expired, or does not belong to this module." };
            }

            if (request.Decision != Decision.Allow && request.Decision != Decision.Deny)
            {
                return new VerdictReply { Ok = false, Error = "The decision must be ALLOW or DENY." };
            }

            var reply = new VerdictReply();
            List<EmittedEvent> accepted;
            List<int> originalPositions;
            PrepareEmitted(connection, request.Emitted, reply.Refusals, out accepted, out originalPositions);

            Verdict verdict;
            if (request.Decision == Decision.Deny) { verdict = Verdict.Deny(request.Message, accepted); }
            else if (request.Handled && string.Equals(found.Delivery.Hook, BehaviorHooks.CommandEntered, StringComparison.Ordinal)) { verdict = Verdict.Handled(string.IsNullOrEmpty(request.Message) ? null : request.Message, accepted); }
            else { verdict = Verdict.Allow(accepted); }

            VerdictAcknowledgement acknowledgement = found.Delivery.Reply(verdict);
            if (!acknowledgement.Counted)
            {
                DroppedVerdicts++;
                return new VerdictReply { Ok = false, Error = "The verdict is late: " + acknowledgement.NotCountedReason + "." };
            }

            for (int index = 0; index < acknowledgement.RefusedEvents.Count; index++)
            {
                int position = acknowledgement.RefusedPositions != null && index < acknowledgement.RefusedPositions.Count
                    ? originalPositions[acknowledgement.RefusedPositions[index]] : 0;
                reply.Refusals.Add(new EmitRefusal { Index = (uint)position, EventName = NameOf(request.Emitted, position), Reason = acknowledgement.RefusedEvents[index] });
            }
            reply.Ok = true;
            return reply;
        }

        private static string NameOf(IList<EventRecord> records, int position)
        {
            return position >= 0 && position < records.Count ? records[position].Name : string.Empty;
        }

        // ---- emit ----

        /// <summary>Game thread. Applies events a module emits outside a verdict (After-phase work, timer-driven work). Each refused event is listed; the rest apply (D61).</summary>
        public EmitReply Emit(ModuleConnection connection, EmitRequest request)
        {
            var reply = new EmitReply();
            List<EmittedEvent> accepted;
            List<int> originalPositions;
            PrepareEmitted(connection, request.Events, reply.Refusals, out accepted, out originalPositions);
            if (accepted.Count == 0) { return reply; }

            // Events of the Emit call are root events: the cause chain does not continue across the call. The per-module quota bounds the loop.
            string anchor = accepted[0].Parties.Count > 0 ? accepted[0].Parties[0].Entity.Id : string.Empty;
            var origin = new EventOrigin(connection.ModuleName, "emit", anchor);
            List<string> refused;
            List<int> refusedPositions;
            _events.ApplyEmittedFrom(null, origin, accepted, out refused, out refusedPositions);
            if (refused != null)
            {
                for (int index = 0; index < refused.Count; index++)
                {
                    int position = originalPositions[refusedPositions[index]];
                    reply.Refusals.Add(new EmitRefusal { Index = (uint)position, EventName = NameOf(request.Events, position), Reason = refused[index] });
                }
            }
            return reply;
        }

        // Checks each record against the module's capabilities, its emission quota, its component prefix and its targets. Records that pass
        // are returned as events; the others become refusals with their position in the submitted list (D61).
        private void PrepareEmitted(ModuleConnection connection, IList<EventRecord> records, ICollection<EmitRefusal> refusals, out List<EmittedEvent> accepted, out List<int> originalPositions)
        {
            accepted = new List<EmittedEvent>();
            originalPositions = new List<int>();
            for (int index = 0; index < records.Count; index++)
            {
                EventRecord record = records[index];
                EmittedEvent prepared;
                string refusal = Prepare(connection, record, out prepared);
                if (refusal != null)
                {
                    _log("Module '" + connection.ModuleName + "' emitted event '" + record.Name + "' was refused: " + refusal);
                    refusals.Add(new EmitRefusal { Index = (uint)index, EventName = record.Name, Reason = refusal });
                    continue;
                }
                accepted.Add(prepared);
                originalPositions.Add(index);
            }
        }

        private string Prepare(ModuleConnection connection, EventRecord record, out EmittedEvent prepared)
        {
            prepared = null;
            string name = record.Name;
            if (string.IsNullOrEmpty(name)) { return "the event has no name"; }

            // D65: emit:<event name>. A missing entry refuses the event with an error that names the capability.
            string capability = "emit:" + name;
            if (!connection.HasCapability(capability)) { return "missing capability '" + capability + "' (the grants file does not allow it for module '" + connection.ModuleName + "')"; }

            lock (_lock)
            {
                TokenBucket bucket;
                if (!_emitBuckets.TryGetValue(connection.ModuleName, out bucket))
                {
                    bucket = new TokenBucket(_options.EmitPerSecond, _options.EmitBurst);
                    _emitBuckets[connection.ModuleName] = bucket;
                }
                if (!bucket.TryTake(_clock)) { return "emission quota exceeded (" + _options.EmitPerSecond + " per second, burst " + _options.EmitBurst + ")"; }
            }

            Dictionary<string, object> payload = StructConversion.ToDictionary(record.Payload);

            var parties = new List<EventParty>(record.Targets.Count);
            for (int index = 0; index < record.Targets.Count; index++)
            {
                GameObject target = _findEntity(record.Targets[index]);
                if (target == null) { return "target entity not found: " + record.Targets[index]; }
                parties.Add(new EventParty(RoleFor(name, index), target));
            }

            string problem = ModuleEmitActions.Validate(name, connection.ModuleName, payload, parties);
            if (problem != null) { return problem; }

            prepared = new EmittedEvent(name, parties, payload);
            return null;
        }

        // entity.move takes the mover first and the destination room second (the roles World's action reads); every other event names its targets "target".
        private static string RoleFor(string eventName, int index)
        {
            if (string.Equals(eventName, EventNames.EntityMove, StringComparison.Ordinal)) { return index == 0 ? "mover" : index == 1 ? "to" : "target"; }
            return "target";
        }

        // ---- state (D12, D73) ----

        /// <summary>Replaces the module's document under the key. Null on success, otherwise the reason.</summary>
        public string SetState(ModuleConnection connection, string key, Struct document)
        {
            return _state.Set(connection.ModuleName, key, StructConversion.ToBson(document));
        }

        public bool TryGetState(ModuleConnection connection, string key, out Struct document)
        {
            document = null;
            BsonDocument stored;
            if (!_state.TryGet(connection.ModuleName, key, out stored)) { return false; }
            document = StructConversion.FromBson(stored);
            return true;
        }

        // ---- timers (D11, D74) ----

        /// <summary>Schedules or replaces a timer of the connection. Null on success, otherwise the reason. Callable from any thread.</summary>
        public string ScheduleTimer(ModuleConnection connection, string timerId, long atUnixMilliseconds, Struct payload)
        {
            if (string.IsNullOrWhiteSpace(timerId)) { return "A timer needs an id."; }
            DateTime now = _clock.Now;
            long nowUnix = new DateTimeOffset(now).ToUnixTimeMilliseconds();
            TimeSpan delay = TimeSpan.FromMilliseconds(Math.Max(0, atUnixMilliseconds - nowUnix));
            if (delay < _options.TimerMinimumDelay) { delay = _options.TimerMinimumDelay; }

            lock (_lock)
            {
                Dictionary<string, TimerEntry> held;
                if (!_timers.TryGetValue(connection.ConnectionId, out held))
                {
                    held = new Dictionary<string, TimerEntry>(StringComparer.Ordinal);
                    _timers[connection.ConnectionId] = held;
                }
                if (!held.ContainsKey(timerId) && held.Count >= _options.TimersPerModule)
                {
                    return "The module already holds " + held.Count + " timers; the limit is " + _options.TimersPerModule + ".";
                }
                held[timerId] = new TimerEntry { Id = timerId, Due = now + delay, Payload = payload ?? new Struct() };
            }
            return null;
        }

        /// <summary>Removes a timer. True when it existed.</summary>
        public bool CancelTimer(ModuleConnection connection, string timerId)
        {
            lock (_lock)
            {
                Dictionary<string, TimerEntry> held;
                return timerId != null && _timers.TryGetValue(connection.ConnectionId, out held) && held.Remove(timerId);
            }
        }

        /// <summary>
        /// Game thread, once per tick: sends timer.fired to the module that scheduled each due timer (D59, D74) and forgets verdicts nobody
        /// answered in time. A timer.fired that cannot be queued (full queue, closed stream) is dropped and logged; timers are not kept (D74).
        /// </summary>
        public void Tick()
        {
            DateTime now = _clock.Now;
            List<KeyValuePair<ModuleConnection, TimerEntry>> due = null;
            lock (_lock)
            {
                if (_pending.Count > 0)
                {
                    List<string> expired = null;
                    foreach (KeyValuePair<string, Pending> pair in _pending)
                    {
                        if (now >= pair.Value.ForgetAfter)
                        {
                            if (expired == null) { expired = new List<string>(); }
                            expired.Add(pair.Key);
                        }
                    }
                    if (expired != null) { foreach (string key in expired) { _pending.Remove(key); } }
                }

                foreach (KeyValuePair<string, Dictionary<string, TimerEntry>> held in _timers)
                {
                    if (held.Value.Count == 0) { continue; }
                    List<string> fired = null;
                    foreach (KeyValuePair<string, TimerEntry> pair in held.Value)
                    {
                        if (pair.Value.Due > now) { continue; }
                        if (fired == null) { fired = new List<string>(); }
                        fired.Add(pair.Key);
                    }
                    if (fired == null) { continue; }
                    ModuleConnection connection = _registry.Find(held.Key);
                    foreach (string id in fired)
                    {
                        TimerEntry entry = held.Value[id];
                        held.Value.Remove(id);
                        if (connection == null) { continue; }
                        if (due == null) { due = new List<KeyValuePair<ModuleConnection, TimerEntry>>(); }
                        due.Add(new KeyValuePair<ModuleConnection, TimerEntry>(connection, entry));
                    }
                }
            }
            if (due == null) { return; }

            foreach (KeyValuePair<ModuleConnection, TimerEntry> item in due.OrderBy(pair => pair.Value.Due))
            {
                var payload = new Struct();
                payload.Fields["timer_id"] = Value.ForString(item.Value.Id);
                payload.Fields["payload"] = Value.ForStruct(item.Value.Payload);
                var message = new CoreEvent { EventId = "t" + (++_timerCounter), Name = "timer.fired", Phase = Phase.After, Payload = payload };
                if (!item.Key.TryEnqueue(message))
                {
                    DroppedDeliveries++;
                    _log("timer.fired '" + item.Value.Id + "' for module '" + item.Key.ModuleName + "' dropped: its queue is full or its stream has ended.");
                }
            }
        }
    }

    /// <summary>Asks the in-process registry first, then the connected modules (owner decision D49: one contract, two adapters, same lookup).</summary>
    public sealed class CompositeEventHandlerResolver : IEventHandlerResolver
    {
        private readonly IEventHandlerResolver _first;
        private readonly IEventHandlerResolver _second;

        public CompositeEventHandlerResolver(IEventHandlerResolver first, IEventHandlerResolver second)
        {
            _first = first ?? throw new ArgumentNullException(nameof(first));
            _second = second ?? throw new ArgumentNullException(nameof(second));
        }

        public bool TryResolve(string module, string type, out IEventHandler handler)
        {
            return _first.TryResolve(module, type, out handler) || _second.TryResolve(module, type, out handler);
        }
    }
}
