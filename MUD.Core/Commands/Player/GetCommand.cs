using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class GetCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "get" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Default; }

        public string HelpText { get => "Get an object in the room with you."; }

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