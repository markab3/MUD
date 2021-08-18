using System;
using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class CommandExecution
    {
        public Living CommandIssuer { get { return _commandIssuer; } }
        public ICommand CommandObject { get { return _commandObject; } }
        protected object[] _commandArgs;

        protected Living _commandIssuer;

        protected string _commandText;

        private ICommand _commandObject;

        public CommandExecution(Living commandIssuer, string commandText, ICommand commandObject)
        {
            _commandIssuer = commandIssuer;
            _commandText = commandText;
            _commandObject = commandObject;
            _commandArgs = _commandObject.ParseCommand(commandIssuer, commandText);
        }

        public void Execute()
        {
            _commandObject.DoCommand(_commandIssuer, _commandArgs);
        }

        public void NotifyException(Exception exception)
        {
            if (_commandIssuer != null)
            {
                _commandIssuer.ReceiveMessage(string.Format("There was an error executing your command of type {0}.\r\n{1}", _commandObject.GetType().Name, exception));
            }
        }
    }
}