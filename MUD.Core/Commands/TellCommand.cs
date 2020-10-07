using System.Linq;

namespace MUD.Core.Commands
{
    public class TellCommand : ICommand
    {

        public string[] CommandKeywords { get => new string[] { "tell", "t" }; }

        public bool IsDefault { get => true; }

        public string HelpText { get => "Send a message to a specific person.\r\n \r\nFormat: tell <person> <message>"; }

        private World _world;

        public TellCommand(World world)
        {
            _world = world;
        }

        public object[] ParseCommand(Player commandIssuer, string input)
        {
            input = this.StripKeyword(input);

            // TODO: This is a bad way to do things. What if we want to tell "captain smith" something? 
            // Really should have a way to match things in a room.
            var firstSpace = input.IndexOf(" ");
            if (string.IsNullOrWhiteSpace(input) || firstSpace == -1) { return null; }

            return new object[] { input.Substring(0, firstSpace).Trim(), input.Substring(firstSpace).Trim() };
        }

        public void DoCommand(Player commandIssuer, object[] commandArgs)
        {
            if (commandArgs == null || commandArgs.Length != 2)
            {
                commandIssuer.ReceiveMessage("Tell who what now?");
                return;
            }

            string recipientName = ((string)commandArgs[0]).ToLower();
            var recipient = _world.Players.FirstOrDefault(p => p.PlayerName.ToLower() == recipientName);

            if (recipient == null)
            {
                commandIssuer.ReceiveMessage(string.Format("Could not find {0}.", (string)commandArgs[0]));
                return;
            }

            string recipientMessage = string.Format("{0} tells you: {1}", commandIssuer.PlayerName, (string)commandArgs[1]);
            string myMessage = string.Format("You tell {0}: {1}", recipient.PlayerName, (string)commandArgs[1]);

            recipient.ReceiveMessage(recipientMessage);
            commandIssuer.ReceiveMessage(myMessage);
        }
    }
}