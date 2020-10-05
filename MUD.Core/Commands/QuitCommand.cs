namespace MUD.Core.Commands
{
    public class QuitCommand : ICommand
    {

        public string[] CommandKeywords { get => new string[] { "quit" }; }

        public bool IsDefault { get => true; }

        public string HelpText { get => "Quits the game."; }

        public object[] ParseCommand(string input)
        {
            return null;
        }

        public void DoCommand(Player commandIssuer, object[] commandArgs)
        {
            commandIssuer.Quit();
        }
    }
}