using System.Linq;
using MUD.Core.Formatting;

namespace MUD.Core.Commands
{
    public class CommandsCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "commands" }; }

        public bool IsDefault { get => true; }

        public string HelpText { get => "Returns available commands."; }

        private World _world;

        public CommandsCommand(World world)
        {
            _world = world;
        }

        public object[] ParseCommand(Player commandIssuer, string input)
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
            var defaultCommandList = _world.DefaultCommands.Commands.Select(c => c.Value.CommandKeywords[0]).Distinct().ToArray().GetListText();
            commandIssuer.ReceiveMessage(string.Format("Default Commands:\r\n\r\n{0}", defaultCommandList));
        }
    }
}