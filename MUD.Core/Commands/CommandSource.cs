using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace MUD.Core.Commands
{
    public class CommandSource
    {
        public Dictionary<string, ICommand> Commands;

        public CommandSource()
        {
            Commands = new Dictionary<string, ICommand>();
        }

        public CommandSource(Dictionary<string, ICommand> commands)
        {
            Commands = commands;
        }

        public CommandSource GetSubset(string[] subset)
        {
            if (subset == null || subset.Length == 0) { return new CommandSource(); }
            return new CommandSource(new Dictionary<string, ICommand>(Commands.Where(c => subset.Contains(c.Key))));
        }

        public ICommand GetCommand(string commandKeyword)
        {
            return Commands[commandKeyword];
        }

        public ICommand GetCommandFromInput(string input)
        {
            foreach (var currentPair in Commands)
            {
                if (input.ToLower().StartsWith(currentPair.Key.ToLower()))
                {
                    return currentPair.Value;
                }
            }
            return null;
        }

        public bool AddCommand(ICommand command)
        {
            string keyword = command.CommandKeyword;
            return Commands.TryAdd(command.CommandKeyword, command);
        }

        public void LoadCommandsFromAssembly(Assembly assemblyToLoad)
        {
            var commandTypes = assemblyToLoad.DefinedTypes.Where(t => t.ImplementedInterfaces.Any(i => i == typeof(ICommand)));
            foreach (TypeInfo currentType in commandTypes)
            {
                // Skip AnonymousCommand. It is not intended to be an actual command without being instantiated and given delegates.
                if (currentType.Name == "AnonymousCommand") { continue; }
                ICommand newCommand = (ICommand)Activator.CreateInstance(currentType);
                AddCommand(newCommand);
            }
        }
    }
}