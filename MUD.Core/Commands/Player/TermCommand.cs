using System.Linq;
using MUD.Core.Formatting;
using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class TermCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "term", "terminal" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Default; }

        public string HelpText { get => string.Format("Set your terminal type. Current types include: {0}\r\n \r\nSyntax: term <terminal type>", TerminalTypes.GetListText()); }

        // The terminal names a player may choose. The telnet front end maps each name to its conversion (see TelnetSessionSink);
        // a name it does not know leaves the markup as it is. Fixed here so core does not depend on the front end.
        public static readonly string[] TerminalTypes = new string[] { "ansi", "default", "gmud", "mudlet" };

        private World _world;

        public TermCommand(World world)
        {
            _world = world;
        }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return new object[] { this.StripKeyword(input) };
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            if (commandArgs == null || commandArgs.Length == 0 || string.IsNullOrWhiteSpace((string)commandArgs[0]))
            {
                commandIssuer.ReceiveMessage(string.Format("Terminal currently set to: {0}", ((Player)commandIssuer).SelectedTerm));
                return;
            }

            string newTermValue = ((string)commandArgs[0]).Trim().ToLower();
            if (TerminalTypes.Contains(newTermValue))
            {
                ((Player)commandIssuer).ChangeTerminal(newTermValue);
                commandIssuer.ReceiveMessage(string.Format("Terminal type set to {0}.", newTermValue));
            }
            else
            {
                commandIssuer.ReceiveMessage(string.Format("Terminal type {0} not recognized. Current types include: {1}", newTermValue, TerminalTypes.GetListText()));
            }
        }
    }
}