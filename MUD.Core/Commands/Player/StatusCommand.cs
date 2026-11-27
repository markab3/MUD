using System.Linq;
using MUD.Core.Formatting;
using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class StatusCommand : ICommand
    {

        public string[] CommandKeywords { get => new string[] { "status" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Default; }

        public string HelpText { get => "List all status effects that are currently applied to you.\r\n \r\nSyntax: status"; }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return null;
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            // string returnString = string.Empty;
            // var statusList = commandIssuer.Statuses.Select(s => s.StatusName).GetListText();
            // if (!string.IsNullOrWhiteSpace(statusList))
            // {
            //     returnString += string.Format("Current statuses:\r\n\r\n{0}\r\n\r\n", statusList);
            // }
            // commandIssuer.ReceiveMessage(returnString);

            commandIssuer.ReceiveMessage(string.Format(
                "Health: {0}/{1}\r\nStamina: {2}/{3}\r\nAlacrity: {4}/{5}\r\nExperience: {6}\r\n"
                + "Strength: {7}  Dexterity: {8}  Intelligence: {9}  Wisdom: {10}  Constitution: {11}  Charisma: {12}",
                commandIssuer.Health, commandIssuer.MaxHealth,
                commandIssuer.Stamina, commandIssuer.MaxStamina,
                commandIssuer.Alacrity, commandIssuer.MaxAlacrity,
                commandIssuer.Experience,
                commandIssuer.Strength, commandIssuer.Dexterity, commandIssuer.Intelligence,
                commandIssuer.Wisdom, commandIssuer.Constitution, commandIssuer.Charisma));
        }
    }
}