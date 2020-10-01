using System;
using System.Linq;
using System.Reflection;

namespace MUD.Core.Commands
{
    public interface ICommand
    {
        string CommandKeyword { get; }

        string[] CommandAliases { get; }

        bool IsDefault { get; }

        string HelpText { get; }

        object[] ParseCommand(string input);

        void DoCommand(Player commandIssuer, object[] commandArgs);
    }
}