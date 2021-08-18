using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class QuitCommand : ICommand
    {

        public string[] CommandKeywords { get => new string[] { "quit" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Default; }

        public string HelpText { get => "Quits the game."; }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return null;
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            ((Player)commandIssuer).Quit();
        }
    }
}