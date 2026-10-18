using System;
using System.Linq;
using MUD.Core.GameObjects;
using MUD.Core.Properties.PlayerProperties;
using MUD.Core.Repositories;

namespace MUD.Core.Commands
{
    public class TransCommand : ICommand
    {
        private const string Usage = "Syntax: trans <player name> [<room key>]";

        public string[] CommandKeywords { get => new string[] { "trans" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Creator; }

        public string HelpText
        {
            get => "Transport an online player to a room.\r\n \r\n" + Usage + "\r\n \r\n" +
                "With only a player name, the player is brought to your current room. With a room key, the player is sent to that room.\r\n" +
                "The player must be online (names match exactly, ignoring case).\r\n" +
                "You can only move players of a strictly lower role than yours: admins may move creators and players, creators may move players only.\r\n" +
                "You cannot use trans on yourself; use navigate instead.";
        }

        private World _world;

        // Resolves a room key to a room. Defaults to the world; tests replace it so no database is needed.
        public Func<string, Room> RoomLookup { get; set; }

        public TransCommand(World world)
        {
            _world = world;
            RoomLookup = world.GetRoom;
        }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            var remainder = this.StripKeyword(input);
            if (string.IsNullOrWhiteSpace(remainder)) { return null; }

            var tokens = remainder.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > 2) { return null; }

            return tokens.Cast<object>().ToArray();
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            if (commandArgs == null || commandArgs.Length < 1 || commandArgs.Length > 2)
            {
                commandIssuer.ReceiveMessage(Usage);
                return;
            }

            int issuerRank = GetRank(commandIssuer);
            if (issuerRank < 1)
            {
                commandIssuer.ReceiveMessage("Only creators and admins can do that.");
                return;
            }

            string typedName = (string)commandArgs[0];
            string nameKey = PlayerAccounts.Normalize(typedName);
            Player target = _world.Players.FirstOrDefault(p => p.NameKey == nameKey);
            if (target == null)
            {
                commandIssuer.ReceiveMessage(string.Format("{0} is not online. Only online players can be moved.", typedName));
                return;
            }

            if (target == commandIssuer)
            {
                commandIssuer.ReceiveMessage("You cannot trans yourself. Use navigate to move yourself.");
                return;
            }

            if (GetRank(target) >= issuerRank)
            {
                commandIssuer.ReceiveMessage(string.Format("You cannot move {0}: you can only move players of a lower role than yours.", target.PlayerName));
                return;
            }

            // Resolve the destination before anything moves.
            Room destination;
            if (commandArgs.Length == 2)
            {
                string roomKey = (string)commandArgs[1];
                destination = RoomLookup(roomKey);
                if (destination == null)
                {
                    commandIssuer.ReceiveMessage(string.Format("There is no room with the key {0}. Nobody was moved.", roomKey));
                    return;
                }
            }
            else
            {
                destination = commandIssuer.CurrentLocation;
                if (destination == null)
                {
                    commandIssuer.ReceiveMessage("You are not in a room, so there is nowhere to bring them. Nobody was moved.");
                    return;
                }
            }

            if (target.CurrentLocation == destination)
            {
                commandIssuer.ReceiveMessage(string.Format("{0} is already there.", target.PlayerName));
                return;
            }

            target.ReceiveMessage(string.Format("You have been moved by {0}.", GetName(commandIssuer)));
            target.MoveToRoom(destination,
                string.Format("{0} is whisked away by a strange force.", target.PlayerName),
                string.Format("{0} appears in a flash of light.", target.PlayerName));
            commandIssuer.ReceiveMessage(string.Format("{0} has been moved.", target.PlayerName));
        }

        private static string GetName(Living living)
        {
            var player = living as Player;
            return player != null ? player.PlayerName : living.ShortDescription;
        }

        // 2 = admin, 1 = creator, 0 = player.
        private static int GetRank(Living living)
        {
            if (living.ExtendedProperties.Any(s => s.GetType() == typeof(AdminProperty))) { return 2; }
            if (living.ExtendedProperties.Any(s => s.GetType() == typeof(CreatorProperty))) { return 1; }
            return 0;
        }
    }
}
