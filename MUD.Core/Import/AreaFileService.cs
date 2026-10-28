using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MUD.Core.Import
{
    // Locates area files in the configured content directory and runs the importer on them.
    public class AreaFileService
    {
        private readonly AreaImporter _importer;
        private readonly string _contentDirectory;

        public AreaFileService(AreaImporter importer, string contentDirectory)
        {
            _importer = importer;
            _contentDirectory = contentDirectory;
        }

        public bool IsConfigured { get { return !string.IsNullOrWhiteSpace(_contentDirectory); } }

        // Imports <contentDirectory>/<area>.yaml. Returns lines suitable for showing to a user.
        public List<string> ImportArea(string area, bool dryRun, bool replaceBehaviors = false)
        {
            var lines = new List<string>();
            if (!IsConfigured) { lines.Add("No content directory is configured (WorldSettings:ContentDirectory)."); return lines; }
            if (string.IsNullOrWhiteSpace(area) || !AreaParser.PrefixPattern.IsMatch(area))
            {
                lines.Add("Invalid area name. Use the area prefix, e.g. 'tower_of_aellioc'.");
                return lines;
            }

            var path = Path.Combine(_contentDirectory, area + ".yaml");
            if (!File.Exists(path)) { lines.Add("No such area file: " + path); return lines; }

            var result = _importer.Import(File.ReadAllText(path, Encoding.UTF8), dryRun, replaceBehaviors);
            Describe(area, result, lines);
            return lines;
        }

        // Imports every *.yaml in the content directory. Files are independent; a failing file does not stop the rest.
        public List<string> ImportAll()
        {
            var lines = new List<string>();
            if (!IsConfigured || !Directory.Exists(_contentDirectory)) { return lines; }
            foreach (var path in Directory.GetFiles(_contentDirectory, "*.yaml").OrderBy(p => p, StringComparer.Ordinal))
            {
                var area = Path.GetFileNameWithoutExtension(path);
                var result = _importer.Import(File.ReadAllText(path, Encoding.UTF8), false);
                Describe(area, result, lines);
            }
            return lines;
        }

        private static void Describe(string area, ImportResult result, List<string> lines)
        {
            lines.Add(area + ": " + result.Summary());
            foreach (var error in result.Errors) { lines.Add("  error: " + error); }
            if (result.Success)
            {
                foreach (var plan in result.Plan.Where(p => p.Action != ImportAction.Unchanged))
                {
                    lines.Add(string.Format("  {0} {1}{2}", plan.Action.ToString().ToLowerInvariant(), plan.Key,
                        plan.Action == ImportAction.Update ? " (" + string.Join(", ", plan.ChangedFields) + ")" : ""));
                }
                foreach (var orphan in result.Orphans) { lines.Add("  orphaned (kept): " + orphan); }
                foreach (var skipped in result.SkippedBehaviors) { lines.Add("  " + skipped + " Use 'import <area> replace' to replace stored entries."); }
                foreach (var replaced in result.ReplacedBehaviors) { lines.Add("  " + replaced); }
            }
        }
    }
}
