using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class DropCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "drop" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Default; }

        public string HelpText { get => "Drop an item in your inventory."; }

        public object[] ParseCommand(Player commandIssuer, string input)
        {
            return null;
        }

        public void DoCommand(Player commandIssuer, object[] commandArgs)
        {
            return;
        }
    }
}