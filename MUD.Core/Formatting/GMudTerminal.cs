using System.Collections.Generic;

namespace MUD.Core.Formatting
{
    public class GMudTerminalHandler : ITerminalHandler
    {
        private Dictionary<EFormatOptions, int> _formatOptions = new Dictionary<EFormatOptions, int>()
        {
            {EFormatOptions.Reset, 0},
            {EFormatOptions.Bold, 1},
            {EFormatOptions.Underline, 4},
            {EFormatOptions.Black, 30},
            {EFormatOptions.Red, 31},
            {EFormatOptions.Green, 32},
            {EFormatOptions.Yellow, 33},
            {EFormatOptions.Blue, 34},
            {EFormatOptions.Magenta, 35},
            {EFormatOptions.Cyan, 36},
            {EFormatOptions.White, 37},
            {EFormatOptions.BrightBlack, 90},
            {EFormatOptions.BrightRed, 91},
            {EFormatOptions.BrightGreen, 92},
            {EFormatOptions.BrightYellow, 93},
            {EFormatOptions.BrightBlue, 94},
            {EFormatOptions.BrightMagenta, 95},
            {EFormatOptions.BrightCyan, 96},
            {EFormatOptions.BrightWhite, 97},        
            {EFormatOptions.BGBlack, 40},
            {EFormatOptions.BGRed, 41},
            {EFormatOptions.BGGreen, 42},
            {EFormatOptions.BGYellow, 43},
            {EFormatOptions.BGBlue, 44},
            {EFormatOptions.BGMagenta, 45},
            {EFormatOptions.BGCyan, 46},
            {EFormatOptions.BGWhite, 47},
            {EFormatOptions.BGBrightBlack, 100},
            {EFormatOptions.BGBrightRed, 101},
            {EFormatOptions.BGBrightGreen, 102},
            {EFormatOptions.BGBrightYellow, 103},
            {EFormatOptions.BGBrightBlue, 104},
            {EFormatOptions.BGBrightMagenta, 105},
            {EFormatOptions.BGBrightCyan, 106},
            {EFormatOptions.BGBrightWhite, 107}
        };

        private const string _escape = "\x1B";

        public string TerminalName => "GMud";

        public string[] Aliases => null;

        public string GetReplacement(EFormatOptions option)
        {
            return string.Format("{0}[{1}m", _escape, _formatOptions[option]);
        }
    }
}