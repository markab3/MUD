using System.Linq;
using MUD.Core.Formatting;

namespace MUD.Core.Commands
{
    public class TermCommand : ICommand
    {
        public string CommandKeyword { get => "term"; }

        public bool IsDefault { get => true; }

        public string HelpText { get => string.Format("Set your terminal type. Current types include: {0}\r\n \r\nFormat: term <terminal type>", _terminalTypes.GetListText()); }

        private string[] _terminalTypes = null;

        public object[] ParseCommand(string input)
        {
            return new object[] { input.Substring(CommandKeyword.Length).Trim() };
        }

        public void DoCommand(Player commandIssuer, object[] commandArgs)
        {
            if (_terminalTypes == null) {
                _terminalTypes = World.Instance.TerminalHandlers.Select(t => t.TerminalName).ToArray();
                var terminalAliases = World.Instance.TerminalHandlers.Where(a => a.Aliases != null).Select(t => t.Aliases);
                if (terminalAliases != null && terminalAliases.Count() > 0) {
                    foreach(var currentAliasSet in terminalAliases) {
                        _terminalTypes = _terminalTypes.Union(currentAliasSet).ToArray();
                    }
                }
            }

            if (commandArgs == null || commandArgs.Length == 0 || string.IsNullOrWhiteSpace((string)commandArgs[0]))
            {
                commandIssuer.ReceiveMessage(string.Format("Terminal currently set to: {0}", commandIssuer.SelectedTerm));
                return;
            }
            
            string newTermValue = ((string) commandArgs[0]).Trim();
            if (_terminalTypes.Contains(newTermValue)) {
                commandIssuer.SelectedTerm = newTermValue;
                commandIssuer.ReceiveMessage(string.Format("Terminal type set to {0}.", newTermValue));
            } else {
                commandIssuer.ReceiveMessage(string.Format("Terminal type {0} not recognized. Current types include: {1}", newTermValue, _terminalTypes.GetListText()));
            }
        }
    }
}