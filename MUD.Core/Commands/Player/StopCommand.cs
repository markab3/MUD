using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class StopCommand : ICommand
    {

        public string[] CommandKeywords { get => new string[] { "stop" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Default; }

        public string HelpText { get => "Clear your current command queue.\r\n \r\nFormat: stop"; }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return null;
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            // Do nothing? Maybe there's a better way to handle this...
        }
    }
}