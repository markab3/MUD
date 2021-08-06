using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class WearCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "wear" }; }

        public bool IsDefault { get => true; }

        public string HelpText { get => "Wear an item in your inventory."; }

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