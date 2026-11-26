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
    //  - command.entered takes one kind of line, "<verb> <noun>" (default "press button"), and only for a player who has the
    //    discovery flag named by the Config "discovery". Any other line, and the line from a player without the flag, is answered
    //    "not handled", so core's commands get it and nothing leaks that the control exists (D63/D64). The flag is granted by the
    //    core "search" component (a search entry with a discovery, e.g. "search floor"), not by this behavior.
    //  - heartbeat closes an opened exit when its time is up (so the room needs both entries; closing is at most one
    //    heartbeat late).
    // Config (BSON): discovery (required: the id of the per-player flag, see DiscoveryFlags), to (required: room key), verb
    // ("press"), noun ("button"), dir ("down"), open_for_s (60), and the texts press_msg, open_msg, already_open_msg, close_msg,
    // close_dest_msg. The exit is a runtime-only exit of the room (Room.OpenTemporaryExit): it is never stored, so a restart
    // always starts closed. The discovery is player state (component "search.found"), so an area re-import never touches it.
    public sealed class SecretExitBehavior : IEventHandler, IBeforeHandler
    {
        public const string Module = "secretexit";
        public const string Type = "SecretExit";
        public const int DefaultOpenForSeconds = 60;

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
            string pressVerb = Text(config, "verb", "press");
            string discovery = Text(config, "discovery", null);
            if (discovery != null && Same(verb, pressVerb) && Same(rest, noun) && DiscoveryFlags.Has(actor, discovery))
            {
                return Press(delivery, room, actor, config, noun);
            }
            return Verdict.Allow();
        }

        private static bool Same(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
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
