using System.Linq;
using System.Text;
using MUD.Core.Formatting;
using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class DropCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "drop" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Default; }

        public string HelpText { get => "Drop an item in your inventory.\r\n \r\nSyntax: drop <object>"; }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return new object[] { this.StripKeyword(input) };
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            if (string.IsNullOrWhiteSpace((string)commandArgs[0]))
            {
                commandIssuer.ReceiveMessage("Drop what? Syntax: drop <object>");
            }
            // Resolve the object(s) given the player's context.
            var matchedObjects = commandIssuer.MatchObjects(((string)commandArgs[0]).ToLower(), true, false, false, false);
            if (matchedObjects != null && matchedObjects.Length > 0)
            {
                foreach (GameObject currentItem in matchedObjects)
                {
                    commandIssuer.CurrentLocation.Items.Add((InventoryItem)currentItem);
                    commandIssuer.Inventory.Remove((InventoryItem)currentItem);
                }

                string itemList = matchedObjects.Select(ob => ob.ShortDescription).GetListText();
                commandIssuer.ReceiveMessage(string.Format("You drop {0}.", itemList));
                commandIssuer.CurrentLocation.TellRoom(string.Format("{0} drops {1}.", commandIssuer.ShortDescription, matchedObjects.Select(ob => ob.ShortDescription).GetListText()), new Living[] { commandIssuer });
            }
            else
            {
                commandIssuer.ReceiveMessage(string.Format("You don't have any {0} to drop.", (string)commandArgs[0]));
            }
        }
    }
}