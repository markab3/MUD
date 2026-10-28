using System;
using System.Linq;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MUD.Core.Components;
using MUD.Core.GameObjects;
using MUD.Core.Components;

namespace MUD.Core.Commands
{
    // Lets a creator list, add and remove the behavior entries of a room, item, player or NPC (owner decisions D5,
    // D53, D55: no authorisation check on which module name is attached). Entries are stored only; nothing runs them yet.
    public class BehaviorCommand : ICommand
    {
        private const string Usage =
            "Syntax:\r\n" +
            "  behavior <target> list\r\n" +
            "  behavior <target> add <hook> <module> <type> [json]\r\n" +
            "  behavior <target> remove <hook> <module> <type>";

        // <target words> <action> <rest>. The target may be several words ("rusty sword").
        private static readonly Regex Pattern = new Regex(@"^(?<target>.+?)\s+(?<action>list|add|remove)(?:\s+(?<rest>.*))?$", RegexOptions.Singleline);

        public string[] CommandKeywords { get => new string[] { "behavior" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Creator; }

        public string HelpText
        {
            get => "List, add or remove behavior entries on a room, item, player or NPC. Entries are only stored for now; nothing runs them yet.\r\n \r\n" + Usage + "\r\n \r\n" +
                "The target is 'here' (this room), 'me' (yourself), or the name of an item or living in your inventory or this room. Features cannot carry behaviors.\r\n" +
                "Hooks: " + string.Join(", ", BehaviorHooks.All) + ".\r\n" +
                "The optional json is the entry's config, a JSON object such as {\"damage\": 5}. An entry with the same hook, module and type as an existing one is refused; remove it first.";
        }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return new object[] { this.StripKeyword(input) ?? "" };
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            Roles creatorStatusObj = commandIssuer.Roles.Creator ? commandIssuer.Roles : null;
            if (creatorStatusObj == null)
            {
                commandIssuer.ReceiveMessage("This is a creator only command. How did you get this?!");
                return;
            }

            var args = ((commandArgs != null && commandArgs.Length > 0 ? commandArgs[0] as string : null) ?? "").Trim();
            var match = Pattern.Match(args);
            if (!match.Success) { commandIssuer.ReceiveMessage(Usage); return; }

            string targetText = match.Groups["target"].Value.Trim();
            string action = match.Groups["action"].Value;
            string rest = match.Groups["rest"].Success ? match.Groups["rest"].Value.Trim() : "";

            var target = ResolveTarget(commandIssuer, targetText, out string targetProblem);
            if (target == null) { commandIssuer.ReceiveMessage(targetProblem); return; }

            switch (action)
            {
                case "list":
                    if (rest.Length != 0) { commandIssuer.ReceiveMessage(Usage); return; }
                    List(commandIssuer, target);
                    break;
                case "add":
                    Add(commandIssuer, target, rest);
                    break;
                default:
                    Remove(commandIssuer, target, rest);
                    break;
            }
        }

        private static GameObject ResolveTarget(Living issuer, string targetText, out string problem)
        {
            problem = null;
            if (targetText == "me") { return issuer; }
            if (issuer.CurrentLocation == null) { problem = "You are not in a room."; return null; }
            if (targetText == "here") { return issuer.CurrentLocation; }

            var matches = issuer.MatchObjects(targetText, true, true, true, false);
            if (matches.Length == 0) { problem = "You cannot find '" + targetText + "'. Features cannot carry behaviors."; return null; }
            if (matches.Length > 1) { problem = "'" + targetText + "' matches more than one thing; be more specific."; return null; }
            if (!(matches[0] is IBehaviorHolder)) { problem = "That cannot carry behaviors."; return null; }
            return matches[0];
        }

        private static void List(Living issuer, GameObject target)
        {
            var holder = (IBehaviorHolder)target;
            if (holder.Behaviors == null || holder.Behaviors.Count == 0)
            {
                issuer.ReceiveMessage("No behavior entries on " + Describe(target) + ".");
                return;
            }
            issuer.ReceiveMessage(holder.Behaviors.Count + " behavior entr" + (holder.Behaviors.Count == 1 ? "y" : "ies") + " on " + Describe(target) + ":");
            foreach (var entry in holder.Behaviors)
            {
                issuer.ReceiveMessage(string.Format("  {0} {1} {2} priority {3} schema {4} config {5}",
                    entry.Hook, entry.Module, entry.Type, entry.Priority, entry.SchemaVersion, entry.Config.ToJson()));
            }
        }

        private static void Add(Living issuer, GameObject target, string rest)
        {
            var words = Regex.Match(rest, @"^(?<hook>\S+)\s+(?<module>\S+)\s+(?<type>\S+)(?:\s+(?<json>.*))?$", RegexOptions.Singleline);
            if (!words.Success) { issuer.ReceiveMessage(Usage); return; }

            var config = new BsonDocument();
            string json = words.Groups["json"].Success ? words.Groups["json"].Value.Trim() : "";
            if (json.Length > 0)
            {
                try { config = BsonDocument.Parse(json); }
                catch (Exception)
                {
                    issuer.ReceiveMessage("The config is not a valid JSON object: " + json);
                    return;
                }
            }

            var entry = new BehaviorRef(words.Groups["hook"].Value, words.Groups["module"].Value, words.Groups["type"].Value, 0, config);
            var problem = ((IBehaviorHolder)target).AddBehavior(entry);
            if (problem != null) { issuer.ReceiveMessage("Not added: " + problem); return; }

            if (!Save(issuer, target)) { issuer.ReceiveMessage("Added to " + Describe(target) + ", but saving failed."); return; }
            issuer.ReceiveMessage("Added " + entry + " to " + Describe(target) + ".");
        }

        private static void Remove(Living issuer, GameObject target, string rest)
        {
            var words = rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length != 3) { issuer.ReceiveMessage(Usage); return; }
            if (!((IBehaviorHolder)target).RemoveBehavior(words[0], words[1], words[2]))
            {
                issuer.ReceiveMessage("No entry " + rest + " on " + Describe(target) + ".");
                return;
            }
            if (!Save(issuer, target)) { issuer.ReceiveMessage("Removed from " + Describe(target) + ", but saving failed."); return; }
            issuer.ReceiveMessage("Removed " + string.Join(" ", words) + " from " + Describe(target) + ".");
        }

        // A room saves itself; a player saves with the player record; an item is stored inside the creator's
        // inventory (saved with the creator) or inside the room's items (room state). NPCs are not persisted.
        private static bool Save(Living issuer, GameObject target)
        {
            var room = target as Room;
            if (room != null) { return room.Save(); }

            var player = target as Player;
            if (player != null) { return player.Save(true); }

            if (target is Living) { return true; }

            var item = target as InventoryItem;
            if (item != null)
            {
                if (issuer.Inventory != null && issuer.Inventory.Contains(item))
                {
                    var issuingPlayer = issuer as Player;
                    return issuingPlayer == null || issuingPlayer.Save(true);
                }
                if (issuer.CurrentLocation != null && issuer.CurrentLocation.Items.Contains(item)) { return issuer.CurrentLocation.SaveState(); }
            }
            return false;
        }

        private static string Describe(GameObject target)
        {
            if (target is Room room) { return "room " + room.Id; }
            return string.IsNullOrEmpty(target.ShortDescription) ? target.Id : target.ShortDescription;
        }
    }
}
