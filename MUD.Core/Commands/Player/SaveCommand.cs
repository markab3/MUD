using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class SaveCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "save" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Default; }

        public string HelpText { get => "Saves your precious player information."; }

        public object[] ParseCommand(Player commandIssuer, string input)
        {
            return null;
        }

        public void DoCommand(Player commandIssuer, object[] commandArgs)
        {
            commandIssuer.Save();
        }
    }
}