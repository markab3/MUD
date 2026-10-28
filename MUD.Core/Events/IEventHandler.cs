using System.Collections.Generic;
using MUD.Core.Components;

namespace MUD.Core.Events
{
    // Code that runs for a behavior entry. Handlers are found by (Module, Type) through IEventHandlerResolver at call
    // time (owner decisions D5, D49). Phase 3 has in-process handlers only. For an After event the handler's work is
    // what it does and what it emits; there is no verdict to give (D6, CORE_API 2.1).
    public interface IEventHandler
    {
        void Handle(EventDelivery delivery);
    }

    // Maps (Module, Type) to a handler. Returns false when nothing is registered, which makes the entry dormant (D10).
    // Core registers none yet: the in-process registry is card P3-3.
    public interface IEventHandlerResolver
    {
        bool TryResolve(string module, string type, out IEventHandler handler);
    }

    public sealed class NoEventHandlers : IEventHandlerResolver
    {
        public static readonly NoEventHandlers Instance = new NoEventHandlers();

        public bool TryResolve(string module, string type, out IEventHandler handler)
        {
            handler = null;
            return false;
        }
    }

    // One call of one handler: the event, the party whose entry matched, the entry (and so its Config, D57) and the
    // hook it matched under. Created only when a handler actually runs.
    public sealed class EventDelivery
    {
        private readonly EventDispatcher _dispatcher;

        public GameEvent Event { get; }

        public EventParty Party { get; }

        public BehaviorRef Entry { get; }

        public string Hook { get; }

        internal EventDelivery(EventDispatcher dispatcher, GameEvent gameEvent, EventParty party, BehaviorRef entry, string hook)
        {
            _dispatcher = dispatcher;
            Event = gameEvent;
            Party = party;
            Entry = entry;
            Hook = hook;
        }

        // Emits a new event caused by this delivery. It is queued, never run inside the handler. Returns false when core
        // refuses it (cascade depth or re-entrancy guard); the refusal is counted and logged.
        public bool Emit(string name, IReadOnlyList<EventParty> parties, IReadOnlyDictionary<string, object> payload = null)
        {
            return _dispatcher.Emit(name, EventPhase.After, parties, payload) != null;
        }
    }
}
