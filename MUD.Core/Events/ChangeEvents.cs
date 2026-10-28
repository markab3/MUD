using System.Collections.Generic;
using MongoDB.Bson;
using MUD.Core.Components;
using MUD.Core.GameObjects;

namespace MUD.Core.Events
{
    // The change events the phase 2 APIs emit when the world is edited. They are not hookable in version 1 (owner
    // decision D59): their parties carry no hook names. They are emitted only on the game thread that has a dispatcher
    // bound with at least one observer, and are otherwise skipped without allocating.
    public static class ChangeEvents
    {
        public const string ComponentSet = "component.set";
        public const string ComponentRemove = "component.remove";
        public const string BehaviorAdd = "behavior.add";
        public const string BehaviorRemove = "behavior.remove";

        public static void EmitComponentSet(GameObject target, string fullName, BsonDocument data)
        {
            EventDispatcher dispatcher = EventDispatcher.Current;
            if (dispatcher == null || !dispatcher.WantsChangeEvents) { return; }
            dispatcher.Emit(ComponentSet, EventPhase.After, Target(target), new Dictionary<string, object>
            {
                { "component", fullName },
                { "data", data.ToDictionary() }
            });
        }

        public static void EmitComponentRemove(GameObject target, string fullName)
        {
            EventDispatcher dispatcher = EventDispatcher.Current;
            if (dispatcher == null || !dispatcher.WantsChangeEvents) { return; }
            dispatcher.Emit(ComponentRemove, EventPhase.After, Target(target), new Dictionary<string, object>
            {
                { "component", fullName }
            });
        }

        public static void EmitBehaviorAdd(GameObject target, BehaviorRef entry)
        {
            EventDispatcher dispatcher = EventDispatcher.Current;
            if (dispatcher == null || !dispatcher.WantsChangeEvents) { return; }
            dispatcher.Emit(BehaviorAdd, EventPhase.After, Target(target), EntryPayload(entry));
        }

        public static void EmitBehaviorRemove(GameObject target, string hook, string module, string type)
        {
            EventDispatcher dispatcher = EventDispatcher.Current;
            if (dispatcher == null || !dispatcher.WantsChangeEvents) { return; }
            dispatcher.Emit(BehaviorRemove, EventPhase.After, Target(target), new Dictionary<string, object>
            {
                { "hook", hook },
                { "module", module },
                { "type", type }
            });
        }

        private static EventParty[] Target(GameObject target)
        {
            return new[] { new EventParty("target", target) };
        }

        private static Dictionary<string, object> EntryPayload(BehaviorRef entry)
        {
            return new Dictionary<string, object>
            {
                { "hook", entry.Hook },
                { "module", entry.Module },
                { "type", entry.Type },
                { "priority", entry.Priority },
                { "config", entry.Config.ToDictionary() },
                { "schemaVersion", entry.SchemaVersion }
            };
        }
    }
}
