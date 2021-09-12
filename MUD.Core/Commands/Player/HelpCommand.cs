using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class HelpCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "help" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Default; }

        public string HelpText { get => "Displays information about the usage of a command.\r\n \r\nSyntax: help <command>"; }

        private string _helpTextWrapper =
@"

-------------------------------- Help System -----------------------------------
{0}
--------------------------------------------------------------------------------";

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            // TODO: Match command here. Also check that it is a default command or the player already knows that command.
            return new object[] { this.StripKeyword(input) };
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            if (commandArgs == null || commandArgs.Length != 1 || string.IsNullOrWhiteSpace((string)commandArgs[0]))
            {
                commandIssuer.ReceiveMessage("What did you need help with?");
                return;
            }

            var matchedCommand = commandIssuer.ResolveCommand((string)commandArgs[0]);

            if (matchedCommand != null)
            {
                commandIssuer.ReceiveMessage(string.Format(_helpTextWrapper, matchedCommand.HelpText));
            }
        }
    }
}