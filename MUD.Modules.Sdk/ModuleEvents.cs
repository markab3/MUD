using System.Collections.Generic;
using Google.Protobuf.WellKnownTypes;
using MUD.Contracts;

namespace MUD.Modules.Sdk
{
    /// <summary>
    /// Builds the generic event records a module sends to core (D40: one generic emit, with named helper methods that build the records).
    /// The event names and payload fields are the ones core carries out today (MUD.Core.Modules.ModuleEmitActions); every builder
    /// needs the matching emit:&lt;event name&gt; capability in the grants file (D65). Use these for the events of a Before verdict
    /// (BehaviorResult.WithEmitted) and ModuleContext for events sent on their own.
    /// </summary>
    public static class ModuleEvents
    {
        public const string Say = "say";
        public const string Tell = "tell";
        public const string Room = "room";
        public const string Combat = "combat";
        public const string System = "system";
        public const string Chat = "chat";

        /// <summary>Any event, with its targets and payload. The After phase is the default for emitted events.</summary>
        public static EventRecord Custom(string name, Struct payload, params string[] targets)
        {
            var record = new EventRecord { Name = name, Phase = Phase.After, Payload = payload ?? new Struct() };
            record.Targets.AddRange(targets);
            return record;
        }

        /// <summary>message.send: text (%^Color%^ markup) to livings or rooms. The channel is one of say, tell, room, combat, system, chat (D44); null means system.</summary>
        public static EventRecord Message(string target, string text, string channel = null)
        {
            return Messages(new[] { target }, text, channel);
        }

        public static EventRecord Messages(IEnumerable<string> targets, string text, string channel = null)
        {
            var payload = Payloads.Map(("text", text));
            if (!string.IsNullOrEmpty(channel)) { payload.Fields["channel"] = Value.ForString(channel); }
            return Custom("message.send", payload, new List<string>(targets).ToArray());
        }

        /// <summary>component.set: writes a component of this module on one entity. The name must start with the module's own name (D50).</summary>
        public static EventRecord SetComponent(string entityId, string component, Struct data)
        {
            return Custom("component.set", Payloads.Map(("component", component), ("data", data ?? new Struct())), entityId);
        }

        public static EventRecord RemoveComponent(string entityId, string component)
        {
            return Custom("component.remove", Payloads.Map(("component", component)), entityId);
        }

        /// <summary>feature.add: adds a Feature to a room (D72). Features are identified by their short text.</summary>
        public static EventRecord AddFeature(string roomId, string shortText, string longText = null, string roomLine = null, string detail = null, IEnumerable<string> handles = null)
        {
            Struct payload = Payloads.Map(("short", shortText));
            if (longText != null) { payload.Fields["long"] = Value.ForString(longText); }
            if (roomLine != null) { payload.Fields["roomLine"] = Value.ForString(roomLine); }
            if (detail != null) { payload.Fields["detail"] = Value.ForString(detail); }
            if (handles != null) { payload.Fields["handles"] = Payloads.ToValue(new List<string>(handles)); }
            return Custom("feature.add", payload, roomId);
        }

        /// <summary>feature.remove: removes a Feature from a room by its short text (D72).</summary>
        public static EventRecord RemoveFeature(string roomId, string shortText)
        {
            return Custom("feature.remove", Payloads.Map(("short", shortText)), roomId);
        }

        /// <summary>entity.move: moves a living to a room. A redirect is a Deny plus this event (D6).</summary>
        public static EventRecord MoveEntity(string moverId, string destinationRoomId, string reason = null)
        {
            Struct payload = reason == null ? new Struct() : Payloads.Map(("reason", reason));
            return Custom("entity.move", payload, moverId, destinationRoomId);
        }

        /// <summary>behavior.add: attaches a behavior entry to a room, item, player or NPC (D55: no further check).</summary>
        public static EventRecord AddBehavior(string entityId, string hook, string module, string type, int priority = 0, Struct config = null)
        {
            Struct payload = Payloads.Map(("hook", hook), ("module", module), ("type", type), ("priority", priority));
            if (config != null) { payload.Fields["config"] = Value.ForStruct(config); }
            return Custom("behavior.add", payload, entityId);
        }

        public static EventRecord RemoveBehavior(string entityId, string hook, string module, string type)
        {
            return Custom("behavior.remove", Payloads.Map(("hook", hook), ("module", module), ("type", type)), entityId);
        }
    }
}
