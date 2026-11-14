using System;
using System.Collections.Concurrent;
using MUD.Core.Events;
using MUD.Core.GameObjects;

namespace MUD.Core.Components
{
    // The (Module, Type) dictionary for behaviors whose code ships inside core (owner decisions D5, D49): the entity stores
    // only the reference, and the handler is looked up here at call time. It is the resolver the event dispatcher uses by
    // default. A (Module, Type) nothing is registered for is dormant: the entry stays on the entity, is skipped under the
    // hook's fail policy and is counted (D10, D60). In-process registration stays after phase 4 as the second adapter next
    // to out-of-process modules (D49); one contract, same lookup.
    public class InProcessBehaviorRegistry : IEventHandlerResolver
    {
        // The registry that core-shipped behaviors are registered in.
        public static readonly InProcessBehaviorRegistry Shared = CreateShared();

        private readonly ConcurrentDictionary<string, IEventHandler> _handlers =
            new ConcurrentDictionary<string, IEventHandler>(StringComparer.Ordinal);

        // Registers the handler for the (module, type) entries, whatever hook they are attached under. Registering the
        // same (module, type) twice is a programming error.
        public void Register(string module, string type, IEventHandler handler)
        {
            if (string.IsNullOrEmpty(module)) { throw new ArgumentException("A module name is required.", nameof(module)); }
            if (string.IsNullOrEmpty(type)) { throw new ArgumentException("A type name is required.", nameof(type)); }
            if (handler == null) { throw new ArgumentNullException(nameof(handler)); }
            if (!_handlers.TryAdd(Key(module, type), handler))
            {
                throw new InvalidOperationException("A handler for " + module + " " + type + " is already registered.");
            }
        }

        // Convenience for a handler that only acts on a living's heartbeat.
        public void RegisterHeartbeat(string module, string type, Action<Living, BehaviorRef> handler)
        {
            if (handler == null) { throw new ArgumentNullException(nameof(handler)); }
            Register(module, type, new LivingHeartbeatHandler(handler));
        }

        // False when nothing is registered for the entry: the entry is dormant and is skipped (owner decisions D10, D60).
        public bool TryResolve(string module, string type, out IEventHandler handler)
        {
            if (module == null || type == null)
            {
                handler = null;
                return false;
            }
            return _handlers.TryGetValue(Key(module, type), out handler);
        }

        private static string Key(string module, string type)
        {
            return module + "\n" + type;
        }

        private sealed class LivingHeartbeatHandler : IEventHandler
        {
            private readonly Action<Living, BehaviorRef> _action;

            public LivingHeartbeatHandler(Action<Living, BehaviorRef> action) { _action = action; }

            public void Handle(EventDelivery delivery)
            {
                var living = delivery.Party.Entity as Living;
                if (living != null) { _action(living, delivery.Entry); }
            }
        }

        private static InProcessBehaviorRegistry CreateShared()
        {
            var registry = new InProcessBehaviorRegistry();
            registry.Register(BleedingStatus.Module, BleedingStatus.Type, new BleedingBehavior());
            return registry;
        }
    }
}
