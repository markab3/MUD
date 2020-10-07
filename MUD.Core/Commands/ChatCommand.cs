namespace MUD.Core.Commands
{
    public class ChatCommand : ICommand
    {

        public string[] CommandKeywords { get => new string[] { "chat" }; }

        public bool IsDefault { get => true; }

        public string HelpText { get => "Send a message to all other players.\r\n \r\nFormat: chat <message>"; }

        private World _world;

        public ChatCommand(World world) {
            _world = world;
        }

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

            foreach (Player currentPlayer in _world.Players)
            {
                currentPlayer.ReceiveMessage(string.Format("(Chat) {0}: {1}", commandIssuer.PlayerName, (string)commandArgs[0]));
            }
        }
    }
}