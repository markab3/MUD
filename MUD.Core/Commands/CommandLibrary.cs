using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace MUD.Core
{
    public class CommandLibrary
    {
        public Dictionary<string, ICommand> Commands;

        public CommandLibrary()
        {
            Commands = new Dictionary<string, ICommand>();
        }

        public CommandLibrary(Dictionary<string, ICommand> commands)
        {
            Commands = commands;
        }

        public CommandLibrary GetSubset(string[] subset)
        {
            if (subset == null || subset.Length == 0) { return new CommandLibrary(); }
            return new CommandLibrary(new Dictionary<string, ICommand>(Commands.Where(c => subset.Contains(c.Key))));
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
                AddCommand((ICommand)Activator.CreateInstance(currentType));
            }
        }
    }
}