using System.Linq;

namespace MUD.Core.Commands
{
    public class NavigateCommand : ICommand
    {
        public string CommandKeyword { get => "navigate"; }

        public string[] CommandAliases { get  => null; }

        public bool IsDefault { get => true; }

        public string HelpText { get => "Navigate to a specific room using the magic of database ObjectIds.\r\n \r\nFormat: navigate <room Id>"; }

        public object[] ParseCommand(string input)
        {
            return new object[] { input.Substring(CommandKeyword.Length).Trim() };
        }

        public void DoCommand(Player commandIssuer, object[] commandArgs)
        {
            if (commandArgs == null || commandArgs.Length != 1 || string.IsNullOrWhiteSpace((string)commandArgs[0]))
            {
                commandIssuer.ReceiveMessage("You have to have somewhere to go!");
                return;
            }

            Room destination = World.Instance.Rooms.FirstOrDefault(r => r._id == (string)commandArgs[0]);
            if (destination != null)
            {
                commandIssuer.MoveToRoom(destination);
            }
        }
    }
}