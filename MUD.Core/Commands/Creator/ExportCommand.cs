using System.Linq;
using MUD.Core.GameObjects;
using MUD.Core.Import;
using MUD.Core.Properties.PlayerProperties;
using MUD.Core.Repositories.Interfaces;

namespace MUD.Core.Commands
{
    public class ExportCommand : ICommand
    {
        private readonly AreaExportService _service;

        public ExportCommand(IRoomRepository roomRepository, WorldSettings settings)
        {
            _service = new AreaExportService(new RoomRepositoryImportStore(roomRepository), settings.ContentDirectory);
        }

        public string[] CommandKeywords { get => new string[] { "export" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Creator; }

        public string HelpText { get => "Export database rooms to area YAML (the format 'import' reads). Only definition fields are written; items and other runtime state are not.\r\n\r\nSyntax: export area <prefix>   writes <content directory>/<prefix>.yaml\r\n        export room <full key>   shows one room's YAML (not written to a file)"; }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return new object[] { this.StripKeyword(input) }; ;
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
            if (parts.Length != 2 || (parts[0] != "area" && parts[0] != "room"))
            {
                commandIssuer.ReceiveMessage("Syntax: export area <prefix>  |  export room <full key>");
                return;
            }

            var lines = parts[0] == "area" ? _service.ExportArea(parts[1]) : _service.ExportRoom(parts[1]);
            foreach (var line in lines)
            {
                commandIssuer.ReceiveMessage(line);
            }
        }
    }
}