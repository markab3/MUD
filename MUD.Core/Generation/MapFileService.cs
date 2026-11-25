using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MUD.Core.Generation.Terrains;
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
        private readonly Func<ILibrary> _libraryProvider;

        public MapFileService(string contentDirectory)
        {
            _contentDirectory = contentDirectory;
        }

        // libraryProvider is called once per run and must return a current snapshot of the terrain library, or null when
        // there is none (then nothing is generated: a library-less run would write rooms without provenance, which a
        // later re-import could not recognise as generated). It may throw when the database is unreachable.
        public MapFileService(string contentDirectory, Func<ILibrary> libraryProvider) : this(contentDirectory)
        {
            _libraryProvider = libraryProvider;
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

            GenerateResult result;
            if (_libraryProvider == null)
            {
                result = MapGenerator.Generate(File.ReadAllText(mapPath, Encoding.UTF8));
            }
            else
            {
                ILibrary library;
                try { library = _libraryProvider(); }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    lines.Add(mapName + ": the terrain library could not be read (" + ex.GetType().Name + "), nothing written.");
                    return lines;
                }
                if (library == null)
                {
                    lines.Add(mapName + ": the terrain library is not available, nothing written.");
                    return lines;
                }
                result = MapGenerator.Generate(File.ReadAllText(mapPath, Encoding.UTF8), mapName, library);
            }
            if (!result.Success)
            {
                lines.Add(mapName + ": map has errors, nothing written.");
                foreach (var error in result.Errors) { lines.Add("  error: " + error); }
                return lines;
            }

            foreach (var warning in result.Warnings) { lines.Add("  warning: " + warning); }
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
