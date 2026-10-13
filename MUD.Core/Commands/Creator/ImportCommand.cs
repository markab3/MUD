using System.Linq;
using MUD.Core.GameObjects;
using MUD.Core.Import;
using MUD.Core.Properties.PlayerProperties;
using MUD.Core.Repositories.Interfaces;

namespace MUD.Core.Commands
{
    public class ImportCommand : ICommand
    {
        private readonly AreaFileService _service;

        public string[] CommandKeywords { get => new string[] { "import" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Creator; }

        public string HelpText { get => "Import an area file from the content directory. Only authored definition fields are overwritten; items and other runtime state are kept.\r\n\r\nSyntax: import <area> [dry]"; }

        public ImportCommand(IRoomRepository roomRepository, WorldSettings settings)
        {
            _service = new AreaFileService(new AreaImporter(new RoomRepositoryImportStore(roomRepository)), settings.ContentDirectory);
        }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return new object[] { this.StripKeyword(input) };
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            CreatorProperty creatorStatusObj = (CreatorProperty)commandIssuer.ExtendedProperties.FirstOrDefault(s => s.GetType() == typeof(CreatorProperty));
            if (creatorStatusObj == null)
            {
                commandIssuer.ReceiveMessage("This is a creator only command. How did you get this?!");
                return;
            }

            var args = (commandArgs != null && commandArgs.Length > 0 ? commandArgs[0] as string : null) ?? "";
            var parts = args.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts.Length > 2 || (parts.Length == 2 && parts[1] != "dry"))
            {
                commandIssuer.ReceiveMessage("Syntax: import <area> [dry]");
                return;
            }

            foreach (var line in _service.ImportArea(parts[0], parts.Length == 2))
            {
                commandIssuer.ReceiveMessage(line);
            }
        }
    }
}
