using System.Text;
using MUD.Core.Formatting;
using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class InventoryCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "inventory", "i", "eq", "equipment" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Default; }

        public string HelpText { get => "See what you are holding.\r\n \r\nSyntax: inventory"; }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return null;
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            StringBuilder inventoryString = new StringBuilder();
            if (commandIssuer.Inventory != null && commandIssuer.Inventory.Count > 0)
            {
                bool firstItem = true;
                foreach (InventoryItem currentItem in commandIssuer.Inventory)
                {
                    if (firstItem)
                    {
                        inventoryString.Append(string.Format("Carrying : {0} {1}\r\n", currentItem.ShortDescription.GetArticle(), currentItem.ShortDescription));
                        firstItem = false;
                    }
                    else
                    {
                        inventoryString.Append(string.Format("           {0} {1}\r\n", currentItem.ShortDescription.GetArticle(), currentItem.ShortDescription));
                    }
                }
            }
            else
            {
                inventoryString.Append("You have nothing.\r\n");
            }
            commandIssuer.ReceiveMessage(inventoryString.ToString());
        }
    }
}