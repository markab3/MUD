using System.Linq;
using MUD.Core.GameObjects;
using MUD.Core.Properties.PlayerProperties;

namespace MUD.Core.Commands
{
    public class CloneCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "clone" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Creator; }

        public string HelpText { get => "Create a new object from a given object or JSON template file. Syntax: clone <object or filename>"; }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return new object[] { this.StripKeyword(input) }; ;
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            CreatorProperty creatorStatusObj = (CreatorProperty)commandIssuer.ExtendedProperties.FirstOrDefault(s => s.GetType() == typeof(CreatorProperty));
            if (creatorStatusObj == null)
            {
                commandIssuer.ReceiveMessage("This is a creator only command. How did you get this?!");
                return;
            }
        }
    }
}