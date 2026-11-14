using MUD.Core.GameObjects;
using MUD.CoreApi;

namespace MUD.Core.Commands
{
    public class SayCommand : ICommand
    {

        public string[] CommandKeywords { get => new string[] { "say", "'" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Default; }

        public string HelpText { get => "Send a message to everyone in the same room.\r\n \r\nSyntax: say <message>"; }

        public SayCommand() { }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return new object[] { this.StripKeyword(input) };
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            if (commandArgs == null || commandArgs.Length != 1 || string.IsNullOrWhiteSpace((string)commandArgs[0]))
            {
                commandIssuer.ReceiveMessage("You must provide something to chat!");
                return;
            }

            string text;
            bool shortened = CommandExtensions.TruncateMessage((string)commandArgs[0], out text);

            string roomSayMessage = string.Format("{0} says: {1}", commandIssuer.ShortDescription, text);
            string mySayMessage = string.Format("You say: {0}", text);

            Room currentRoom = commandIssuer.CurrentLocation;
            if (currentRoom != null)
            {
                currentRoom.TellRoom(roomSayMessage, new Living[] { commandIssuer }, OutputChannel.Say);
            }
            commandIssuer.ReceiveMessage(mySayMessage, OutputChannel.Say);
            if (shortened) { commandIssuer.ReceiveMessage(CommandExtensions.MessageShortenedNotice); }
        }
    }
}