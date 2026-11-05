using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MUD.CoreApi;
using MUD.Core.Components;
using MUD.Core.Events;
using MUD.Core.GameObjects;

namespace MUD.Core.Modules
{
    /// <summary>
    /// The events a module may emit that core carries out (card P4-4), and the checks that go with them. Every other event name is
    /// accepted as a plain After-phase event: it is delivered to the hooks of its targets (none are named for it, so in practice it
    /// reaches observers and the log) and carries a module's own meaning, for example temple.trap_sprung (D65).
    ///
    /// Event names and payload fields below were chosen by card P4-4 and are flagged in its pull request, because the decisions fix the
    /// rules (D50, D55, D65, D72) but not the names.
    ///
    ///   entity.move       targets: mover (a living), destination (a room). payload: reason (text, optional).
    ///   component.set     target: one entity. payload: component ("&lt;module&gt;.&lt;name&gt;", the emitting module's own prefix, D50), data (map).
    ///   component.remove  target: one entity. payload: component.
    ///   message.send      targets: livings or rooms. payload: text (markup, D28), channel (say, tell, room, combat, system, chat; default system, D44).
    ///   feature.add       target: a room. payload: short, long, roomLine, detail (text), handles (list of text). D72.
    ///   feature.remove    target: a room. payload: short (the Feature is identified by its short text, any module may remove any Feature). D72.
    ///   behavior.add      target: a room, item, player or NPC. payload: hook, module, type, priority, config, schemaVersion. No capability beyond emit:behavior.add (D55).
    ///   behavior.remove   target: as above. payload: hook, module, type.
    /// </summary>
    public static class ModuleEmitActions
    {
        public const string ComponentSet = "component.set";
        public const string ComponentRemove = "component.remove";
        public const string MessageSend = "message.send";
        public const string FeatureAdd = "feature.add";
        public const string FeatureRemove = "feature.remove";
        public const string BehaviorAdd = "behavior.add";
        public const string BehaviorRemove = "behavior.remove";

        /// <summary>Registers the actions with the event dispatcher (entity.move is registered by the world itself). Safe to call once per dispatcher.</summary>
        public static void Register(EventDispatcher events)
        {
            events.RegisterAction(ComponentSet, request => RunComponentSet(request));
            events.RegisterAction(ComponentRemove, request => RunComponentRemove(request));
            events.RegisterAction(MessageSend, request => RunMessageSend(request));
            events.RegisterAction(FeatureAdd, request => RunFeatureAdd(request));
            events.RegisterAction(FeatureRemove, request => RunFeatureRemove(request));
            events.RegisterAction(BehaviorAdd, request => RunBehaviorAdd(request));
            events.RegisterAction(BehaviorRemove, request => RunBehaviorRemove(request));
        }

        /// <summary>Null when the emitted event is acceptable from this module, otherwise the reason it is refused (D61).</summary>
        public static string Validate(string name, string moduleName, IReadOnlyDictionary<string, object> payload, IReadOnlyList<EventParty> parties)
        {
            switch (name)
            {
                case EventNames.EntityMove:
                    if (parties.Count < 2 || !(parties[0].Entity is Living) || !(parties[1].Entity is Room)) { return "entity.move needs two targets: the mover (a living) and the destination (a room)"; }
                    return null;
                case ComponentSet:
                case ComponentRemove:
                    {
                        if (parties.Count != 1) { return name + " needs exactly one target"; }
                        string component = Text(payload, "component");
                        string owner, local;
                        if (!ComponentName.TryParseModuleName(component, out owner, out local)) { return "the component name must be '<module>.<name>'"; }
                        // D50: a module writes only components whose names start with its own module name.
                        if (!string.Equals(owner, moduleName, StringComparison.Ordinal)) { return "module '" + moduleName + "' may write only components named '" + moduleName + ".*', not '" + component + "'"; }
                        if (name == ComponentSet && !(Value(payload, "data") is IDictionary<string, object>)) { return "component.set needs a data map"; }
                        return null;
                    }
                case MessageSend:
                    if (parties.Count == 0) { return "message.send needs at least one target"; }
                    if (string.IsNullOrEmpty(Text(payload, "text"))) { return "message.send needs text"; }
                    if (!TryChannel(Text(payload, "channel"), out _)) { return "the channel must be one of say, tell, room, combat, system, chat"; }
                    foreach (EventParty party in parties)
                    {
                        if (!(party.Entity is Living) && !(party.Entity is Room)) { return "message.send targets must be livings or rooms"; }
                    }
                    return null;
                case FeatureAdd:
                case FeatureRemove:
                    if (parties.Count != 1 || !(parties[0].Entity is Room)) { return name + " needs exactly one target, a room"; }
                    if (string.IsNullOrWhiteSpace(Text(payload, "short"))) { return name + " needs the Feature's short text"; }
                    return null;
                case BehaviorAdd:
                case BehaviorRemove:
                    if (parties.Count != 1 || !(parties[0].Entity is IBehaviorHolder)) { return name + " needs exactly one target that can carry behaviors (room, item, player or NPC)"; }
                    if (string.IsNullOrEmpty(Text(payload, "hook")) || string.IsNullOrEmpty(Text(payload, "module")) || string.IsNullOrEmpty(Text(payload, "type"))) { return name + " needs hook, module and type"; }
                    return null;
                default:
                    return null;
            }
        }

        // ---- the actions ----

        private static bool RunComponentSet(EmittedEvent request)
        {
            GameObject target = request.Parties[0].Entity;
            BsonDocument data = StructConversion.ToBson(Value(request.Payload, "data"));
            if (data == null) { return false; }
            target.SetComponent(Text(request.Payload, "component"), data);
            Persist(target);
            return true;
        }

        private static bool RunComponentRemove(EmittedEvent request)
        {
            GameObject target = request.Parties[0].Entity;
            // Removing a component that is not there is a no-op, so the module's undo can be idempotent (D11).
            if (target.RemoveComponent(Text(request.Payload, "component"))) { Persist(target); }
            return true;
        }

        private static bool RunMessageSend(EmittedEvent request)
        {
            OutputChannel channel;
            TryChannel(Text(request.Payload, "channel"), out channel);
            string text = Text(request.Payload, "text");
            foreach (EventParty party in request.Parties)
            {
                var room = party.Entity as Room;
                if (room != null) { room.TellRoom(text, null, channel); continue; }
                var living = party.Entity as Living;
                if (living != null) { living.ReceiveMessage(text, channel); }
            }
            return true;
        }

        private static bool RunFeatureAdd(EmittedEvent request)
        {
            var room = (Room)request.Parties[0].Entity;
            string shortText = Text(request.Payload, "short");
            if (room.Features.Any(f => f != null && string.Equals(f.ShortDescription, shortText, StringComparison.OrdinalIgnoreCase))) { return false; }
            var feature = new Feature
            {
                Id = room.Id + "#" + shortText.ToLowerInvariant(),
                ShortDescription = shortText,
                LongDescription = Text(request.Payload, "long"),
                RoomLine = Text(request.Payload, "roomLine"),
                DetailDescription = Text(request.Payload, "detail"),
                Handles = TextList(request.Payload, "handles")
            };
            room.Features.Add(feature);
            room.Save();
            return true;
        }

        private static bool RunFeatureRemove(EmittedEvent request)
        {
            var room = (Room)request.Parties[0].Entity;
            string shortText = Text(request.Payload, "short");
            int removed = room.Features.RemoveAll(f => f != null && string.Equals(f.ShortDescription, shortText, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) { room.Save(); }
            return true;
        }

        private static bool RunBehaviorAdd(EmittedEvent request)
        {
            var holder = (IBehaviorHolder)request.Parties[0].Entity;
            BsonDocument config = StructConversion.ToBson(Value(request.Payload, "config")) ?? new BsonDocument();
            var entry = new BehaviorRef(Text(request.Payload, "hook"), Text(request.Payload, "module"), Text(request.Payload, "type"),
                (int)Number(request.Payload, "priority", 0), config, (int)Number(request.Payload, "schemaVersion", 1));
            if (holder.AddBehavior(entry) != null) { return false; }
            Persist((GameObject)holder);
            return true;
        }

        private static bool RunBehaviorRemove(EmittedEvent request)
        {
            var holder = (IBehaviorHolder)request.Parties[0].Entity;
            if (holder.RemoveBehavior(Text(request.Payload, "hook"), Text(request.Payload, "module"), Text(request.Payload, "type"))) { Persist((GameObject)holder); }
            return true;
        }

        // Saves what core saves for the entity today: a room with its definition and state, a player with the player record. NPCs and
        // items are not saved on their own (items are stored with their holder, as in the creator's behavior command).
        private static void Persist(GameObject target)
        {
            var room = target as Room;
            if (room != null) { room.Save(); return; }
            var player = target as Player;
            if (player != null) { player.Save(true); }
        }

        // ---- payload helpers ----

        private static object Value(IReadOnlyDictionary<string, object> payload, string key)
        {
            object found;
            return payload != null && payload.TryGetValue(key, out found) ? found : null;
        }

        private static string Text(IReadOnlyDictionary<string, object> payload, string key)
        {
            return Value(payload, key) as string;
        }

        private static double Number(IReadOnlyDictionary<string, object> payload, string key, double otherwise)
        {
            object found = Value(payload, key);
            return found is double ? (double)found : otherwise;
        }

        private static List<string> TextList(IReadOnlyDictionary<string, object> payload, string key)
        {
            var result = new List<string>();
            var list = Value(payload, key) as IEnumerable<object>;
            if (list != null) { foreach (object item in list) { if (item is string text && text.Length > 0) { result.Add(text); } } }
            return result;
        }

        private static bool TryChannel(string name, out OutputChannel channel)
        {
            channel = OutputChannel.System; // untagged messages default to system (D44)
            if (string.IsNullOrEmpty(name)) { return true; }
            switch (name)
            {
                case "say": channel = OutputChannel.Say; return true;
                case "tell": channel = OutputChannel.Tell; return true;
                case "room": channel = OutputChannel.Room; return true;
                case "combat": channel = OutputChannel.Combat; return true;
                case "system": channel = OutputChannel.System; return true;
                case "chat": channel = OutputChannel.Chat; return true;
                default: return false;
            }
        }
    }
}
