namespace MUD.Core.Commands
{
    public class SaveCommand : ICommand
    {
        public string CommandKeyword { get => "save"; }

        public bool IsDefault { get => true; }

        public string HelpText { get => "Saves your precious player information."; }

        public object[] ParseCommand(string input)
        {
            return null;
        }

        public void DoCommand(Player commandIssuer, object[] commandArgs)
        {
            commandIssuer.Save();
        }
    }
}