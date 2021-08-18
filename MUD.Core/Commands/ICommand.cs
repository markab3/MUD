using System;
using System.Linq;
using System.Reflection;
using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public interface ICommand
    {
        string[] CommandKeywords { get; }

        CommandCategories CommandCategory { get; }

        string HelpText { get; }

        object[] ParseCommand(Living commandIssuer, string input);

        void DoCommand(Living commandIssuer, object[] commandArgs);
    }
}