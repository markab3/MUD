using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class LowerCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "lower", "unwield", "unhold" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Default; }

        public string HelpText { get => "Lower an item you are currently holding.\r\n \r\nSyntax: lower <item>"; }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return null;
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            return;
        }
    }
}