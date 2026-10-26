namespace MUD.Core
{
    public class WorldSettings
    {
        // Room key (Room _id) where new players start and which BuildCommand rooms exit to.
        public string StartRoomKey { get; set; }

        // Name of the MongoDB database holding game data (WorldSettings:DatabaseName).
        public string DatabaseName { get; set; } = "testmud";

        // Optional directory of area YAML files. When set, all *.yaml files in it are imported at startup.
        public string ContentDirectory { get; set; }

        // Whether to import ContentDirectory at startup. Defaults to true when a directory is configured.
        public bool ImportOnStartup { get; set; } = true;

        // Where snapshots that could not be written at shutdown are kept. Defaults to ~/.local/share/mud/spool.
        public string SpoolDirectory { get; set; }
    }
}