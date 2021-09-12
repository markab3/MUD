using System;
using System.Linq;
using MUD.Core.Formatting;
using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class WhoCommand : ICommand
    {

        public string[] CommandKeywords { get => new string[] { "who" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Default; }

        public string HelpText { get => "See who is logged on right now.\r\n \r\nSyntax: who"; }

        private World _world;

        public WhoCommand(World world)
        {
            _world = world;
        }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return null;
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            // Show the list of logged in players
            if (_world.Players == null || _world.Players.Count == 0)
            {
                commandIssuer.ReceiveMessage("No one is logged on.");
            }
            else if (_world.Players.Count == 1)
            {
                commandIssuer.ReceiveMessage(String.Format("{0} is logged on.", _world.Players.First().PlayerName));
            }
            else
            {
                commandIssuer.ReceiveMessage(String.Format("{0} are logged on.", _world.Players.Select(p => p.PlayerName).GetListText()));
            }
        }
    }
}