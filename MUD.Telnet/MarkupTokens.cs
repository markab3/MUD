namespace MUD.Telnet
{
    /// <summary>
    /// The names of the %^Token%^ markup tokens, in the order core's EFormatOptions declares them. This project cannot
    /// reference core, so the names are listed here; a test checks the list against the enum.
    /// </summary>
    public static class MarkupTokens
    {
        public static readonly string[] Names = new string[]
        {
            "Reset", "Bold", "Underline",
            "Black", "Red", "Green", "Yellow", "Blue", "Magenta", "Cyan", "White",
            "BrightBlack", "BrightRed", "BrightGreen", "BrightYellow", "BrightBlue", "BrightMagenta", "BrightCyan", "BrightWhite",
            "BGBlack", "BGRed", "BGGreen", "BGYellow", "BGBlue", "BGMagenta", "BGCyan", "BGWhite",
            "BGBrightBlack", "BGBrightRed", "BGBrightGreen", "BGBrightYellow", "BGBrightBlue", "BGBrightMagenta", "BGBrightCyan", "BGBrightWhite"
        };
    }
}
