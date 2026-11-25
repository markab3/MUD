using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace MUD.Core.Generation.Terrains
{
    public sealed class LibrarySeedData
    {
        public int LibraryVersion = 1;
        public List<DescriptionFragment> Fragments = new List<DescriptionFragment>();
        public List<FeatureTemplate> Features = new List<FeatureTemplate>();
        public List<NeighborPhrase> Phrases = new List<NeighborPhrase>();
    }

    public sealed class LibrarySeedResult
    {
        public bool Seeded;
        public bool AlreadySeeded;
        public List<string> Errors = new List<string>();
        public List<string> Warnings = new List<string>();
        public int Fragments, Features, Phrases;
    }

    // Seed file (section 6.2): parse, validate, and load only on a fresh install.
    public static class LibrarySeed
    {
        public const string ResourceName = "terrain-library.yaml";
        public const int SchemaVersion = 1;

        // The seed file shipped inside MUD.Core (embedded resource, Q3 default), or null if the resource is missing.
        public static string ReadEmbedded()
        {
            Assembly assembly = typeof(LibrarySeed).Assembly;
            string name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(ResourceName, StringComparison.Ordinal));
            if (name == null) { return null; }
            using (var reader = new StreamReader(assembly.GetManifestResourceStream(name)))
            {
                return reader.ReadToEnd();
            }
        }

        // Seeds only when the meta document does not exist (so admin edits survive restarts, and a library emptied on purpose is not refilled).
        // On a validation failure nothing is written. A store exception propagates (the caller logs it and carries on without a library).
        public static LibrarySeedResult SeedIfFresh(ILibraryStore store, string yaml)
        {
            var result = new LibrarySeedResult();
            if (store.GetVersion().HasValue) { result.AlreadySeeded = true; return result; }
            if (yaml == null) { result.Errors.Add("The seed file is missing."); return result; }

            LibrarySeedData data = Parse(yaml, result.Errors, result.Warnings);
            if (data == null || result.Errors.Count > 0) { return result; }

            foreach (var f in data.Fragments) { store.Fragments.Insert(f, "seed"); }
            foreach (var f in data.Features) { store.Features.Insert(f, "seed"); }
            foreach (var p in data.Phrases) { store.Phrases.Insert(p, "seed"); }
            // The meta document goes last: if the process dies half way, the next start finds no meta and seeds again;
            // the duplicate-id inserts are refused (Insert returns false) so the load is idempotent.
            store.InitMeta(data.LibraryVersion);
            result.Seeded = true;
            result.Fragments = data.Fragments.Count;
            result.Features = data.Features.Count;
            result.Phrases = data.Phrases.Count;
            return result;
        }

        // Returns null on a YAML syntax error. Otherwise the data; check errors for validation failures.
        public static LibrarySeedData Parse(string yaml, List<string> errors, List<string> warnings)
        {
            YamlMappingNode root;
            try
            {
                var stream = new YamlStream();
                stream.Load(new StringReader(yaml));
                root = stream.Documents.Count == 0 ? null : stream.Documents[0].RootNode as YamlMappingNode;
            }
            catch (YamlException ex)
            {
                errors.Add("The seed file is not valid YAML (line " + ex.Start.Line + ").");
                return null;
            }
            if (root == null) { errors.Add("The seed file must be a mapping."); return null; }

            var data = new LibrarySeedData();
            var known = new HashSet<string> { "schema_version", "library_version", "fragments", "features", "neighborPhrases" };
            foreach (var pair in root.Children)
            {
                string key = Scalar(pair.Key);
                if (key == null || (!known.Contains(key) && !key.StartsWith("x-", StringComparison.Ordinal))) { errors.Add("Unknown top-level key '" + key + "'."); }
            }
            int schema;
            if (!Int(root, "schema_version", out schema) || schema != SchemaVersion) { errors.Add("schema_version must be " + SchemaVersion + "."); }
            if (Has(root, "library_version"))
            {
                int lv;
                if (!Int(root, "library_version", out lv) || lv < 1) { errors.Add("library_version must be an integer of 1 or more."); } else { data.LibraryVersion = lv; }
            }

            var fragmentKeys = new[] { "id", "terrains", "slot", "text", "weight", "tags" };
            var featureKeys = new[] { "id", "terrains", "short", "long", "handles", "adjectives", "roomLine", "detail", "weight", "chance", "version", "tags" };
            var phraseKeys = new[] { "id", "terrains", "neighborTerrain", "text", "weight", "merge" };

            foreach (var n in Items(root, "fragments", errors))
            {
                var m = Entry(n, "fragment", fragmentKeys, errors); if (m == null) { continue; }
                var f = new DescriptionFragment { Id = Str(m, "id"), Terrains = List(m, "terrains"), Slot = Str(m, "slot"), Text = Str(m, "text"), Tags = List(m, "tags") };
                Weight(m, errors, w => f.Weight = w);
                Collect(errors, "fragment", f.Id, LibraryValidation.Validate(f));
                data.Fragments.Add(f);
            }
            foreach (var n in Items(root, "features", errors))
            {
                var m = Entry(n, "feature", featureKeys, errors); if (m == null) { continue; }
                var f = new FeatureTemplate
                {
                    Id = Str(m, "id"), Terrains = List(m, "terrains"), Short = Str(m, "short"), Long = Str(m, "long"), Handles = List(m, "handles"),
                    Adjectives = List(m, "adjectives"), RoomLine = Str(m, "roomLine"), Detail = Str(m, "detail"), Tags = List(m, "tags"),
                };
                Weight(m, errors, w => f.Weight = w);
                if (Has(m, "chance"))
                {
                    double c;
                    if (!double.TryParse(Str(m, "chance"), NumberStyles.Float, CultureInfo.InvariantCulture, out c)) { errors.Add("feature '" + f.Id + "': chance must be a number."); } else { f.Chance = c; }
                }
                if (Has(m, "version"))
                {
                    int v;
                    if (!Int(m, "version", out v)) { errors.Add("feature '" + f.Id + "': version must be an integer."); } else { f.Version = v; }
                }
                Collect(errors, "feature", f.Id, LibraryValidation.Validate(f));
                data.Features.Add(f);
            }
            foreach (var n in Items(root, "neighborPhrases", errors))
            {
                var m = Entry(n, "neighbour phrase", phraseKeys, errors); if (m == null) { continue; }
                var p = new NeighborPhrase { Id = Str(m, "id"), Terrains = List(m, "terrains"), NeighborTerrain = Str(m, "neighborTerrain"), Text = Str(m, "text") };
                Weight(m, errors, w => p.Weight = w);
                if (Has(m, "merge"))
                {
                    bool b;
                    if (!bool.TryParse(Str(m, "merge"), out b)) { errors.Add("neighbour phrase '" + p.Id + "': merge must be true or false."); } else { p.Merge = b; }
                }
                Collect(errors, "neighbour phrase", p.Id, LibraryValidation.Validate(p));
                data.Phrases.Add(p);
            }

            Unique(errors, "fragment", data.Fragments.Select(f => f.Id));
            Unique(errors, "feature", data.Features.Select(f => f.Id));
            Unique(errors, "neighbour phrase", data.Phrases.Select(f => f.Id));
            return data;
        }

        private static void Unique(List<string> errors, string what, IEnumerable<string> ids)
        {
            foreach (var g in ids.Where(i => i != null).GroupBy(i => i).Where(g => g.Count() > 1)) { errors.Add("Duplicate " + what + " id '" + g.Key + "'."); }
        }

        private static void Collect(List<string> errors, string what, string id, List<LibraryError> found)
        {
            foreach (var e in found) { errors.Add(what + " '" + id + "': " + e.Field + ": " + e.Message); }
        }

        private static IEnumerable<YamlNode> Items(YamlMappingNode root, string key, List<string> errors)
        {
            YamlNode node;
            if (!root.Children.TryGetValue(new YamlScalarNode(key), out node)) { return new YamlNode[0]; }
            var seq = node as YamlSequenceNode;
            if (seq == null)
            {
                if (node is YamlScalarNode && string.IsNullOrEmpty(((YamlScalarNode)node).Value)) { return new YamlNode[0]; }
                errors.Add("'" + key + "' must be a list.");
                return new YamlNode[0];
            }
            return seq.Children;
        }

        private static YamlMappingNode Entry(YamlNode node, string what, string[] keys, List<string> errors)
        {
            var m = node as YamlMappingNode;
            if (m == null) { errors.Add("Every " + what + " must be a mapping."); return null; }
            foreach (var pair in m.Children)
            {
                string key = Scalar(pair.Key);
                if (key == null || (!keys.Contains(key) && !key.StartsWith("x-", StringComparison.Ordinal))) { errors.Add("Unknown " + what + " key '" + key + "'."); }
            }
            return m;
        }

        private static string Scalar(YamlNode node) { var s = node as YamlScalarNode; return s == null ? null : s.Value; }
        private static bool Has(YamlMappingNode m, string key) { return m.Children.ContainsKey(new YamlScalarNode(key)); }
        private static string Str(YamlMappingNode m, string key)
        {
            YamlNode n;
            if (!m.Children.TryGetValue(new YamlScalarNode(key), out n)) { return null; }
            return Scalar(n);
        }

        private static bool Int(YamlMappingNode m, string key, out int value)
        {
            value = 0;
            string s = Str(m, key);
            return s != null && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static void Weight(YamlMappingNode m, List<string> errors, Action<int> set)
        {
            if (!Has(m, "weight")) { return; }
            int w;
            if (!Int(m, "weight", out w)) { errors.Add("weight must be an integer (entry '" + Str(m, "id") + "')."); } else { set(w); }
        }

        private static List<string> List(YamlMappingNode m, string key)
        {
            var result = new List<string>();
            YamlNode n;
            if (!m.Children.TryGetValue(new YamlScalarNode(key), out n)) { return result; }
            var seq = n as YamlSequenceNode;
            if (seq != null) { foreach (var c in seq.Children) { result.Add(Scalar(c) ?? string.Empty); } }
            return result;
        }
    }
}
