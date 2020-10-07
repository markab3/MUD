namespace MUD.Core.Commands
{
    public class SaveCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "save" }; }

        public bool IsDefault { get => true; }

        public string HelpText { get => "Saves your precious player information."; }

        public object[] ParseCommand(Player commandIssuer, string input)
        {
            return null;
        }

        public void DoCommand(Player commandIssuer, object[] commandArgs)
        {
            commandIssuer.Save();
        }
    }
}