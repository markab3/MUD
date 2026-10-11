namespace MUD.Core
{
    public class WorldSettings
    {
        // Room key (Room _id) where new players start and which BuildCommand rooms exit to.
        public string StartRoomKey { get; set; }

        // Optional directory of area YAML files. When set, all *.yaml files in it are imported at startup.
        public string ContentDirectory { get; set; }

        // Whether to import ContentDirectory at startup. Defaults to true when a directory is configured.
        public bool ImportOnStartup { get; set; } = true;
    }
}