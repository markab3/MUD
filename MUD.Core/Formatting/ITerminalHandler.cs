using System;
using System.Linq;

namespace MUD.Core.Formatting
{
    public interface ITerminalHandler
    {
        string TerminalName { get; }

        string[] Aliases { get; }

        string GetReplacement(EFormatOptions option);

        string ResolveOutput(string outputString) {
            
            foreach (var currentOption in Enum.GetValues(typeof(EFormatOptions)).Cast<EFormatOptions>())
            {
                outputString = outputString.Replace(string.Format("%^{0}%^", currentOption.ToString()), GetReplacement(currentOption));
            }
            return outputString;
        }
    }
}