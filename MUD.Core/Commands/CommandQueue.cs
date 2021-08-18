using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class CommandQueue
    {
        private ConcurrentDictionary<Living, ConcurrentQueue<CommandExecution>> _commandQueueDictionary;
        //private ConcurrentQueue<CommandExecution> _internalQueue; // Can't use this because it will block for other players if you queue up a bunch of commands. This is single threaded execution.
        //Each player should have their own queue and their own thread?
        // Once a command completes, it should start the next command? ExecuteAllCommands is actually called from a while loop on world that just continuously runs. Heartbeat is done on another thread.
        // We'll want a thread pool of some sort...

        public CommandQueue()
        {
            //_internalQueue = new ConcurrentQueue<CommandExecution>();
            _commandQueueDictionary = new ConcurrentDictionary<Living, ConcurrentQueue<CommandExecution>>();
        }

        public void AddCommand(CommandExecution newCommand)
        {
            _commandQueueDictionary.TryAdd(newCommand.CommandIssuer, new ConcurrentQueue<CommandExecution>());
            var queue = _commandQueueDictionary[newCommand.CommandIssuer];
            if (newCommand.CommandObject.GetType() == typeof(StopCommand))
            {
                queue.Clear();
            }
            else
            {
                queue.Enqueue(newCommand);
                if (newCommand.CommandIssuer.IsBusy)
                {
                    newCommand.CommandIssuer.ReceiveMessage("Command queued.");
                }
            }
        }

        public void ExecuteAllCommands()
        {
            foreach (Living currentLiving in _commandQueueDictionary.Keys)
            {
                CommandExecution command;
                if (!currentLiving.IsBusy && _commandQueueDictionary[currentLiving].TryDequeue(out command))
                {
                    command.CommandIssuer.IsBusy = true;

                    // Start a new command executing.
                    Task t = Task.Run(() =>
                    {
                        try
                        {
                            command.Execute();
                            command.CommandIssuer.IsBusy = false;
                        }
                        catch (Exception ex) { command.NotifyException(ex); }

                    });
                }
            }
        }

        public bool HasMoreCommands()
        {
            foreach (Living currentLiving in _commandQueueDictionary.Keys)
            {
                if (_commandQueueDictionary[currentLiving].Count > 0) { return true; }
            }
            return false;
        }
    }
}