using System.Linq;

namespace MUD.Core.Commands
{
    public class NavigateCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "navigate" }; }

        public bool IsDefault { get => true; }

        public string HelpText { get => "Navigate to a specific room using the magic of database ObjectIds.\r\n \r\nFormat: navigate <room Id>"; }

        public object[] ParseCommand(Player commandIssuer, string input)
        {
            return new object[] { this.StripKeyword(input) };
        }

        public void DoCommand(Player commandIssuer, object[] commandArgs)
        {
            if (commandArgs == null || commandArgs.Length < 1 || string.IsNullOrWhiteSpace((string)commandArgs[0]))
            {
                commandIssuer.ReceiveMessage("You have to have somewhere to go!");
                return;
            }

            commandIssuer.MoveToRoom((string)commandArgs[0]);
        }
    }
}