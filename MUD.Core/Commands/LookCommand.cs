using MUD.Core.Interfaces;
using System.Linq;
namespace MUD.Core.Commands
{
    public class LookCommand : ICommand
    {
        public string[] CommandKeywords => new string[] { "look", "l", "examine", "exa" };

        public bool IsDefault { get => true; }

        public string HelpText { get => "Send a message to all other players.\r\n \r\nFormat: chat <message>"; }

        public object[] ParseCommand(string input)
        {
            string remainingInput = null;

            foreach (string currentKeyword in CommandKeywords)
            {
                if (input.StartsWith(currentKeyword + " ") || input == currentKeyword)
                {
                    remainingInput = input.Substring(currentKeyword.Length).Trim();
                    break;
                }
            }

            if (remainingInput.ToLower().StartsWith("at "))
            {
                remainingInput = remainingInput.Substring(3);
            }
            return new object[] { remainingInput };
        }

        public void DoCommand(Player commandIssuer, object[] commandArgs)
        {
            if (commandArgs == null || commandArgs.Length == 0 || string.IsNullOrWhiteSpace((string)commandArgs[0]) || ((string)commandArgs[0]).ToLower() == "room")
            {
                // Just plain old look. Return the current room.
                if (commandIssuer.CurrentLocation != null)
                {
                    commandIssuer.ReceiveMessage(commandIssuer.CurrentLocation.Examine());
                }
                else
                {
                    commandIssuer.ReceiveMessage("You are nowhere, adrift in a sea of nothing!");
                }
                return;
            }

            // TODO: Resolve the object(s) given the player's context...
            IExaminable matchedObjects = null;
            if (matchedObjects != null)
            {
                commandIssuer.ReceiveMessage(matchedObjects.Examine());
            }
            else
            {
                commandIssuer.ReceiveMessage("Look at what?");
            }
        }
    }
}