using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class RemoveCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "remove", "unwear" }; }

        public bool IsDefault { get => true; }

        public string HelpText { get => "Remove an item in you are currently wearing."; }

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