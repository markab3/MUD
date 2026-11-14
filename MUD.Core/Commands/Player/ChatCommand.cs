using MUD.Core.GameObjects;
using MUD.CoreApi;

namespace MUD.Core.Commands
{
    public class ChatCommand : ICommand
    {

        public string[] CommandKeywords { get => new string[] { "chat" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Default; }

        public string HelpText { get => "Send a message to all other players.\r\n \r\nSyntax: chat <message>"; }

        private World _world;

        public ChatCommand(World world)
        {
            _world = world;
        }

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

            foreach (Player currentPlayer in _world.Players)
            {
                currentPlayer.ReceiveMessage(string.Format("(Chat) {0}: {1}", ((Player)commandIssuer).PlayerName, text), OutputChannel.Chat);
            }
            if (shortened) { commandIssuer.ReceiveMessage(CommandExtensions.MessageShortenedNotice); }
        }
    }
}