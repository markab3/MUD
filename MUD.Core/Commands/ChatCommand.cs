namespace MUD.Core.Commands
{
    public class ChatCommand : ICommand
    {
        public string CommandKeyword { get => "chat"; }
        
        public string[] CommandAliases { get  => null; }

        public bool IsDefault { get => true; }

        public string HelpText { get => "Send a message to all other players.\r\n \r\nFormat: chat <message>"; }

        public object[] ParseCommand(string input)
        {
            return new object[] { input.Substring(5).Trim() };
        }

        public void DoCommand(Player commandIssuer, object[] commandArgs)
        {
            if (commandArgs == null || commandArgs.Length != 1 || string.IsNullOrWhiteSpace((string)commandArgs[0]))
            {
                commandIssuer.ReceiveMessage("You must provide something to chat!");
                return;
            }

            foreach (Player currentPlayer in World.Instance.Players)
            {
                currentPlayer.ReceiveMessage(string.Format("(Chat) {0}: {1}", commandIssuer.PlayerName, (string)commandArgs[0]));
            }
        }
    }
}