using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class RemoveCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "remove", "unwear" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Default; }

        public string HelpText { get => "Remove an item in you are currently wearing."; }

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