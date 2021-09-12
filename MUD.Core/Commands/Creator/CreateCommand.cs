using System.Linq;
using MUD.Core.GameObjects;
using MUD.Core.Properties.PlayerProperties;
using System.CommandLine;
using System.CommandLine.Invocation;

namespace MUD.Core.Commands
{
    public class CreateCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "create" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Creator; }

        public string HelpText { get => "Create an item of a specific type.\r\n\r\nSyntax: create <object type> [options]"; }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return new object[] { this.StripKeyword(input).Split(" ") }; ;
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            CreatorProperty creatorStatusObj = (CreatorProperty)commandIssuer.ExtendedProperties.FirstOrDefault(s => s.GetType() == typeof(CreatorProperty));
            if (creatorStatusObj == null)
            {
                commandIssuer.ReceiveMessage("This is a creator only command. How did you get this?!");
                return;
            }

            var cmd = new RootCommand
            {
                new Argument<string>("objectType", "The type of the object to create."),
                new Option<string>(new[] {"--shortDescription", "-s"}, "Short description of the new item."),
                new Option<string>(new[] {"--longDescription", "-l"}, "Long description of the new item."),
                //new Option<string?>(new[] {"--handles", "-h"}, "Handles for the new item."),
            };
            cmd.Handler = CommandHandler.Create<string, string, string>(CreateObject);

            cmd.Invoke(commandArgs.Select(o => (string)o).ToArray());
        }

        private void CreateObject(string objectType, string shortDescription, string longDescription)
        {

        }
    }
}