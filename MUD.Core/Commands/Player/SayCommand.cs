using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class SayCommand : ICommand
    {

        public string[] CommandKeywords { get => new string[] { "say", "'" }; }

        public bool IsDefault { get => true; }

        public string HelpText { get => "Send a message to everyone in the same room.\r\n \r\nFormat: say <message>"; }

        public SayCommand() { }

        public object[] ParseCommand(Player commandIssuer, string input)
        {
            return new object[] { this.StripKeyword(input) };
        }

        public void DoCommand(Player commandIssuer, object[] commandArgs)
        {
            if (commandArgs == null || commandArgs.Length != 1 || string.IsNullOrWhiteSpace((string)commandArgs[0]))
            {
                commandIssuer.ReceiveMessage("You must provide something to chat!");
                return;
            }

            string roomSayMessage = string.Format("{0} says: {1}", commandIssuer.PlayerName, (string)commandArgs[0]);
            string mySayMessage = string.Format("You say: {0}", (string)commandArgs[0]);

            Room currentRoom = commandIssuer.CurrentLocation;
            if (currentRoom != null)
            {
                currentRoom.TellRoom(roomSayMessage, new Player[] { commandIssuer });
            }
            commandIssuer.ReceiveMessage(mySayMessage);
        }
    }
}