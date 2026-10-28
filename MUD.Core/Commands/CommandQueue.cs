using System;
using System.Collections.Generic;
using System.Linq;
using MUD.Core.GameObjects;
using MUD.Data;

namespace MUD.Core.Commands
{
    /// <summary>
    /// Pending commands per living. GAME-THREAD ONLY (specs/GAME_LOOP.md section 5.1): nothing here is
    /// synchronised, and commands run synchronously on the calling thread.
    ///
    /// A living may act when clock.Now >= NextActionAt. Commands are queued per living and at most one runs per
    /// living per dispatch; a command that takes game time sets NextActionAt instead of waiting
    /// (see DelayThen).
    /// </summary>
    public class CommandQueue
    {
        private readonly IClock _clock;
        // Only livings with something queued are present, so a tick does not scan everyone.
        private readonly Dictionary<Living, LinkedList<CommandExecution>> _queues = new Dictionary<Living, LinkedList<CommandExecution>>();

        /// <summary>The current game time, as this queue sees it.</summary>
        public DateTime Now { get { return _clock.Now; } }

        public CommandQueue(IClock clock)
        {
            _clock = clock ?? SystemClock.Instance;
        }

        public void AddCommand(CommandExecution newCommand)
        {
            Living issuer = newCommand.CommandIssuer;
            if (newCommand.CommandObject.GetType() == typeof(StopCommand))
            {
                Clear(issuer);
                return;
            }

            LinkedList<CommandExecution> queue;
            if (!_queues.TryGetValue(issuer, out queue))
            {
                queue = new LinkedList<CommandExecution>();
                _queues[issuer] = queue;
            }
            queue.AddLast(newCommand);

            if (_clock.Now < issuer.NextActionAt)
            {
                issuer.ReceiveMessage("Command queued.");
                return;
            }
            DispatchOne(issuer);
        }

        /// <summary>
        /// Makes the living busy for the given game time and then runs the action as its next command, ahead of
        /// anything already queued. "stop" cancels it. This replaces sleeping inside a command.
        /// </summary>
        public void DelayThen(Living living, TimeSpan delay, Action action)
        {
            living.NextActionAt = _clock.Now + delay;
            var pseudoCommand = new AnonymousCommand
            {
                CommandKeywords = new string[0],
                ParseCommandHandler = (l, input) => null,
                DoCommandHandler = (l, args) => action()
            };

            LinkedList<CommandExecution> queue;
            if (!_queues.TryGetValue(living, out queue))
            {
                queue = new LinkedList<CommandExecution>();
                _queues[living] = queue;
            }
            queue.AddFirst(new CommandExecution(living, string.Empty, pseudoCommand));
        }

        /// <summary>Drops everything queued for the living ("stop", logout).</summary>
        public void Clear(Living living)
        {
            _queues.Remove(living);
        }

        /// <summary>Runs at most one command for each living that is ready. Called every tick.</summary>
        public void Dispatch()
        {
            // Snapshot: commands may queue or clear commands for anyone.
            foreach (Living living in _queues.Keys.ToList())
            {
                if (_clock.Now >= living.NextActionAt) { DispatchOne(living); }
            }
        }

        public bool HasMoreCommands()
        {
            return _queues.Count > 0;
        }

        public int PendingCount(Living living)
        {
            LinkedList<CommandExecution> queue;
            return _queues.TryGetValue(living, out queue) ? queue.Count : 0;
        }

        private void DispatchOne(Living living)
        {
            LinkedList<CommandExecution> queue;
            if (!_queues.TryGetValue(living, out queue)) { return; }

            CommandExecution command = queue.First.Value;
            queue.RemoveFirst();
            if (queue.Count == 0) { _queues.Remove(living); }

            try
            {
                command.Execute();
            }
            catch (Exception ex)
            {
                command.NotifyException(ex);
            }
        }
    }
}
