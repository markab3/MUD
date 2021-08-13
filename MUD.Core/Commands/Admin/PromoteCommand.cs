using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MUD.Core.Formatting;
using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class PromoteCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "promote" }; }

        public bool IsDefault { get => true; }

        public string HelpText { get => string.Format("Promote the specified player to the provided rank. Available ranks include {0}.\nSyntax: promote <player> to <rank>", _ranks.GetListText()); }

        private World _world;

        private string[] _ranks = { "player", "creator", "admin" };

        public PromoteCommand(World world)
        {
            _world = world;
        }

        public object[] ParseCommand(Player commandIssuer, string input)
        {
            Match m = Regex.Match(input, "^promote (.+) to (\\w+)$"); // Neat.
            if (m.Success)
            {
                return new object[] { ((string)m.Groups[1].Value).ToLower(), ((string)m.Groups[2].Value).ToLower() };
            }
            else
            {
                return null;
            }
        }

        public void DoCommand(Player commandIssuer, object[] commandArgs)
        {
            if (!(commandIssuer is Admin))
            {
                commandIssuer.ReceiveMessage("This is an admin only command. How did you get this?!");
                return;
            }

            if (commandArgs == null || commandArgs.Length != 2)
            {
                commandIssuer.ReceiveMessage("Could not match input. Syntax: promote <player> to <rank>");
                return;
            }

            var recipient = _world.Players.FirstOrDefault(p => p.PlayerName.ToLower() == (string)commandArgs[0]);

            if (recipient == null)
            {
                commandIssuer.ReceiveMessage(string.Format("Could not find player {0}.", commandArgs[0]));
                return;
            }

            if (!_ranks.Contains(commandArgs[1]))
            {
                commandIssuer.ReceiveMessage(string.Format("{0} is not a valid rank. Available ranks include {1}.", commandArgs[0], _ranks.GetListText()));
                return;
            }


            string jsonString = recipient.ToJson();
            var typeElementPattern = "^(.*)(\"_t\" : \\[.*\\],)(.*)$"; // Buckle up. Gotta fiddle with the type discriminator here to get it to deserialize to the right type.

            Player newPlayerObject = null;
            switch (commandArgs[1])
            {
                case "player":
                    jsonString = Regex.Replace(jsonString, typeElementPattern, "$1\"_t\" : [\"Entity\", \"GameObject\", \"Living\", \"Player\"]$3");
                    newPlayerObject = BsonSerializer.Deserialize<Player>(jsonString);
                    break;
                case "creator":
                    jsonString = Regex.Replace(jsonString, typeElementPattern, "$1\"_t\" : [\"Entity\", \"GameObject\", \"Living\", \"Player\", \"Creator\"]$3");
                    newPlayerObject = BsonSerializer.Deserialize<Creator>(jsonString);
                    break;
                case "admin":
                    jsonString = Regex.Replace(jsonString, typeElementPattern, "$1\"_t\" : [\"Entity\", \"GameObject\", \"Living\", \"Player\", \"Creator\", \"Admin\"]$3");
                    newPlayerObject = BsonSerializer.Deserialize<Admin>(jsonString);
                    break;
            }
            newPlayerObject.Connection = recipient.Connection;
            newPlayerObject.ConnectionStatus = recipient.ConnectionStatus;
            newPlayerObject.CurrentLocation = recipient.CurrentLocation;
            newPlayerObject.Save();

            _world.Players.Remove(recipient);
            recipient.Connection = null;
            recipient.CurrentLocation.ExitRoom(recipient);

            _world.Players.Add(newPlayerObject);
            newPlayerObject.CurrentLocation.EnterRoom(newPlayerObject);

            newPlayerObject.ReceiveMessage(string.Format("You have been promoted to {0}.", commandArgs[1]));
            commandIssuer.ReceiveMessage(string.Format("{0} has been promoted to {1}.", recipient.PlayerName, commandArgs[1]));
        }
    }
}