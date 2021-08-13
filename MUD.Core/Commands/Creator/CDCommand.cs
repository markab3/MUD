using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class CDCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "cd" }; }

        public bool IsDefault { get => true; }

        public string HelpText { get => "Change your current directory."; }

        public object[] ParseCommand(Player commandIssuer, string input)
        {
            return new object[] { this.StripKeyword(input) }; ;
        }

        public void DoCommand(Player commandIssuer, object[] commandArgs)
        {
            if (!(commandIssuer is Creator))
            {
                commandIssuer.ReceiveMessage("This is a creator only command. How did you get this?!");
                return;
            }

            var creatorIssuer = ((Creator)commandIssuer);
            if (commandArgs == null || commandArgs.Length == 0 || !(commandArgs[0] is string) || string.IsNullOrWhiteSpace((string)commandArgs[0]))
            {
                commandIssuer.ReceiveMessage(creatorIssuer.CurrentDirectory);
            }
            else
            {
                creatorIssuer.CurrentDirectory = (string)commandArgs[0];
                commandIssuer.ReceiveMessage(creatorIssuer.CurrentDirectory + ">");
            }
        }
    }
}