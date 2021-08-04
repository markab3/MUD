using System.Linq;
using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class AnonymousCommand : ICommand
    {
        public string[] CommandKeywords { get; set; }

        public bool IsDefault { get => true; }

        public string HelpText { get; set; }

        public delegate object[] ParseCommandDelegate(Player commandIssuer, string input);

        public delegate void DoCommandDelegate(Player commandIssuer, object[] commandArgs);

        public ParseCommandDelegate ParseCommandHandler;

        public DoCommandDelegate DoCommandHandler;

        public AnonymousCommand() { }

        public object[] ParseCommand(Player commandIssuer, string input)
        {
            return ParseCommandHandler(commandIssuer, input);
        }

        public void DoCommand(Player commandIssuer, object[] commandArgs)
        {
            DoCommandHandler(commandIssuer, commandArgs);
        }
    }
}