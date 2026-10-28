using System;
using System.Collections.Concurrent;
using MUD.Core.GameObjects;

namespace MUD.Core.Components
{
    // Resolves a behavior entry (Module, Type) to code that ships inside core (owner decisions D5, D49): the entity
    // stores only the reference, and the code is looked up here at call time. Only the heartbeat hook is wired up
    // so far, and only through Living.DoHeartbeat; the event bus that dispatches every hook comes in phase 3.
    public class InProcessBehaviorRegistry
    {
        // The registry that core-shipped behaviors are registered in.
        public static readonly InProcessBehaviorRegistry Shared = CreateShared();

        private readonly ConcurrentDictionary<string, Action<Living, BehaviorRef>> _heartbeatHandlers =
            new ConcurrentDictionary<string, Action<Living, BehaviorRef>>(StringComparer.Ordinal);

        public void RegisterHeartbeat(string module, string type, Action<Living, BehaviorRef> handler)
        {
            if (handler == null) { throw new ArgumentNullException(nameof(handler)); }
            if (!_heartbeatHandlers.TryAdd(Key(module, type), handler))
            {
                throw new InvalidOperationException("A heartbeat handler for " + module + " " + type + " is already registered.");
            }
        }

        // False when nothing is registered for the entry: the entry is dormant and is skipped (owner decisions D10, D60).
        public bool TryGetHeartbeat(string module, string type, out Action<Living, BehaviorRef> handler)
        {
            return _heartbeatHandlers.TryGetValue(Key(module, type), out handler);
        }

        private static string Key(string module, string type)
        {
            return module + "\n" + type;
        }

        private static InProcessBehaviorRegistry CreateShared()
        {
            var registry = new InProcessBehaviorRegistry();
            registry.RegisterHeartbeat(BleedingStatus.Module, BleedingStatus.Type, (living, entry) => BleedingStatus.OnHeartbeat(living));
            return registry;
        }
    }
}
