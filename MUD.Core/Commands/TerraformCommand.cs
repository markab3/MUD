using System;
using System.Linq;

namespace MUD.Core.Commands
{
    public class TerraformCommand : ICommand
    {
        public string CommandKeyword { get => "terraform"; }

        public bool IsDefault { get => true; }

        public string HelpText { get => "Does a bunch of room stuff."; }

        private enum TerraformOption {
            CREATE,
            LINK,
            UNLINK,
            SETSHORT,
            SETLONG,
        }

        private TerraformOption? _terraformOption = null;

        public object[] ParseCommand(string input)
        {
            input = input.Replace(CommandKeyword, "").Trim();
            
            string[] inputArgs = input.Split(" ");            

            if (inputArgs != null && inputArgs.Length > 0) { 
                TerraformOption matchedOption;
                if (Enum.TryParse(inputArgs[0], out matchedOption)) {
                    _terraformOption = matchedOption;
                    inputArgs = inputArgs.Skip(1).ToArray();
                }
            }
            return inputArgs;
        }

        public void DoCommand(Player commandIssuer, object[] commandArgs)
        {          
            if (_terraformOption == null) {
                // No bueno.
            }

            switch(_terraformOption) {
                case TerraformOption.LINK:
                    // Link this with another room.
                    break;

            }
        }
    }
}