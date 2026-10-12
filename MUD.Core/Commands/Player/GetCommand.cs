using System.Collections.Generic;
using System.Linq;
using System.Text;
using MUD.Core.Formatting;
using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class GetCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "get" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Default; }

        public string HelpText { get => "Get an object from your current surroundings.\r\n \r\nSyntax: get <object>"; }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return new object[] { this.StripKeyword(input) };
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            if (string.IsNullOrWhiteSpace((string)commandArgs[0]))
            {
                commandIssuer.ReceiveMessage("Get what? Syntax: get <object>");
                return;
            }
            // Resolve the object(s) given the player's context.
            var matchedObjects = commandIssuer.MatchObjects(((string)commandArgs[0]).ToLower(), false, true, false, false);
            if (matchedObjects != null && matchedObjects.Length > 0)
            {
                // Only move items that were actually removed from the room.
                var movedObjects = new List<GameObject>();
                foreach (GameObject currentItem in matchedObjects)
                {
                    if (commandIssuer.CurrentLocation.Items.Remove((InventoryItem)currentItem))
                    {
                        commandIssuer.Inventory.Add((InventoryItem)currentItem);
                        movedObjects.Add(currentItem);
                    }
                }

                if (movedObjects.Count > 0)
                {
                    // Write-through. Player first, then room: a crash between the two duplicates the item rather than losing it.
                    (commandIssuer as Player)?.Save(true);
                    commandIssuer.CurrentLocation.SaveState();

                    string itemList = movedObjects.Select(ob => ob.ShortDescription).GetListText();
                    commandIssuer.ReceiveMessage(string.Format("You get {0}.", itemList));
                    commandIssuer.CurrentLocation.TellRoom(string.Format("{0} gets {1}.", commandIssuer.ShortDescription, itemList), new Living[] { commandIssuer });
                }
                else
                {
                    commandIssuer.ReceiveMessage(string.Format("You can't get any {0}.", (string)commandArgs[0]));
                }
            }
            else
            {
                commandIssuer.ReceiveMessage(string.Format("You can't get any {0}.", (string)commandArgs[0]));
            }
        }
    }
}