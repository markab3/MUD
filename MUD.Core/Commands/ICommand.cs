using System;
using System.Linq;
using System.Reflection;

namespace MUD.Core
{
    public interface ICommand
    {
        public string CommandKeyword { get; }

        public bool IsDefault { get; }

        public string HelpText { get; }

        public object[] ParseCommand(string input);

        public void DoCommand(Player commandIssuer, object[] commandArgs);
    }
}