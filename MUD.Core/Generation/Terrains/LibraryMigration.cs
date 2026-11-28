using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MUD.Core.Areas;
using MUD.Data;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace MUD.Core.Generation.Terrains
{
    // Reads the area-relevant part of a map file: the generator block (prefix, name, description) and the legend template names.
    public static class MapAreaReader
    {
        // Null when the text is not a map file with a valid generator.prefix.
        public static MapAreaInfo Read(string mapYaml)
        {
            try
            {
                var stream = new YamlStream();
                stream.Load(new StringReader(mapYaml ?? string.Empty));
                var root = stream.Documents.Count == 0 ? null : stream.Documents[0].RootNode as YamlMappingNode;
                var gen = Child(root, "generator") as YamlMappingNode;
                string prefix = Scalar(gen, "prefix");
                if (prefix == null || RoomRules.CheckAreaPrefix(prefix) != null) { return null; }
                var info = new MapAreaInfo
                {
                    Prefix = prefix,
                    Name = RoomRules.NormalizeText(Scalar(gen, "name") ?? string.Empty),
                    Description = RoomRules.NormalizeText(Scalar(gen, "description") ?? string.Empty),
                };
                if (string.IsNullOrEmpty(info.Name)) { info.Name = AreaRules.ReadableName(prefix); }
                var legend = Child(root, "legend") as YamlMappingNode;
                if (legend != null)
                {
                    foreach (var pair in legend.Children)
                    {
                        string template = pair.Value is YamlScalarNode s ? s.Value : Scalar(pair.Value as YamlMappingNode, "template");
                        if (!string.IsNullOrEmpty(template) && !info.Terrains.Contains(template)) { info.Terrains.Add(template); }
                    }
                }
                return info;
            }
            catch (YamlException) { return null; }
        }

        // Every <contentDirectory>/maps/*.map.yaml, ordered by file name. Unreadable and invalid files are skipped. A prefix used by two
        // files is read once (the first file wins).
        public static List<MapAreaInfo> ReadDirectory(string contentDirectory)
        {
            var result = new List<MapAreaInfo>();
            if (string.IsNullOrWhiteSpace(contentDirectory)) { return result; }
            string dir = Path.Combine(contentDirectory, MapFileService.MapSubdirectory);
            if (!Directory.Exists(dir)) { return result; }
            foreach (string path in Directory.GetFiles(dir, "*.map.yaml").OrderBy(p => p, StringComparer.Ordinal))
            {
                MapAreaInfo info;
                try { info = Read(File.ReadAllText(path, Encoding.UTF8)); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if (info != null && result.All(r => r.Prefix != info.Prefix)) { result.Add(info); }
            }
            return result;
        }

        private static YamlNode Child(YamlMappingNode m, string key)
        {
            YamlNode n;
            return m != null && m.Children.TryGetValue(new YamlScalarNode(key), out n) ? n : null;
        }

        private static string Scalar(YamlMappingNode m, string key) { return (Child(m, key) as YamlScalarNode)?.Value; }
    }

    public sealed class LibraryMigrationResult
    {
        public int AreasCreated, TerrainTypesCreated;
        // Written documents: the original that now carries a terrain, plus the split copies created.
        public int Fragments, Features, Phrases;
        // Documents that were migrated (an original), and copies among the written ones.
        public int Migrated, Copies;
        // Legacy documents left untouched: no terrain listed, or no map file uses any listed name.
        public List<string> Unmigrated = new List<string>();
        public bool VersionBumped;
        public bool Changed { get { return Migrated > 0 || AreasCreated > 0 || TerrainTypesCreated > 0; } }
    }

    // Startup step next to the seeding: moves legacy library documents (a `terrains` list, no `terrain`) onto terrain types of the areas
    // whose map files use those names. Idempotent: a document that has a terrain is never read again; everything it writes is an insert that
    // refuses an existing id (areas, terrain types, copies), and the original is replaced last, so a crash half way is finished by the next run.
    public static class LibraryMigration
    {
        public static LibraryMigrationResult Run(ILibraryStore store, IAreaStore areas, IReadOnlyList<MapAreaInfo> maps, Action<string> log)
        {
            log = log ?? (_ => { });
            var result = new LibraryMigrationResult();
            if (!store.GetVersion().HasValue) { return result; }   // no library yet (nothing legacy can exist)

            var legacyFragments = store.Fragments.List().Where(IsLegacy).ToList();
            var legacyFeatures = store.Features.List().Where(IsLegacy).ToList();
            var legacyPhrases = store.Phrases.List().Where(IsLegacy).ToList();
            if (legacyFragments.Count + legacyFeatures.Count + legacyPhrases.Count == 0) { return result; }

            var ensured = new HashSet<string>(StringComparer.Ordinal);

            foreach (var f in legacyFragments)
            {
                Move(store.Fragments, f, "fragment", result, store, areas, maps, log, ensured, null, (copy, id, terrain) => { copy.Id = id; copy.Terrain = terrain; },
                    c => new DescriptionFragment { Slot = c.Slot, Text = c.Text, Weight = c.Weight, Tags = c.Tags.ToList(), LegacyTerrains = c.LegacyTerrains.ToList() }, n => result.Fragments += n);
            }
            foreach (var f in legacyFeatures)
            {
                Move(store.Features, f, "feature template", result, store, areas, maps, log, ensured, null, (copy, id, terrain) => { copy.Id = id; copy.Terrain = terrain; },
                    c => new FeatureTemplate
                    {
                        Short = c.Short, Long = c.Long, Handles = c.Handles.ToList(), Adjectives = c.Adjectives.ToList(), RoomLine = c.RoomLine, Detail = c.Detail,
                        Weight = c.Weight, Chance = c.Chance, Version = c.Version, Tags = c.Tags.ToList(), LegacyTerrains = c.LegacyTerrains.ToList(),
                    }, n => result.Features += n);
            }
            foreach (var p in legacyPhrases)
            {
                Move(store.Phrases, p, "neighbour phrase", result, store, areas, maps, log, ensured, p.NeighborTerrain,
                    (copy, id, terrain) => { copy.Id = id; copy.Terrain = terrain; copy.NeighborTerrain = TerrainTypeRules.IdFor(TerrainTypeRules.AreaOf(terrain), p.NeighborTerrain); },
                    c => new NeighborPhrase { Text = c.Text, Weight = c.Weight, Merge = c.Merge, LegacyTerrains = c.LegacyTerrains.ToList() }, n => result.Phrases += n);
            }

            if (result.Migrated > 0)
            {
                store.BumpVersion();   // once, so regen/update see a change
                result.VersionBumped = true;
            }
            log(string.Format("Terrain library migration: {0} fragment(s), {1} feature template(s), {2} neighbour phrase(s) written ({3} document(s) moved, {4} split cop{5}); " +
                "{6} area(s) and {7} terrain type(s) created; {8} left untouched{9}.",
                result.Fragments, result.Features, result.Phrases, result.Migrated, result.Copies, result.Copies == 1 ? "y" : "ies",
                result.AreasCreated, result.TerrainTypesCreated, result.Unmigrated.Count, result.VersionBumped ? "; library version bumped" : ""));
            return result;
        }

        // No `terrain` field and not scoped yet. (An entry with an empty legacy list is "legacy" too: it is reported, not migrated.)
        private static bool IsLegacy(ITerrainScoped e) { return string.IsNullOrEmpty(e.Terrain); }

        private static void Move<T>(ILibraryCollection<T> collection, T entity, string what, LibraryMigrationResult result, ILibraryStore store, IAreaStore areas,
            IReadOnlyList<MapAreaInfo> maps, Action<string> log, HashSet<string> ensured, string neighbor, Action<T, string, string> scope,
            Func<T, T> blank, Action<int> count) where T : class, ITerrainScoped
        {
            if (entity.LegacyTerrains == null || entity.LegacyTerrains.Count == 0)
            {
                result.Unmigrated.Add(entity.Id);
                log("Terrain library migration: " + what + " '" + entity.Id + "' lists no terrain; left untouched.");
                return;
            }

            // The (area, local) pairs, in the order of the legacy list; a name used by several maps gives one pair per area (maps by prefix).
            var targets = new List<MapAreaInfo>();
            var locals = new List<string>();
            foreach (string name in entity.LegacyTerrains)
            {
                foreach (var map in maps.Where(m => m.Terrains.Contains(name)).OrderBy(m => m.Prefix, StringComparer.Ordinal))
                {
                    // A phrase's neighbour must be a terrain of the same map; otherwise this copy cannot be written.
                    if (neighbor != null && !map.Terrains.Contains(neighbor))
                    {
                        log("Terrain library migration: " + what + " '" + entity.Id + "' not written for " + TerrainTypeRules.IdFor(map.Prefix, name) +
                            ": the neighbour terrain '" + neighbor + "' is not used by that map.");
                        continue;
                    }
                    bool listedTwice = false;
                    for (int i = 0; i < targets.Count; i++) { if (targets[i].Prefix == map.Prefix && locals[i] == name) { listedTwice = true; } }
                    if (listedTwice) { continue; }
                    targets.Add(map);
                    locals.Add(name);
                }
            }
            if (targets.Count == 0)
            {
                result.Unmigrated.Add(entity.Id);
                log("Terrain library migration: " + what + " '" + entity.Id + "' (" + string.Join(", ", entity.LegacyTerrains) + ") is used by no map file; left untouched.");
                return;
            }

            // Copies first, the original last (it is the one that makes the document "migrated").
            for (int i = 1; i < targets.Count; i++)
            {
                string terrain = EnsureTerrainType(store, areas, targets[i], locals[i], result, log, ensured);
                if (neighbor != null) { EnsureTerrainType(store, areas, targets[i], neighbor, result, log, ensured); }
                string id = CopyId(collection, entity.Id, locals[i], targets[i].Prefix);
                T copy = blank(entity);
                scope(copy, id, terrain);
                if (collection.Insert(copy, "migration")) { result.Copies++; count(1); log("Terrain library migration: " + what + " '" + entity.Id + "' copied to '" + id + "' on " + terrain + "."); }
            }
            string first = EnsureTerrainType(store, areas, targets[0], locals[0], result, log, ensured);
            if (neighbor != null) { EnsureTerrainType(store, areas, targets[0], neighbor, result, log, ensured); }
            scope(entity, entity.Id, first);
            if (collection.Replace(entity, "migration")) { result.Migrated++; count(1); log("Terrain library migration: " + what + " '" + entity.Id + "' moved to " + first + "."); }
        }

        // The id of a split copy: "<id>-<terrain local>"; when that id is taken by another entry, "<id>-<area>-<local>".
        private static string CopyId<T>(ILibraryCollection<T> collection, string id, string local, string area) where T : class, ITerrainScoped
        {
            string plain = id + "-" + local;
            var existing = collection.Get(plain);
            if (existing == null || SameOrigin(existing, id, local, area)) { return plain; }
            return id + "-" + area + "-" + local;
        }

        // A copy a previous (half finished) run already wrote for this origin.
        private static bool SameOrigin<T>(T existing, string id, string local, string area) where T : class, ITerrainScoped
        {
            return existing.Terrain == TerrainTypeRules.IdFor(area, local);
        }

        // The terrain type "<area>.<local>" (and its area) are created when missing; never overwritten.
        private static string EnsureTerrainType(ILibraryStore store, IAreaStore areas, MapAreaInfo map, string local, LibraryMigrationResult result, Action<string> log, HashSet<string> ensured)
        {
            string id = TerrainTypeRules.IdFor(map.Prefix, local);
            if (!ensured.Add(id)) { return id; }
            if (areas != null && areas.Insert(new AreaEntry { Id = map.Prefix, Name = map.Name, Description = map.Description }, "migration"))
            {
                result.AreasCreated++;
                log("Terrain library migration: area '" + map.Prefix + "' created from its map file.");
            }
            if (store.TerrainTypes.Insert(new TerrainType { Id = id, Name = AreaRules.ReadableName(local), Description = string.Empty }, "migration"))
            {
                result.TerrainTypesCreated++;
                log("Terrain library migration: terrain type '" + id + "' created.");
            }
            return id;
        }
    }
}
