using System.Collections.Generic;
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
                return;
            }
            // Resolve the object(s) given the player's context.
            var matchedObjects = commandIssuer.MatchObjects(((string)commandArgs[0]).ToLower(), true, false, false, false);
            if (matchedObjects != null && matchedObjects.Length > 0)
            {
                // Only move items that were actually removed from the inventory.
                var movedObjects = new List<GameObject>();
                foreach (GameObject currentItem in matchedObjects)
                {
                    if (commandIssuer.Inventory.Remove((InventoryItem)currentItem))
                    {
                        commandIssuer.CurrentLocation.Items.Add((InventoryItem)currentItem);
                        movedObjects.Add(currentItem);
                    }
                }

                if (movedObjects.Count > 0)
                {
                    // Write-through. Room first, then player: a crash between the two duplicates the item rather than losing it.
                    commandIssuer.CurrentLocation.SaveState();
                    (commandIssuer as Player)?.Save(true);

                    string itemList = movedObjects.Select(ob => ob.ShortDescription).GetListText();
                    commandIssuer.ReceiveMessage(string.Format("You drop {0}.", itemList));
                    commandIssuer.CurrentLocation.TellRoom(string.Format("{0} drops {1}.", commandIssuer.ShortDescription, itemList), new Living[] { commandIssuer });
                }
                else
                {
                    commandIssuer.ReceiveMessage(string.Format("You don't have any {0} to drop.", (string)commandArgs[0]));
                }
            }
            else
            {
                commandIssuer.ReceiveMessage(string.Format("You don't have any {0} to drop.", (string)commandArgs[0]));
            }
        }
    }
}