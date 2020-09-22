
namespace MUD.Core
{
    public class ChatCommand : Command
    {
        private static new string _commandKeyword = "chat";

        public ChatCommand(Player commandIssuer, string commandText) : base(commandIssuer, commandText) { }

        public override bool DoCommand()
        {
            if (_commandArgs == null || _commandArgs.Length != 1 || string.IsNullOrWhiteSpace((string)_commandArgs[0]))
            {
                _commandIssuer.Connection.Send("You must provide something to chat!");
            }

            foreach (Player currentPlayer in World.Instance.Players)
            {
                currentPlayer.Connection.Send(string.Format("(Chat) {0}: {1}", _commandIssuer.PlayerName, (string)_commandArgs[0]));
            }
            return true;
        }

        // Move this to a separate class to handle command stuffs.
        public static bool IsInputValid(string input)
        {
            return input.ToLower().StartsWith(_commandKeyword.ToLower() + " ");
        }

        protected override object[] parseCommand(string input)
        {
            return new object[] { input.Substring(5) };
        }
    }
}