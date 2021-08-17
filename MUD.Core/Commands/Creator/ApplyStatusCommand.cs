using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class ApplyStatusCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "apply" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Creator; }

        public string HelpText { get => "Apply a given status to a living object. Syntax: apply <status name> to <living>"; }

        public object[] ParseCommand(Player commandIssuer, string input)
        {
            return new object[] { this.StripKeyword(input) }; ;
        }

        public void DoCommand(Player commandIssuer, object[] commandArgs)
        {

        }
    }
}