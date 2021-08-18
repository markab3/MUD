using System.Linq;
using System.Text.RegularExpressions;
using MUD.Core.Formatting;
using MUD.Core.GameObjects;
using MUD.Core.Properties;

namespace MUD.Core.Commands
{
    public class PromoteCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "promote" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Default; }

        public string HelpText { get => string.Format("Promote the specified player to the provided rank. Available ranks include {0}.\nSyntax: promote <player> to <rank>", _ranks.GetListText()); }

        private World _world;

        private string[] _ranks = { "player", "creator", "admin" };

        public PromoteCommand(World world)
        {
            _world = world;
        }

        public object[] ParseCommand(Living commandIssuer, string input)
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

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            AdminProperty adminStatusObj = (AdminProperty)commandIssuer.ExtendedProperties.FirstOrDefault(s => s.GetType() == typeof(AdminProperty));

            // if (adminStatusObj == null)
            // {
            //     commandIssuer.ReceiveMessage("This is an admin only command. How did you get this?!");
            //     return;
            // }

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

            // Yuck yuck barf vomit. This was really tortured. I am keeping it here for the neat regex stuff though. Like project paperclip.
            // string jsonString = recipient.ToJson();
            // var typeElementPattern = "^(.*)(\"_t\" : \\[.*\\],)(.*)$"; // Buckle up. Gotta fiddle with the type discriminator here to get it to deserialize to the right type.

            // Player newPlayerObject = null;
            // switch (commandArgs[1])
            // {
            //     case "player":
            //         jsonString = Regex.Replace(jsonString, typeElementPattern, "$1\"_t\" : [\"Entity\", \"GameObject\", \"Living\", \"Player\"]$3");
            //         newPlayerObject = BsonSerializer.Deserialize<Player>(jsonString);
            //         break;
            //     case "creator":
            //         jsonString = Regex.Replace(jsonString, typeElementPattern, "$1\"_t\" : [\"Entity\", \"GameObject\", \"Living\", \"Player\", \"Creator\"]$3");
            //         newPlayerObject = BsonSerializer.Deserialize<Creator>(jsonString);
            //         break;
            //     case "admin":
            //         jsonString = Regex.Replace(jsonString, typeElementPattern, "$1\"_t\" : [\"Entity\", \"GameObject\", \"Living\", \"Player\", \"Creator\", \"Admin\"]$3");
            //         newPlayerObject = BsonSerializer.Deserialize<Admin>(jsonString);
            //         break;
            // }
            // newPlayerObject.Connection = recipient.Connection;
            // newPlayerObject.ConnectionStatus = recipient.ConnectionStatus;
            // newPlayerObject.CurrentLocation = recipient.CurrentLocation;
            // newPlayerObject.Save();

            // _world.Players.Remove(recipient);
            // recipient.Connection = null;
            // recipient.CurrentLocation.ExitRoom(recipient);

            // _world.Players.Add(newPlayerObject);
            // newPlayerObject.CurrentLocation.EnterRoom(newPlayerObject);
            switch (commandArgs[1])
            {
                case "player":
                    recipient.ExtendedProperties.RemoveAll(s => s.GetType() == typeof(CreatorProperty) || s.GetType() == typeof(AdminProperty));
                    break;
                case "creator":
                    recipient.ExtendedProperties.RemoveAll(s => s.GetType() == typeof(AdminProperty));
                    if (!recipient.ExtendedProperties.Any(s => s.GetType() == typeof(CreatorProperty)))
                    {
                        recipient.ExtendedProperties.Add(new CreatorProperty());
                    }
                    break;
                case "admin":
                    if (!recipient.ExtendedProperties.Any(s => s.GetType() == typeof(CreatorProperty)))
                    {
                        recipient.ExtendedProperties.Add(new CreatorProperty());
                    }
                    if (!recipient.ExtendedProperties.Any(s => s.GetType() == typeof(AdminProperty)))
                    {
                        recipient.ExtendedProperties.Add(new AdminProperty());
                    }
                    break;
            }

            recipient.Save();
            recipient.ReceiveMessage(string.Format("You have been promoted to {0}.", commandArgs[1]));
            commandIssuer.ReceiveMessage(string.Format("{0} has been promoted to {1}.", recipient.PlayerName, commandArgs[1]));
        }
    }
}