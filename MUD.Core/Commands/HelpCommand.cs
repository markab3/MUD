namespace MUD.Core.Commands
{
    public class HelpCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "help" }; }

        public bool IsDefault { get => true; }

        public string HelpText { get => "Displays information about the usage of a command.\r\n \r\nFormat: help <command>"; }

        private string _helpTextWrapper =
@"

-------------------------------- Help System -----------------------------------
{0}
--------------------------------------------------------------------------------";

        public object[] ParseCommand(Player commandIssuer, string input)
        {
            // TODO: Match command here. Also check that it is a default command or the player already knows that command.
            return new object[] { this.StripKeyword(input) };
        }

        public void DoCommand(Player commandIssuer, object[] commandArgs)
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