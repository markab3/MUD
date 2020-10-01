namespace MUD.Core.Commands
{
    public class CommandExecution
    {
        protected object[] _commandArgs;

        protected Player _commandIssuer;

        protected string _commandText;

        private ICommand _commandObject;

        public CommandExecution(Player commandIssuer, string commandText, ICommand commandObject)
        {
            _commandIssuer = commandIssuer;
            _commandText = commandText;
            _commandObject = commandObject;
            _commandArgs = _commandObject.ParseCommand(commandText);
        }

        public void Execute()
        {
            _commandObject.DoCommand(_commandIssuer, _commandArgs);
        }
    }
}