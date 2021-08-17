using System.Linq;
using MUD.Core.GameObjects;
using MUD.Core.Properties;

namespace MUD.Core.Commands
{
    public class CDCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "cd" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Creator; }

        public string HelpText { get => "Change your current directory."; }

        public object[] ParseCommand(Player commandIssuer, string input)
        {
            return new object[] { this.StripKeyword(input) }; ;
        }

        public void DoCommand(Player commandIssuer, object[] commandArgs)
        {
            CreatorProperty creatorStatusObj = (CreatorProperty)commandIssuer.ExtendedProperties.FirstOrDefault(s => s.GetType() == typeof(CreatorProperty));
            if (creatorStatusObj == null)
            {
                commandIssuer.ReceiveMessage("This is a creator only command. How did you get this?!");
                return;
            }

            if (commandArgs == null || commandArgs.Length == 0 || !(commandArgs[0] is string) || string.IsNullOrWhiteSpace((string)commandArgs[0]))
            {
                commandIssuer.ReceiveMessage(creatorStatusObj.CurrentDirectory);
            }
            else
            {
                creatorStatusObj.CurrentDirectory = (string)commandArgs[0];
                commandIssuer.ReceiveMessage(creatorStatusObj.CurrentDirectory + ">");
            }
        }
    }
}