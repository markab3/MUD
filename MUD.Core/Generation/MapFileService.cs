using System.Collections.Generic;
using System.IO;
using System.Text;
using MUD.Core.Import;

namespace MUD.Core.Generation
{
    // Reads <contentDirectory>/maps/<name>.map.yaml and writes the generated area file to
    // <contentDirectory>/<prefix>.yaml. Map sources live in a subdirectory on purpose: the startup
    // importer only reads *.yaml directly inside the content directory, so map files are never
    // mistaken for area files.
    public class MapFileService
    {
        public const string MapSubdirectory = "maps";

        private readonly string _contentDirectory;

        public MapFileService(string contentDirectory)
        {
            _contentDirectory = contentDirectory;
        }

        public bool IsConfigured { get { return !string.IsNullOrWhiteSpace(_contentDirectory); } }

        public List<string> Generate(string mapName, bool dryRun)
        {
            var lines = new List<string>();
            if (!IsConfigured) { lines.Add("No content directory is configured (WorldSettings:ContentDirectory)."); return lines; }
            if (string.IsNullOrWhiteSpace(mapName) || !AreaParser.PrefixPattern.IsMatch(mapName))
            {
                lines.Add("Invalid map name. Use the file name without extension, e.g. 'sample_city'.");
                return lines;
            }

            var mapPath = Path.Combine(_contentDirectory, MapSubdirectory, mapName + ".map.yaml");
            if (!File.Exists(mapPath)) { lines.Add("No such map file: " + mapPath); return lines; }

            var result = MapGenerator.Generate(File.ReadAllText(mapPath, Encoding.UTF8));
            if (!result.Success)
            {
                lines.Add(mapName + ": map has errors, nothing written.");
                foreach (var error in result.Errors) { lines.Add("  error: " + error); }
                return lines;
            }

            var outPath = Path.Combine(_contentDirectory, result.Prefix + ".yaml");
            if (dryRun)
            {
                lines.Add(string.Format("{0}: would write {1} rooms to {2} (dry run).", mapName, result.RoomCount, outPath));
                return lines;
            }

            // Skip the write when identical, so file timestamps (and file-copy build steps) stay quiet.
            var current = File.Exists(outPath) ? File.ReadAllText(outPath, Encoding.UTF8) : null;
            if (current == result.Yaml)
            {
                lines.Add(string.Format("{0}: {1} unchanged ({2} rooms).", mapName, outPath, result.RoomCount));
            }
            else
            {
                File.WriteAllText(outPath, result.Yaml, new UTF8Encoding(false));
                lines.Add(string.Format("{0}: wrote {1} rooms to {2}. Run 'import {3}' to load them.", mapName, result.RoomCount, outPath, result.Prefix));
            }
            return lines;
        }
    }
}
