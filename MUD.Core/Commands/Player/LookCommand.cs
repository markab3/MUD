using MUD.Core.GameObjects;
using MUD.Core.Interfaces;
using System.Linq;
using System.Text;

namespace MUD.Core.Commands
{
    public class LookCommand : ICommand
    {
        public string[] CommandKeywords => new string[] { "look", "l", "examine", "exa" };

        public CommandCategories CommandCategory { get => CommandCategories.Default; }

        public string HelpText { get => "Send a message to all other players.\r\n \r\nFormat: chat <message>"; }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            string remainingInput = this.StripKeyword(input);

            if (remainingInput.ToLower().StartsWith("at "))
            {
                remainingInput = remainingInput.Substring(3);
            }

            return new object[] { remainingInput };
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            if (commandArgs == null || commandArgs.Length == 0 || string.IsNullOrWhiteSpace((string)commandArgs[0]) || ((string)commandArgs[0]).ToLower() == "room")
            {
                // Just plain old look. Return the current room.
                if (commandIssuer.CurrentLocation != null)
                {
                    commandIssuer.ReceiveMessage(commandIssuer.CurrentLocation.Examine(commandIssuer));
                }
                else
                {
                    commandIssuer.ReceiveMessage("You are nowhere, adrift in a sea of nothing!");
                }
                return;
            }

            // TODO: Resolve the object(s) given the player's context...
            var matchedObjects = commandIssuer.MatchObjects(((string)commandArgs[0]).ToLower());
            if (matchedObjects != null && matchedObjects.Length > 0)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendJoin("\r\n", matchedObjects.Select(g => g.Examine()));
                commandIssuer.ReceiveMessage(sb.ToString());
            }
            else
            {
                commandIssuer.ReceiveMessage(string.Format("There is no {0} to look at.", (string)commandArgs[0]));
            }
        }
    }
}