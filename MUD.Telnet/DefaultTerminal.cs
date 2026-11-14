namespace MUD.Telnet
{
    public class DefaultTerminalHandler: ITerminalHandler
    {
        public string TerminalName => "Default";

        public string[] Aliases => null;

        public string GetReplacement(string tokenName)
        {
            return "";
        }
    }
}