namespace MUD.Core
{
    public class WorldSettings
    {
        // Room key (Room _id) where new players start and which BuildCommand rooms exit to.
        public string StartRoomKey { get; set; }

        // Name of the MongoDB database holding game data (WorldSettings:DatabaseName).
        public string DatabaseName { get; set; } = "testmud";

        // Optional directory of area YAML files. It is the source for the manual import command, and for startup import when ImportOnStartup is true.
        public string ContentDirectory { get; set; }

        // Whether to import ContentDirectory at startup. Defaults to false: the database is the source of truth and the area files are a manual, one-off import (the import creator command).
        public bool ImportOnStartup { get; set; } = false;

        // Where snapshots that could not be written at shutdown are kept. Defaults to ~/.local/share/mud/spool.
        public string SpoolDirectory { get; set; }
    }
}