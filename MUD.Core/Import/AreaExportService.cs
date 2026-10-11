using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MUD.Core.Import
{
    // Runs the exporter and reports results to the user. Area exports are written into the content
    // directory; room exports are returned as text and never written, so a single room cannot be
    // mistaken for a complete area file.
    public class AreaExportService
    {
        private readonly AreaExporter _exporter;
        private readonly string _contentDirectory;

        public AreaExportService(IRoomImportStore store, string contentDirectory)
        {
            _exporter = new AreaExporter(store);
            _contentDirectory = contentDirectory;
        }

        public bool IsConfigured { get { return !string.IsNullOrWhiteSpace(_contentDirectory); } }

        // Writes <contentDirectory>/<area>.yaml. Never overwrites an existing file.
        public List<string> ExportArea(string area)
        {
            var lines = new List<string>();
            if (!IsConfigured) { lines.Add("No content directory is configured (WorldSettings:ContentDirectory)."); return lines; }

            var result = _exporter.ExportArea(area);
            if (!result.Success) { return Failure(result); }

            var path = Path.Combine(_contentDirectory, area + ".yaml");
            if (File.Exists(path))
            {
                lines.Add("Not overwriting existing file: " + path + ". Move it aside and export again.");
                return lines;
            }

            Directory.CreateDirectory(_contentDirectory);
            File.WriteAllText(path, result.Yaml, new UTF8Encoding(false));
            lines.Add(string.Format("Exported {0} room(s) to {1}", result.RoomCount, path));
            return lines;
        }

        // Returns the single-room YAML as lines, or the reasons it cannot be exported.
        public List<string> ExportRoom(string roomId)
        {
            var result = _exporter.ExportRoom(roomId);
            if (!result.Success) { return Failure(result); }
            var lines = result.Yaml.Split('\n').ToList();
            if (lines.Count > 0 && lines[lines.Count - 1].Length == 0) { lines.RemoveAt(lines.Count - 1); }
            return lines;
        }

        private static List<string> Failure(AreaExportResult result)
        {
            var lines = new List<string> { "Export failed; nothing was written." };
            foreach (var error in result.Errors) { lines.Add("  error: " + error); }
            return lines;
        }
    }
}
