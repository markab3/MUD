using System;
using System.Collections.Concurrent;

namespace MUD.Core.Commands
{
    public class CommandQueue
    {
        private ConcurrentQueue<CommandExecution> _internalQueue;

        public CommandQueue()
        {
            _internalQueue = new ConcurrentQueue<CommandExecution>();
        }

        public void AddCommand(CommandExecution newCommand)
        {
            _internalQueue.Enqueue(newCommand);
        }

        public void ExecuteAllCommands()
        {
            CommandExecution command;
            while (_internalQueue.TryDequeue(out command))
            {
                try
                {
                    command.Execute();
                }
                catch (Exception ex) { command.NotifyException(ex); }
            }
        }

        public bool HasMoreCommands()
        {
            return _internalQueue.Count > 0;
        }
    }
}