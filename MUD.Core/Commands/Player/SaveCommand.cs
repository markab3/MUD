using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class SaveCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "save" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Default; }

        public string HelpText { get => "Saves your precious player information."; }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return null;
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            ((Player)commandIssuer).Save();
        }
    }
}