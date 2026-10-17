using System;
using System.Collections.Generic;
using System.Linq;
using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    /// Implements `build feature list|add|set|remove` against the issuer's current room.
    /// Features are identified by their short description (case-insensitive), the same identity the area importer
    /// uses (specs/ROOM_FILES.md 4.4). Edits are saved to the room immediately. There is no locally-modified flag yet
    /// (spec section 9 is not implemented), so the interim rule 9.4 applies: re-importing the area file overwrites these edits
    /// unless they are first captured with `export`.
    public static class BuildFeatureEditor
    {
        public const string Usage =
            "Usage:\r\n" +
            "  build feature list\r\n" +
            "  build feature add <short description>\r\n" +
            "  build feature set <short description> = <field> <text>\r\n" +
            "  build feature remove <short description>\r\n" +
            "Fields: short, long, roomline, detail, handles, adjectives, plurals (the last three take a space separated list; roomline and detail with no text clear them).";

        public static void Execute(Living issuer, string args)
        {
            Room room = issuer.CurrentLocation;
            if (room == null)
            {
                issuer.ReceiveMessage("You are not in a room currently, so what are you editing?");
                return;
            }

            args = (args ?? "").Trim();
            string verb = args.Split(' ')[0].ToLowerInvariant();
            string rest = args.Length > verb.Length ? args.Substring(verb.Length).Trim() : "";

            switch (verb)
            {
                case "list": List(issuer, room); break;
                case "add": Add(issuer, room, rest); break;
                case "set": Set(issuer, room, rest); break;
                case "remove": Remove(issuer, room, rest); break;
                default: issuer.ReceiveMessage(Usage); break;
            }
        }

        private static Feature Find(Room room, string shortDescription)
        {
            return room.Features == null ? null : room.Features.FirstOrDefault(f => f != null && string.Equals(f.ShortDescription, shortDescription, StringComparison.OrdinalIgnoreCase));
        }

        private static void List(Living issuer, Room room)
        {
            if (room.Features == null || room.Features.Count == 0)
            {
                issuer.ReceiveMessage("This room has no features.");
                return;
            }
            var lines = new List<string>();
            foreach (var f in room.Features.Where(f => f != null))
            {
                lines.Add(string.Format("{0}\r\n  handles: {1}\r\n  adjectives: {2}\r\n  plurals: {3}\r\n  long: {4}\r\n  roomline: {5}\r\n  detail: {6}",
                    f.ShortDescription, Join(f.Handles), Join(f.Adjectives), Join(f.Plurals), f.LongDescription, f.RoomLine, f.DetailDescription));
            }
            issuer.ReceiveMessage(string.Join("\r\n", lines));
        }

        private static string Join(List<string> list)
        {
            return list == null ? "" : string.Join(" ", list);
        }

        private static void Add(Living issuer, Room room, string shortDescription)
        {
            if (shortDescription.Length == 0)
            {
                issuer.ReceiveMessage("Format: build feature add <short description>");
                return;
            }
            if (Find(room, shortDescription) != null)
            {
                issuer.ReceiveMessage("This room already has a feature called '" + shortDescription + "'.");
                return;
            }
            if (room.Features == null) { room.Features = new List<Feature>(); }

            // The last word of the short description is a sensible first handle; builders refine with `set`.
            string handle = shortDescription.Split(' ').Last().ToLowerInvariant();
            var feature = new Feature
            {
                Id = string.IsNullOrEmpty(room.Id) ? null : room.Id + "#" + shortDescription.ToLowerInvariant(),
                ShortDescription = shortDescription,
                LongDescription = shortDescription,
                Handles = new List<string> { handle },
                Adjectives = new List<string>(),
                Plurals = new List<string>()
            };
            room.Features.Add(feature);
            SaveAndReport(issuer, room, "Feature '" + shortDescription + "' added with handle '" + handle + "'. Use `build feature set` to describe it.");
        }

        private static void Set(Living issuer, Room room, string rest)
        {
            int eq = rest.IndexOf('=');
            if (eq < 0)
            {
                issuer.ReceiveMessage("Format: build feature set <short description> = <field> <text>");
                return;
            }
            string name = rest.Substring(0, eq).Trim();
            string assignment = rest.Substring(eq + 1).Trim();
            string field = assignment.Split(' ')[0].ToLowerInvariant();
            string text = assignment.Length > field.Length ? assignment.Substring(field.Length).Trim() : "";

            Feature feature = Find(room, name);
            if (feature == null)
            {
                issuer.ReceiveMessage("This room has no feature called '" + name + "'.");
                return;
            }

            var words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()).ToList();
            switch (field)
            {
                case "short":
                    if (text.Length == 0) { issuer.ReceiveMessage("A feature needs a short description."); return; }
                    if (!string.Equals(text, feature.ShortDescription, StringComparison.OrdinalIgnoreCase) && Find(room, text) != null)
                    {
                        issuer.ReceiveMessage("This room already has a feature called '" + text + "'.");
                        return;
                    }
                    feature.ShortDescription = text;
                    if (!string.IsNullOrEmpty(room.Id)) { feature.Id = room.Id + "#" + text.ToLowerInvariant(); }
                    break;
                case "long":
                    if (text.Length == 0) { issuer.ReceiveMessage("A feature needs a long description."); return; }
                    feature.LongDescription = text;
                    break;
                case "roomline": feature.RoomLine = text.Length == 0 ? null : text; break;
                case "detail": feature.DetailDescription = text.Length == 0 ? null : text; break;
                case "handles": feature.Handles = words; break;
                case "adjectives": feature.Adjectives = words; break;
                case "plurals": feature.Plurals = words; break;
                default:
                    issuer.ReceiveMessage("Unknown field '" + field + "'.\r\n" + Usage);
                    return;
            }
            SaveAndReport(issuer, room, "Feature '" + feature.ShortDescription + "' updated.");
        }

        private static void Remove(Living issuer, Room room, string shortDescription)
        {
            Feature feature = shortDescription.Length == 0 ? null : Find(room, shortDescription);
            if (feature == null)
            {
                issuer.ReceiveMessage("This room has no feature called '" + shortDescription + "'.");
                return;
            }
            room.Features.Remove(feature);
            SaveAndReport(issuer, room, "Feature '" + feature.ShortDescription + "' removed.");
        }

        private static void SaveAndReport(Living issuer, Room room, string message)
        {
            issuer.ReceiveMessage(room.Save() ? message : message + " (This room could not be saved.)");
        }
    }
}
