using System.Linq;
using MUD.Core.GameObjects;
using MUD.Core.Generation;
using MUD.Core.Components;

namespace MUD.Core.Commands
{
    public class GenMapCommand : ICommand
    {
        private readonly MapFileService _service;

        public string[] CommandKeywords { get => new string[] { "genmap" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Creator; }

        public string HelpText { get => "Generate an area file from an ASCII map. Reads <content directory>/maps/<map>.map.yaml and writes <content directory>/<prefix>.yaml, which 'import' then loads. Regenerating the same map gives identical output.\r\n\r\nSyntax: genmap <map> [dry]"; }

        public GenMapCommand(WorldSettings settings)
        {
            _service = new MapFileService(settings.ContentDirectory);
        }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return new object[] { this.StripKeyword(input) };
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            Roles creatorStatusObj = commandIssuer.Roles.Creator ? commandIssuer.Roles : null;
            if (creatorStatusObj == null)
            {
                commandIssuer.ReceiveMessage("This is a creator only command. How did you get this?!");
                return;
            }

            var args = (commandArgs != null && commandArgs.Length > 0 ? commandArgs[0] as string : null) ?? "";
            var parts = args.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts.Length > 2 || (parts.Length == 2 && parts[1] != "dry"))
            {
                commandIssuer.ReceiveMessage("Syntax: genmap <map> [dry]");
                return;
            }

            foreach (var line in _service.Generate(parts[0], parts.Length == 2))
            {
                commandIssuer.ReceiveMessage(line);
            }
        }
    }
}
