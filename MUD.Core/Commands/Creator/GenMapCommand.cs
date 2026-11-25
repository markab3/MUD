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

        public string HelpText { get => "Generate an area file from an ASCII map. Reads <content directory>/maps/<map>.map.yaml and writes <content directory>/<prefix>.yaml, which 'import' then loads. Rooms are built from the terrain library (MongoDB) and carry generator provenance; the same map, seed and library give identical output. Then 'import <prefix>' regenerates every room of the map (see 'import').\r\n\r\nSyntax: genmap <map> [dry]"; }

        // Reads the terrain library (MongoDB) fresh on every run, so edits made through the admin API are used at once.
        // The library store is created on first use: a database that is down only fails genmap, not startup.
        public GenMapCommand(WorldSettings settings, MongoDB.Driver.IMongoDatabase database)
        {
            MUD.Core.Generation.Terrains.StoreLibrary library = null;
            _service = new MapFileService(settings.ContentDirectory, () =>
            {
                if (library == null)
                {
                    var store = new MUD.Core.Generation.Terrains.MongoLibraryStore(database);
                    if (!store.GetVersion().HasValue) { return null; }
                    library = new MUD.Core.Generation.Terrains.StoreLibrary(store);
                }
                library.Refresh();
                return library;
            });
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
