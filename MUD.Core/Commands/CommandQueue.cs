using System.Collections.Concurrent;

namespace MUD.Core.Commands
{
    public class CommandQueue
    {
        public static CommandQueue Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new CommandQueue();
                }
                return _instance;
            }
        }
        private static CommandQueue _instance;

        private ConcurrentQueue<CommandExecution> _internalQueue;

        private CommandQueue()
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
            while(_internalQueue.TryDequeue(out command)) {
                command.Execute();
            }
        }

        public bool HasMoreCommands()
        {
            return _internalQueue.Count > 0;
        }
    }
}