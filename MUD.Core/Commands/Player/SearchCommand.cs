using System.Linq;
using MUD.Core.GameObjects;
using MUD.Core.Properties;

namespace MUD.Core.Commands
{
    public class SearchCommand : ICommand
    {

        public string[] CommandKeywords { get => new string[] { "search" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Default; }

        public string HelpText { get => "Search for things in the specified spot. If no additional arguments are provided, search the room in general. Syntax: search <optional item>"; }


        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return new object[] { this.StripKeyword(input) };
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            // Room currentRoom = commandIssuer.CurrentLocation;
            // SearchProperty searchProp = (SearchProperty)currentRoom.ExtendedProperties.FirstOrDefault(p => p.GetType() == typeof(SearchProperty));
            // if (searchProp != null)
            // {
            //     searchProp.DoSearch(commandIssuer, (string)commandArgs[0]);
            // }
            // else
            // {
            //     commandIssuer.ReceiveMessage(string.Format("You search the {0} but find nothing of any value.", (string)commandArgs[0]));
            // }
            commandIssuer.ReceiveMessage("You begin your search.");
            System.Threading.Thread.Sleep(3000);
            commandIssuer.ReceiveMessage("You finish your search.");
            commandIssuer.CurrentLocation.TellRoom(string.Format("{0} searches around a bit.", commandIssuer.ShortDescription), new Living[] { commandIssuer });
        }
    }
}