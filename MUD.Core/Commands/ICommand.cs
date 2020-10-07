using System;
using System.Linq;
using System.Reflection;

namespace MUD.Core.Commands
{
    public interface ICommand
    {
        string[] CommandKeywords { get; }

        bool IsDefault { get; }

        string HelpText { get; }

        object[] ParseCommand(Player commandIssuer, string input);

        void DoCommand(Player commandIssuer, object[] commandArgs);
    }
}