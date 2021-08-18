using System.Linq;
using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class AnonymousCommand : ICommand
    {
        public string[] CommandKeywords { get; set; }

        public CommandCategories CommandCategory { get => CommandCategories.Default; }

        public string HelpText { get; set; }

        public delegate object[] ParseCommandDelegate(Living commandIssuer, string input);

        public delegate void DoCommandDelegate(Living commandIssuer, object[] commandArgs);

        public ParseCommandDelegate ParseCommandHandler;

        public DoCommandDelegate DoCommandHandler;

        public AnonymousCommand() { }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return ParseCommandHandler(commandIssuer, input);
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            DoCommandHandler(commandIssuer, commandArgs);
        }
    }
}