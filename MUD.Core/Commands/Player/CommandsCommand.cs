using System.Linq;
using MUD.Core.Formatting;
using MUD.Core.GameObjects;
using MUD.Core.Properties;

namespace MUD.Core.Commands
{
    public class CommandsCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "commands" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Default; }

        public string HelpText { get => "Returns available commands."; }

        private World _world;

        public CommandsCommand(World world)
        {
            _world = world;
        }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return null;
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            string returnString = string.Empty;
            var knownCommandList = commandIssuer.KnownCommands.GetListText();
            if (!string.IsNullOrWhiteSpace(knownCommandList))
            {
                returnString += string.Format("Known Commands:\r\n\r\n{0}\r\n\r\n", knownCommandList);
            }

            var defaultCommandList = _world.DefaultCommands.Commands.Select(c => c.Value.CommandKeywords[0]).Distinct().ToArray().GetListText();
            if (defaultCommandList != null && defaultCommandList.Length > 0)
            {
                returnString = returnString + string.Format("Default Commands:\r\n\r\n{0}", defaultCommandList);
            }

            if (commandIssuer.ExtendedProperties.Any(s => s.GetType() == typeof(CreatorProperty)))
            {
                var creatorCommandList = _world.CreatorCommands.Commands.Select(c => c.Value.CommandKeywords[0]).Distinct().ToArray().GetListText();
                if (creatorCommandList != null && creatorCommandList.Length > 0)
                {
                    returnString = returnString + string.Format("Creator Commands:\r\n\r\n{0}", creatorCommandList);
                }
            }

            if (commandIssuer.ExtendedProperties.Any(s => s.GetType() == typeof(AdminProperty)))
            {
                var adminCommandList = _world.AdminCommands.Commands.Select(c => c.Value.CommandKeywords[0]).Distinct().ToArray().GetListText();
                if (adminCommandList != null && adminCommandList.Length > 0)
                {
                    returnString = returnString + string.Format("Admin Commands:\r\n\r\n{0}", adminCommandList);
                }
            }

            commandIssuer.ReceiveMessage(returnString);
        }
    }
}