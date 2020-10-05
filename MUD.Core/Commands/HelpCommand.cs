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

        public object[] ParseCommand(string input)
        {
            return new object[] { input.Substring(5).Trim() };
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