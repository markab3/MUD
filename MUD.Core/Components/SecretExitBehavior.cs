using System;
using System.Collections.Generic;
using MongoDB.Bson;
using MUD.Core.Commands;
using MUD.Core.Events;
using MUD.Core.GameObjects;

namespace MUD.Core.Components
{
    // A hidden control in a room that opens a timed secret exit (secretexit, SecretExit). It is a room-level entry on two
    // hooks, with the same Config:
    //  - command.entered takes three kinds of line, each only when it matches the Config exactly (anything else is answered
    //    "not handled", so core's built-in commands get the line and nothing leaks that the control exists, D63/D64):
    //      "search <search_target>"  finds the control (per player, after the same delay as core's search);
    //      "<verb> <noun>"           for a player who found it: opens the exit for open_for_s seconds.
    //  - heartbeat closes an opened exit when its time is up (so the room needs both entries; closing is at most one
    //    heartbeat late).
    // Config (BSON, all optional except "to"): search_target, verb ("press"), noun ("button"), dir ("down"), to (room key),
    // open_for_s (60), and the texts found_msg, already_found_msg, press_msg, open_msg, already_open_msg, close_msg,
    // close_dest_msg. The exit is a runtime-only exit of the room (Room.OpenTemporaryExit): it is never stored, so a restart
    // always starts closed. Discovery is per player and is the component "secretexit.found" { keys: [ "<room key>|<noun>" ] }
    // on the player, saved with the player, so it survives relog and restart and is not touched by an area re-import.
    public sealed class SecretExitBehavior : IEventHandler, IBeforeHandler
    {
        public const string Module = "secretexit";
        public const string Type = "SecretExit";
        public const string ComponentFullName = "secretexit.found";
        public const string KeysField = "keys";

        public const int DefaultOpenForSeconds = 60;

        private static string Key(string roomId, string noun)
        {
            return roomId + "|" + (noun ?? string.Empty).Trim().ToLowerInvariant();
        }

        // True when the player has found the control with the given noun in the given room.
        public static bool HasFound(GameObject player, string roomId, string noun)
        {
            BsonDocument data;
            if (player == null || !player.TryGetComponent(ComponentFullName, out data)) { return false; }
            BsonValue keys = data.GetValue(KeysField, BsonNull.Value);
            if (!keys.IsBsonArray) { return false; }
            string wanted = Key(roomId, noun);
            foreach (BsonValue key in keys.AsBsonArray)
            {
                if (key.IsString && key.AsString == wanted) { return true; }
            }
            return false;
        }

        // Records the discovery in the player's component (the caller saves the player).
        public static void MarkFound(GameObject player, string roomId, string noun)
        {
            if (HasFound(player, roomId, noun)) { return; }
            BsonDocument data;
            BsonArray keys = null;
            if (player.TryGetComponent(ComponentFullName, out data))
            {
                BsonValue existing = data.GetValue(KeysField, BsonNull.Value);
                if (existing.IsBsonArray) { keys = existing.AsBsonArray.DeepClone().AsBsonArray; }
            }
            if (keys == null) { keys = new BsonArray(); }
            keys.Add(Key(roomId, noun));
            player.SetComponent(ComponentFullName, new BsonDocument(KeysField, keys));
        }

        public void Handle(EventDelivery delivery)
        {
            if (!string.Equals(delivery.Hook, BehaviorHooks.Heartbeat, StringComparison.Ordinal)) { return; }
            var room = delivery.Party.Entity as Room;
            if (room != null) { room.ExpireTemporaryExits(delivery.Clock.Now); }
        }

        public void Begin(BeforeDelivery delivery)
        {
            delivery.Reply(Answer(delivery));
        }

        private static string Text(BsonDocument config, string name, string fallback)
        {
            BsonValue value = config.GetValue(name, BsonNull.Value);
            return value.IsString && value.AsString.Length > 0 ? value.AsString : fallback;
        }

        private Verdict Answer(BeforeDelivery delivery)
        {
            var room = delivery.Party.Entity as Room;
            if (room == null || !string.Equals(delivery.Hook, BehaviorHooks.CommandEntered, StringComparison.Ordinal)) { return Verdict.Allow(); }

            Living actor = null;
            foreach (EventParty party in delivery.Event.Parties)
            {
                if (party.Role == "actor") { actor = party.Entity as Living; break; }
            }
            if (actor == null || !ReferenceEquals(actor.CurrentLocation, room)) { return Verdict.Allow(); }

            object lineValue = null;
            string line = delivery.Event.Payload != null && delivery.Event.Payload.TryGetValue("line", out lineValue) ? lineValue as string : null;
            if (string.IsNullOrWhiteSpace(line)) { return Verdict.Allow(); }
            line = line.Trim();
            int space = line.IndexOf(' ');
            string verb = space < 0 ? line : line.Substring(0, space);
            string rest = space < 0 ? string.Empty : line.Substring(space + 1).Trim();

            BsonDocument config = delivery.Entry.Config;
            string noun = Text(config, "noun", "button");
            string searchTarget = Text(config, "search_target", null);
            string pressVerb = Text(config, "verb", "press");

            if (searchTarget != null && Same(verb, "search") && Same(rest, searchTarget))
            {
                return Search(delivery, room, actor, config, noun);
            }
            if (Same(verb, pressVerb) && Same(rest, noun) && HasFound(actor, room.Id, noun))
            {
                return Press(delivery, room, actor, config, noun);
            }
            return Verdict.Allow();
        }

        private static bool Same(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        // "search <target>": the same feel as core's search (busy for SearchCommand.SearchDuration, result afterwards,
        // "stop" cancels), but the result is the discovery, so it is recorded on the player.
        private static Verdict Search(BeforeDelivery delivery, Room room, Living actor, BsonDocument config, string noun)
        {
            var player = actor as Player;
            if (player == null) { return Verdict.Allow(); }
            if (HasFound(player, room.Id, noun))
            {
                return Verdict.Handled(Text(config, "already_found_msg", "You have already found a hidden " + noun + " here."));
            }

            string foundMessage = Text(config, "found_msg", "You search the " + Text(config, "search_target", "area") + " and find a hidden " + noun + "!");
            actor.ReceiveMessage("You begin your search.");
            player.DelayThen(SearchCommand.SearchDuration, () =>
            {
                // A player who is no longer in the room, or who found it meanwhile, finds nothing new.
                if (!ReferenceEquals(player.CurrentLocation, room) || HasFound(player, room.Id, noun))
                {
                    player.ReceiveMessage("You finish your search.");
                    return;
                }
                MarkFound(player, room.Id, noun);
                player.ReceiveMessage(foundMessage);
                room.TellRoom(string.Format("{0} searches around a bit.", player.ShortDescription), new Living[] { player });
                player.Save(true);
            });
            return Verdict.Handled();
        }

        // "<verb> <noun>" for a player who found it: opens the exit for open_for_s seconds.
        private static Verdict Press(BeforeDelivery delivery, Room room, Living actor, BsonDocument config, string noun)
        {
            string dir = Text(config, "dir", "down");
            string to = Text(config, "to", null);
            if (to == null) { return Verdict.Allow(); }

            if (room.HasTemporaryExit(dir) || room.Exits.Exists(e => e != null && Same(e.Name, dir)))
            {
                return Verdict.Handled(Text(config, "already_open_msg", "The way " + dir + " is already open."));
            }
            Room destination = actor.FindRoom(to);
            if (destination == null) { return Verdict.Handled("You press the " + noun + ", but nothing happens."); }

            double seconds = DefaultOpenForSeconds;
            BsonValue configured = config.GetValue("open_for_s", BsonNull.Value);
            if (configured.IsNumeric && configured.ToDouble() > 0) { seconds = configured.ToDouble(); }

            string closeMessage = Text(config, "close_msg", "The way " + dir + " grinds shut again.");
            string closeDestinationMessage = Text(config, "close_dest_msg", "Far away, the passage grinds shut.");
            bool opened = room.OpenTemporaryExit(new Exit { Name = dir, DestinationId = to }, delivery.Clock.Now.AddSeconds(seconds), (r, exit) =>
            {
                r.TellRoom(closeMessage);
                destination.TellRoom(closeDestinationMessage);
            });
            if (!opened) { return Verdict.Handled(Text(config, "already_open_msg", "The way " + dir + " is already open.")); }

            actor.ReceiveMessage(Text(config, "press_msg", "You press the " + noun + "."));
            room.TellRoom(Text(config, "open_msg", "A hidden passage opens, leading " + dir + "."));
            return Verdict.Handled();
        }
    }
}
