using System.Linq;

namespace MUD.Core.Commands
{
    public class AnonymousCommand : ICommand
    {
        public string CommandKeyword { get; set; }

        public bool IsDefault { get => true; }

        public string HelpText { get; set; }

        public delegate object[] ParseCommandDelegate(string input);

        public delegate void DoCommandDelegate(Player commandIssuer, object[] commandArgs);

        public ParseCommandDelegate ParseCommandHandler;

        public DoCommandDelegate DoCommandHandler;

        public AnonymousCommand() {}

        public object[] ParseCommand(string input)
        {
            return ParseCommandHandler(input);
        }

        public void DoCommand(Player commandIssuer, object[] commandArgs)
        {
            DoCommandHandler(commandIssuer, commandArgs);
        }
    }
}