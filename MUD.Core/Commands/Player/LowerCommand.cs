using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class LowerCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "lower", "unwield", "unhold" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Default; }

        public string HelpText { get => "Lower an item you are currently holding."; }

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