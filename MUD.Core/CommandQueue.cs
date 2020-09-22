using System.Collections.Concurrent;
using System.Collections.Generic;

namespace MUD.Core
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

        private ConcurrentQueue<Command> _internalQueue;

        private CommandQueue()
        {
            _internalQueue = new ConcurrentQueue<Command>();
        }

        public void AddCommand(Command newCommand)
        {
            _internalQueue.Enqueue(newCommand);
        }

        public bool ExecuteAllCommands()
        {
            Command command;
            while(_internalQueue.TryDequeue(out command)) {
                return command.DoCommand();
            }
            return false;
        }

        public bool HasMoreCommands()
        {
            return _internalQueue.Count > 0;
        }
    }
}