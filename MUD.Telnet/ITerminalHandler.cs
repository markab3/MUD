namespace MUD.Telnet
{
    /// <summary>
    /// Turns the %^Token%^ markup that core emits into the text one kind of terminal understands.
    /// The token names are the contract with core (the names of core's EFormatOptions values; a test keeps the two in step).
    /// </summary>
    public interface ITerminalHandler
    {
        string TerminalName { get; }

        string[] Aliases { get; }

        /// <summary>The text that replaces the token with the given name.</summary>
        string GetReplacement(string tokenName);

        string ResolveOutput(string outputString) {

            foreach (var tokenName in MarkupTokens.Names)
            {
                outputString = outputString.Replace(string.Format("%^{0}%^", tokenName), GetReplacement(tokenName));
            }
            return outputString;
        }
    }
}