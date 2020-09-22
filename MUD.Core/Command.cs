
namespace MUD.Core
{
    public abstract class Command
    {
        protected static string _commandKeyword = string.Empty;
        protected object[] _commandArgs;

        protected Player _commandIssuer;

        protected string _commandText;

        public Command(Player commandIssuer, string commandText)
        {
            _commandIssuer = commandIssuer;
            _commandText = commandText;
            _commandArgs = parseCommand(commandText);
        }

        public abstract bool DoCommand();

        protected abstract object[] parseCommand(string input);
    }
}