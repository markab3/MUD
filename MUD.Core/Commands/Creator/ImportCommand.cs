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

        public string HelpText { get => "Import an area file from the content directory. Only authored definition fields are overwritten; items and other runtime state are kept. A behavior entry in the file whose hook, module and type already exist on the stored room is reported and skipped; add 'replace' to replace it.\r\n\r\nSyntax: import <area> [dry] [replace]"; }

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
            var flags = parts.Skip(1).ToList();
            if (parts.Length == 0 || flags.Count > 2 || flags.Any(f => f != "dry" && f != "replace") || flags.Distinct().Count() != flags.Count)
            {
                commandIssuer.ReceiveMessage("Syntax: import <area> [dry] [replace]");
                return;
            }

            foreach (var line in _service.ImportArea(parts[0], flags.Contains("dry"), flags.Contains("replace")))
            {
                commandIssuer.ReceiveMessage(line);
            }
        }
    }
}
