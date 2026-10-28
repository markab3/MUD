using System;
using System.Collections.Generic;
using MUD.Core.GameObjects;

namespace MUD.Core.Events
{
    // Whether an event runs before the action commits or after it did. A field of the event, not a separate type
    // (owner decision D4). Only After is processed so far; the Before chain is the next card (P3-2).
    public enum EventPhase
    {
        Before,
        After
    }

    // One entity that takes part in an event, with the role it plays (for example "mover", "from", "to") and the
    // hook names under which its behavior entries are consulted for this event (owner decision D54). Hooks is
    // normally a shared static array, so building a party allocates nothing beyond the party itself.
    public sealed class EventParty
    {
        public static readonly string[] NoHooks = new string[0];

        public string Role { get; }

        public GameObject Entity { get; }

        public string[] Hooks { get; }

        public EventParty(string role, GameObject entity, string[] hooks = null)
        {
            if (string.IsNullOrEmpty(role)) { throw new ArgumentException("A party needs a role.", nameof(role)); }
            if (entity == null) { throw new ArgumentNullException(nameof(entity)); }
            Role = role;
            Entity = entity;
            Hooks = hooks ?? NoHooks;
        }

        public override string ToString()
        {
            return Role + ":" + Entity.Id;
        }
    }

    // The behavior entry that was running when it emitted an event; the re-entrancy guard compares these (D9).
    public sealed class EventOrigin
    {
        public string Module { get; }

        public string Type { get; }

        public string EntityId { get; }

        public EventOrigin(string module, string type, string entityId)
        {
            Module = module;
            Type = type;
            EntityId = entityId;
        }

        public bool Matches(EventOrigin other)
        {
            return other != null
                && string.Equals(Module, other.Module, StringComparison.Ordinal)
                && string.Equals(Type, other.Type, StringComparison.Ordinal)
                && string.Equals(EntityId, other.EntityId, StringComparison.Ordinal);
        }

        public override string ToString()
        {
            return Module + "/" + Type + "@" + EntityId;
        }
    }

    // A generic string-named event record (owner decisions D4, D52). The payload is one generic map for every event
    // (values are strings, numbers, booleans, nested maps or lists); there are no typed payloads in version 1.
    // Created only through EventDispatcher, which assigns the ids and the cause chain.
    public sealed class GameEvent
    {
        private static readonly IReadOnlyDictionary<string, object> EmptyPayload = new Dictionary<string, object>();

        public string Name { get; }

        public EventPhase Phase { get; }

        // Assigned by core, unique for the life of the process.
        public long EventId { get; }

        // The same for every event in one cascade; a root event's trace id is its own event id.
        public string TraceId { get; }

        // The event whose processing produced this one; 0 for events that come from a player, a timer or the loop.
        public long CauseId { get; }

        // The event whose processing produced this one, or null. Kept so the re-entrancy guard can walk the chain.
        public GameEvent Cause { get; }

        // 0 for a root event, one more than the cause for an emitted event.
        public int Depth { get; }

        // The behavior entry that emitted this event; null for events core itself raised.
        public EventOrigin Origin { get; }

        public IReadOnlyList<EventParty> Parties { get; }

        public IReadOnlyDictionary<string, object> Payload { get; }

        internal GameEvent(string name, EventPhase phase, long eventId, string traceId, GameEvent cause, EventOrigin origin,
            IReadOnlyList<EventParty> parties, IReadOnlyDictionary<string, object> payload)
        {
            Name = name;
            Phase = phase;
            EventId = eventId;
            TraceId = traceId;
            Cause = cause;
            CauseId = cause == null ? 0 : cause.EventId;
            Depth = cause == null ? 0 : cause.Depth + 1;
            Origin = origin;
            Parties = parties ?? new EventParty[0];
            Payload = payload ?? EmptyPayload;
        }

        public EventParty FindParty(string role)
        {
            for (int index = 0; index < Parties.Count; index++)
            {
                if (string.Equals(Parties[index].Role, role, StringComparison.Ordinal)) { return Parties[index]; }
            }
            return null;
        }

        public override string ToString()
        {
            return Name + " #" + EventId + " (" + Phase + ", trace " + TraceId + ", cause " + CauseId + ")";
        }
    }
}
