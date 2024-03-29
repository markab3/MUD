using System.Linq;
using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class NavigateCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "navigate" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Creator; }

        public string HelpText { get => "Navigate to a specific room using the magic of database ObjectIds.\r\n \r\nFormat: navigate <room Id>"; }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return new object[] { this.StripKeyword(input) };
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
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