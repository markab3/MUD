namespace MUD.Core
{
    public class HelpCommand : ICommand
    {
        public string CommandKeyword { get => "help"; }

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
                commandIssuer.Connection.Send("What did you need help with?");
            }

            var matchedCommand = commandIssuer.ResolveCommand((string) commandArgs[0]);

            if (matchedCommand != null) {
                commandIssuer.Connection.Send(string.Format(_helpTextWrapper, matchedCommand.HelpText));
            }
        }
    }
}