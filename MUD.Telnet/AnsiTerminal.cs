using System.Collections.Generic;

namespace MUD.Telnet
{
    public class AnsiTerminalHandler : ITerminalHandler
    {
        private Dictionary<string, int> _formatOptions = new Dictionary<string, int>()
        {
            {"Reset", 0},
            {"Bold", 1},
            {"Underline", 4},
            {"Black", 30},
            {"Red", 31},
            {"Green", 32},
            {"Yellow", 33},
            {"Blue", 34},
            {"Magenta", 35},
            {"Cyan", 36},
            {"White", 37},
            {"BrightBlack", 90},
            {"BrightRed", 91},
            {"BrightGreen", 92},
            {"BrightYellow", 93},
            {"BrightBlue", 94},
            {"BrightMagenta", 95},
            {"BrightCyan", 96},
            {"BrightWhite", 97},
            {"BGBlack", 40},
            {"BGRed", 41},
            {"BGGreen", 42},
            {"BGYellow", 43},
            {"BGBlue", 44},
            {"BGMagenta", 45},
            {"BGCyan", 46},
            {"BGWhite", 47},
            {"BGBrightBlack", 100},
            {"BGBrightRed", 101},
            {"BGBrightGreen", 102},
            {"BGBrightYellow", 103},
            {"BGBrightBlue", 104},
            {"BGBrightMagenta", 105},
            {"BGBrightCyan", 106},
            {"BGBrightWhite", 107}
        };

        private const string _escape = "\x1B";

        public string TerminalName => "Ansi";

        public string[] Aliases => new string[] { "Mudlet" };

        public string GetReplacement(string tokenName)
        {
            return string.Format("{0}[{1}m", _escape, _formatOptions[tokenName]);
        }
    }
}