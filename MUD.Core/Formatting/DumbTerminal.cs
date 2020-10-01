using System.Collections.Generic;

namespace MUD.Core.Formatting
{
    public class DefaultTerminalHandler: ITerminalHandler
    {
        public string TerminalName => "Default";

        public string[] Aliases => null;

        public string GetReplacement(EFormatOptions option)
        {
            return "";
        }
    }
}