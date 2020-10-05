using System.Linq;
using MUD.Core.Formatting;

namespace MUD.Core.Commands
{
    public class CommandsCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "commands" }; }

        public bool IsDefault { get => true; }

        public string HelpText { get => "Returns available commands."; }

        public object[] ParseCommand(string input)
        {
            return null;
        }

        public void DoCommand(Player commandIssuer, object[] commandArgs)
        {
            string returnString = string.Empty;
            var knownCommandList = commandIssuer.KnownCommands.GetListText();
            if (!string.IsNullOrWhiteSpace(knownCommandList))
            {
                returnString += string.Format("Known Commands:\r\n\r\n{0}\r\n\r\n", knownCommandList);
            }
            var defaultCommandList = World.Instance.DefaultCommands.Commands.Select(c => c.Value.CommandKeywords[0]).ToArray().GetListText();
            commandIssuer.ReceiveMessage(string.Format("Default Commands:\r\n\r\n{0}", defaultCommandList));
        }
    }
}