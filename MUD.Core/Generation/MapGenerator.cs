using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;
using MUD.Core.Import;

namespace MUD.Core.Generation
{
    public class GenerateResult
    {
        public List<string> Errors { get; set; } = new List<string>();

        // The area file text (specs/ROOM_FILES.md format). Null when Errors is not empty.
        public string Yaml { get; set; }

        public string Prefix { get; set; }

        public int RoomCount { get; set; }

        public bool Success { get { return Errors.Count == 0; } }
    }

    // Turns an ASCII map file into an area file (specs/ROOM_FILES.md, section 12 generator contract).
    //
    // Map file (all top-level keys required unless noted):
    //   generator:  prefix, name, seed (string), description (opt), origin: {x, y} (opt, default 0/0), z (opt)
    //   grid:       list of strings, or a block scalar. First line is the NORTH edge.
    //   legend:     glyph -> template name. Glyphs absent from the legend (and spaces) make no room.
    //   templates:  name -> {walkable (default true), short, long, handles, adjectives, plurals}
    //                 short: a string, or a list of strings (pool; one is picked)
    //                 long:  a list of pools; one fragment is picked from each pool and the picks are
    //                        joined with a space. A pool is a list of strings, or a bare string.
    //   overrides:  (opt) list of {at: {x, y}, template, short, long, handles, adjectives, plurals,
    //                 features, properties}. short/long replace the generated text; features and
    //                 properties are copied verbatim into the room (they are validated by the importer).
    //
    // Coordinates: x grows east, y grows north. The bottom-left cell is `origin` (default 0,0), so adding
    // rows on top or columns on the right never changes existing keys. Key = <prefix>.x<XX>y<YY>
    // (at least two digits, e.g. city.x12y07; negative values render as n<abs>, e.g. x-3 -> xn03).
    public class MapGenerator
    {
        public const string GeneratorName = "mapgen";
        public const int GeneratorVersion = 1;

        private static readonly string[] OverrideKeys = { "at", "template", "short", "long", "handles", "adjectives", "plurals", "features", "properties" };
        private static readonly string[] TemplateKeys = { "walkable", "short", "long", "handles", "adjectives", "plurals" };
        private static readonly string[] GeneratorKeys = { "prefix", "name", "description", "seed", "origin", "z" };

        private class Template
        {
            public string Name;
            public bool Walkable = true;
            public List<string> Short = new List<string>();
            public List<List<string>> Long = new List<List<string>>();
            public List<string> Handles = new List<string>();
            public List<string> Adjectives = new List<string>();
            public List<string> Plurals = new List<string>();
        }

        private class Cell
        {
            public int X, Y;
            public Template Template;
            public YamlMappingNode Override;
            public string Key;
        }

        private readonly List<string> _errors = new List<string>();

        public static GenerateResult Generate(string mapYaml)
        {
            return new MapGenerator().GenerateInternal(mapYaml);
        }

        public static string KeyFor(int x, int y)
        {
            return "x" + Coord(x) + "y" + Coord(y);
        }

        private static string Coord(int value)
        {
            return (value < 0 ? "n" : "") + Math.Abs(value).ToString("D2");
        }

        private GenerateResult GenerateInternal(string mapYaml)
        {
            var result = new GenerateResult();
            YamlMappingNode root;
            try
            {
                var stream = new YamlStream();
                stream.Load(new StringReader(mapYaml ?? ""));
                root = stream.Documents.Count == 0 ? null : stream.Documents[0].RootNode as YamlMappingNode;
            }
            catch (YamlException ex)
            {
                result.Errors.Add(string.Format("YAML syntax error at line {0}: {1}", ex.Start.Line, ex.Message));
                return result;
            }
            if (root == null) { result.Errors.Add("Map file must be a mapping."); return result; }

            CheckKeys(root, new[] { "generator", "grid", "legend", "templates", "overrides" }, "top level");
            var gen = Get(root, "generator") as YamlMappingNode;
            if (gen == null) { _errors.Add("'generator' block is required and must be a mapping."); }
            string prefix = null, name = null, description = null, seed = null;
            int originX = 0, originY = 0;
            int? z = null;
            if (gen != null)
            {
                CheckKeys(gen, GeneratorKeys, "generator");
                prefix = Scalar(gen, "prefix", "generator", true);
                if (prefix != null && !AreaParser.PrefixPattern.IsMatch(prefix))
                {
                    _errors.Add(string.Format("generator.prefix '{0}' is not a valid key prefix.", prefix));
                }
                name = Scalar(gen, "name", "generator", true);
                description = Scalar(gen, "description", "generator", false);
                seed = Scalar(gen, "seed", "generator", true);
                var originNode = Get(gen, "origin");
                if (originNode != null)
                {
                    var om = originNode as YamlMappingNode;
                    if (om == null) { _errors.Add("generator.origin must be a mapping with x and y."); }
                    else
                    {
                        CheckKeys(om, new[] { "x", "y" }, "generator.origin");
                        originX = Int(om, "x", "generator.origin") ?? 0;
                        originY = Int(om, "y", "generator.origin") ?? 0;
                    }
                }
                z = Int(gen, "z", "generator");
            }

            var templates = ParseTemplates(Get(root, "templates") as YamlMappingNode);
            var legend = ParseLegend(Get(root, "legend") as YamlMappingNode, templates);
            var rows = ParseGrid(Get(root, "grid"));
            var overrides = ParseOverrides(Get(root, "overrides"));

            if (_errors.Count > 0) { result.Errors = _errors; return result; }

            // Build cells. Row 0 of the grid is the north edge.
            int height = rows.Count;
            var cells = new Dictionary<Tuple<int, int>, Cell>();
            for (int r = 0; r < height; r++)
            {
                for (int c = 0; c < rows[r].Length; c++)
                {
                    char glyph = rows[r][c];
                    if (glyph == ' ') { continue; }
                    Template template;
                    if (!legend.TryGetValue(glyph, out template))
                    {
                        _errors.Add(string.Format("grid: glyph '{0}' at column {1}, row {2} is not in the legend.", glyph, c, r));
                        continue;
                    }
                    var x = originX + c;
                    var y = originY + (height - 1 - r);
                    cells[Tuple.Create(x, y)] = new Cell { X = x, Y = y, Template = template };
                }
            }

            foreach (var ov in overrides)
            {
                var at = (YamlMappingNode)Get(ov.Value, "at");
                var ax = Int(at, "x", "overrides");
                var ay = Int(at, "y", "overrides");
                if (ax == null || ay == null) { continue; }
                var pos = Tuple.Create(ax.Value, ay.Value);
                var tname = Scalar(ov.Value, "template", "overrides[" + ov.Key + "]", false);
                Template over = null;
                if (tname != null && !templates.TryGetValue(tname, out over))
                {
                    _errors.Add(string.Format("overrides[{0}]: unknown template '{1}'.", ov.Key, tname));
                    continue;
                }
                Cell cell;
                if (!cells.TryGetValue(pos, out cell))
                {
                    if (over == null)
                    {
                        _errors.Add(string.Format("overrides[{0}]: no cell at x={1}, y={2} (give a template to create one).", ov.Key, ax, ay));
                        continue;
                    }
                    cell = new Cell { X = ax.Value, Y = ay.Value };
                    cells[pos] = cell;
                }
                if (over != null) { cell.Template = over; }
                if (cell.Override != null)
                {
                    _errors.Add(string.Format("overrides[{0}]: more than one override for x={1}, y={2}.", ov.Key, ax, ay));
                    continue;
                }
                cell.Override = ov.Value;
            }
            if (_errors.Count > 0) { result.Errors = _errors; return result; }

            var rooms = cells.Values.Where(c => c.Template.Walkable).ToList();
            foreach (var c in rooms) { c.Key = KeyFor(c.X, c.Y); }
            rooms = rooms.OrderByDescending(c => c.Y).ThenBy(c => c.X).ToList();
            var walkable = new HashSet<Tuple<int, int>>(rooms.Select(c => Tuple.Create(c.X, c.Y)));

            // Build the output document.
            var area = new YamlMappingNode();
            area.Add("schema_version", new YamlScalarNode("1"));
            area.Add("prefix", Str(prefix));
            area.Add("name", Str(name));
            if (description != null) { area.Add("description", Str(description)); }
            if (z != null)
            {
                var defaults = new YamlMappingNode();
                var dz = new YamlMappingNode();
                dz.Add("z", new YamlScalarNode(z.Value.ToString()));
                defaults.Add("coords", dz);
                area.Add("defaults", defaults);
            }
            var xgen = new YamlMappingNode();
            xgen.Add("name", Str(GeneratorName));
            xgen.Add("version", new YamlScalarNode(GeneratorVersion.ToString()));
            xgen.Add("seed", Str(seed));
            area.Add("x-generator", xgen);

            var roomSeq = new YamlSequenceNode();
            foreach (var cell in rooms)
            {
                roomSeq.Add(BuildRoom(cell, prefix, seed, walkable));
            }

            var doc = new YamlMappingNode();
            doc.Add("area", area);
            doc.Add("rooms", roomSeq);

            if (_errors.Count > 0) { result.Errors = _errors; return result; }

            var sb = new StringBuilder();
            sb.Append("# Generated by ").Append(GeneratorName).Append(" from an ASCII map. Do not hand-edit; change the map and regenerate.\n");
            using (var writer = new StringWriter(sb) { NewLine = "\n" })
            {
                new YamlStream(new YamlDocument(doc)).Save(writer, false);
            }
            var yaml = sb.ToString();

            // Generator contract: output must pass importer phase-1 parsing unmodified.
            var parsed = AreaParser.Parse(yaml);
            if (parsed.Errors.Count > 0)
            {
                result.Errors.AddRange(parsed.Errors.Select(e => "generated output is invalid: " + e));
                return result;
            }

            result.Yaml = yaml;
            result.Prefix = prefix;
            result.RoomCount = rooms.Count;
            return result;
        }

        private YamlMappingNode BuildRoom(Cell cell, string prefix, string seed, HashSet<Tuple<int, int>> walkable)
        {
            var rng = new SeededRandom(seed + "\n" + prefix + "\n" + cell.Key);
            var t = cell.Template;
            var ov = cell.Override;

            // Draw in a fixed order regardless of overrides so a cell's text never depends on its neighbours.
            var shortText = t.Short.Count == 0 ? null : t.Short[rng.Next(t.Short.Count)];
            var picks = new List<string>();
            foreach (var pool in t.Long) { picks.Add(pool[rng.Next(pool.Count)]); }
            var longText = string.Join(" ", picks);

            var ctx = "room " + cell.Key;
            var ovShort = ov == null ? null : Text(ov, "short", ctx, false);
            var ovLong = ov == null ? null : Text(ov, "long", ctx, false);
            shortText = ovShort ?? shortText;
            longText = ovLong ?? longText;
            if (string.IsNullOrWhiteSpace(shortText)) { _errors.Add(string.Format("{0}: template '{1}' gives no short description.", ctx, t.Name)); shortText = "?"; }
            if (string.IsNullOrWhiteSpace(longText)) { _errors.Add(string.Format("{0}: template '{1}' gives no long description.", ctx, t.Name)); longText = "?"; }

            var room = new YamlMappingNode();
            room.Add("key", Str(cell.Key));
            room.Add("short", Str(shortText));
            room.Add("long", new YamlScalarNode(longText) { Style = ScalarStyle.Literal });
            AddList(room, "handles", ov != null && Get(ov, "handles") != null ? StringList(ov, "handles", ctx) : t.Handles);
            AddList(room, "adjectives", ov != null && Get(ov, "adjectives") != null ? StringList(ov, "adjectives", ctx) : t.Adjectives);
            AddList(room, "plurals", ov != null && Get(ov, "plurals") != null ? StringList(ov, "plurals", ctx) : t.Plurals);

            var coords = new YamlMappingNode { Style = MappingStyle.Flow };
            coords.Add("x", new YamlScalarNode(cell.X.ToString()));
            coords.Add("y", new YamlScalarNode(cell.Y.ToString()));
            room.Add("coords", coords);

            // Exits to walkable orthogonal neighbours, declared on both sides so symmetry is explicit.
            var exits = new YamlSequenceNode();
            foreach (var d in Directions)
            {
                if (!walkable.Contains(Tuple.Create(cell.X + d.Dx, cell.Y + d.Dy))) { continue; }
                var exit = new YamlMappingNode { Style = MappingStyle.Flow };
                exit.Add("dir", new YamlScalarNode(d.Name));
                exit.Add("to", new YamlScalarNode(KeyFor(cell.X + d.Dx, cell.Y + d.Dy)));
                exits.Add(exit);
            }
            if (exits.Children.Count > 0) { room.Add("exits", exits); }

            if (ov != null)
            {
                var features = Get(ov, "features");
                if (features != null) { room.Add("features", features); }
                var properties = Get(ov, "properties");
                if (properties != null) { room.Add("properties", properties); }
            }
            return room;
        }

        private class Direction
        {
            public string Name;
            public int Dx, Dy;
        }

        private static readonly Direction[] Directions =
        {
            new Direction { Name = "north", Dx = 0, Dy = 1 },
            new Direction { Name = "east", Dx = 1, Dy = 0 },
            new Direction { Name = "south", Dx = 0, Dy = -1 },
            new Direction { Name = "west", Dx = -1, Dy = 0 },
        };

        // ---- parsing ----

        private Dictionary<string, Template> ParseTemplates(YamlMappingNode node)
        {
            var map = new Dictionary<string, Template>();
            if (node == null) { _errors.Add("'templates' is required and must be a mapping."); return map; }
            foreach (var pair in node.Children)
            {
                var name = (pair.Key as YamlScalarNode)?.Value;
                var body = pair.Value as YamlMappingNode;
                if (name == null || body == null) { _errors.Add("templates: each template must be a name mapped to a mapping."); continue; }
                var ctx = "template '" + name + "'";
                CheckKeys(body, TemplateKeys, ctx);
                var t = new Template { Name = name };
                var walk = Scalar(body, "walkable", ctx, false);
                if (walk != null)
                {
                    bool b;
                    if (!bool.TryParse(walk, out b)) { _errors.Add(ctx + ": walkable must be true or false."); }
                    t.Walkable = b;
                }
                t.Short = Pool(Get(body, "short"), ctx + ".short");
                var longNode = Get(body, "long");
                if (longNode != null)
                {
                    var seq = longNode as YamlSequenceNode;
                    if (seq == null) { _errors.Add(ctx + ".long must be a list of fragment pools."); }
                    else
                    {
                        int i = 0;
                        foreach (var poolNode in seq.Children)
                        {
                            var pool = Pool(poolNode, string.Format("{0}.long[{1}]", ctx, i++));
                            if (pool.Count > 0) { t.Long.Add(pool); }
                        }
                    }
                }
                t.Handles = StringList(body, "handles", ctx);
                t.Adjectives = StringList(body, "adjectives", ctx);
                t.Plurals = StringList(body, "plurals", ctx);
                if (t.Walkable && (t.Short.Count == 0 || t.Long.Count == 0))
                {
                    _errors.Add(ctx + ": a walkable template needs 'short' and 'long' (or every cell must override them).");
                }
                map[name] = t;
            }
            return map;
        }

        private List<string> Pool(YamlNode node, string ctx)
        {
            var pool = new List<string>();
            if (node == null) { return pool; }
            var scalar = node as YamlScalarNode;
            if (scalar != null) { pool.Add(Normalize(scalar.Value)); return pool; }
            var seq = node as YamlSequenceNode;
            if (seq == null) { _errors.Add(ctx + " must be a string or a list of strings."); return pool; }
            foreach (var child in seq.Children)
            {
                var s = child as YamlScalarNode;
                if (s == null) { _errors.Add(ctx + " must contain only strings."); continue; }
                pool.Add(Normalize(s.Value));
            }
            if (pool.Count == 0) { _errors.Add(ctx + " must not be empty."); }
            return pool;
        }

        private Dictionary<char, Template> ParseLegend(YamlMappingNode node, Dictionary<string, Template> templates)
        {
            var legend = new Dictionary<char, Template>();
            if (node == null) { _errors.Add("'legend' is required and must be a mapping."); return legend; }
            foreach (var pair in node.Children)
            {
                var glyph = (pair.Key as YamlScalarNode)?.Value;
                var tname = (pair.Value as YamlScalarNode)?.Value;
                if (glyph == null || glyph.Length != 1) { _errors.Add(string.Format("legend: glyph '{0}' must be a single character.", glyph)); continue; }
                if (glyph == " ") { _errors.Add("legend: space is reserved for 'no room'."); continue; }
                Template t;
                if (tname == null || !templates.TryGetValue(tname, out t)) { _errors.Add(string.Format("legend: glyph '{0}' names unknown template '{1}'.", glyph, tname)); continue; }
                legend[glyph[0]] = t;
            }
            return legend;
        }

        private List<string> ParseGrid(YamlNode node)
        {
            var lines = new List<string>();
            if (node == null) { _errors.Add("'grid' is required."); return lines; }
            var scalar = node as YamlScalarNode;
            var seq = node as YamlSequenceNode;
            if (scalar != null)
            {
                lines.AddRange(scalar.Value.Replace("\r\n", "\n").Split('\n'));
            }
            else if (seq != null)
            {
                foreach (var child in seq.Children)
                {
                    var s = child as YamlScalarNode;
                    if (s == null) { _errors.Add("grid: every row must be a string."); continue; }
                    lines.Add(s.Value ?? "");
                }
            }
            else { _errors.Add("grid must be a list of strings or a block scalar."); return lines; }

            lines = lines.Select(l => l.TrimEnd()).ToList();
            while (lines.Count > 0 && lines[0].Length == 0) { lines.RemoveAt(0); }
            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) { lines.RemoveAt(lines.Count - 1); }
            if (lines.Count == 0) { _errors.Add("grid is empty."); }
            if (lines.Any(l => l.Contains('\t'))) { _errors.Add("grid must not contain tabs."); }
            return lines;
        }

        private List<KeyValuePair<int, YamlMappingNode>> ParseOverrides(YamlNode node)
        {
            var list = new List<KeyValuePair<int, YamlMappingNode>>();
            if (node == null) { return list; }
            var seq = node as YamlSequenceNode;
            if (seq == null) { _errors.Add("overrides must be a list."); return list; }
            int i = 0;
            foreach (var child in seq.Children)
            {
                i++;
                var map = child as YamlMappingNode;
                if (map == null) { _errors.Add(string.Format("overrides[{0}] must be a mapping.", i)); continue; }
                CheckKeys(map, OverrideKeys, string.Format("overrides[{0}]", i));
                var at = Get(map, "at") as YamlMappingNode;
                if (at == null) { _errors.Add(string.Format("overrides[{0}]: 'at' with x and y is required.", i)); continue; }
                CheckKeys(at, new[] { "x", "y" }, string.Format("overrides[{0}].at", i));
                list.Add(new KeyValuePair<int, YamlMappingNode>(i, map));
            }
            return list;
        }

        // ---- helpers ----

        private static YamlScalarNode Str(string value)
        {
            return new YamlScalarNode(value) { Style = ScalarStyle.DoubleQuoted };
        }

        private static void AddList(YamlMappingNode room, string key, List<string> values)
        {
            if (values == null || values.Count == 0) { return; }
            room.Add(key, new YamlSequenceNode(values.Select(v => (YamlNode)Str(v))) { Style = SequenceStyle.Flow });
        }

        private static YamlNode Get(YamlMappingNode map, string key)
        {
            foreach (var pair in map.Children)
            {
                if ((pair.Key as YamlScalarNode)?.Value == key) { return pair.Value; }
            }
            return null;
        }

        private void CheckKeys(YamlMappingNode map, string[] allowed, string ctx)
        {
            foreach (var pair in map.Children)
            {
                var key = (pair.Key as YamlScalarNode)?.Value;
                if (key == null) { _errors.Add(ctx + ": non-scalar key."); continue; }
                if (key.StartsWith("x-")) { continue; }
                if (!allowed.Contains(key)) { _errors.Add(string.Format("{0}: unknown key '{1}'.", ctx, key)); }
            }
        }

        private string Scalar(YamlMappingNode map, string key, string ctx, bool required)
        {
            var node = Get(map, key);
            if (node == null)
            {
                if (required) { _errors.Add(string.Format("{0}: '{1}' is required.", ctx, key)); }
                return null;
            }
            var scalar = node as YamlScalarNode;
            if (scalar == null || scalar.Value == null) { _errors.Add(string.Format("{0}: '{1}' must be a scalar.", ctx, key)); return null; }
            return scalar.Value;
        }

        private string Text(YamlMappingNode map, string key, string ctx, bool required)
        {
            var value = Scalar(map, key, ctx, required);
            return value == null ? null : Normalize(value);
        }

        private static string Normalize(string value)
        {
            return AreaParser.NormalizeText(value ?? "");
        }

        private int? Int(YamlMappingNode map, string key, string ctx)
        {
            var value = Scalar(map, key, ctx, false);
            if (value == null) { return null; }
            int i;
            if (!int.TryParse(value, out i)) { _errors.Add(string.Format("{0}: '{1}' must be an integer.", ctx, key)); return null; }
            return i;
        }

        private List<string> StringList(YamlMappingNode map, string key, string ctx)
        {
            var list = new List<string>();
            var node = Get(map, key);
            if (node == null) { return list; }
            var seq = node as YamlSequenceNode;
            if (seq == null) { _errors.Add(string.Format("{0}: '{1}' must be a list of strings.", ctx, key)); return list; }
            foreach (var child in seq.Children)
            {
                var s = child as YamlScalarNode;
                if (s == null) { _errors.Add(string.Format("{0}: '{1}' must contain only strings.", ctx, key)); continue; }
                list.Add(s.Value);
            }
            return list;
        }
    }

    // Small, fully specified PRNG (FNV-1a 64 seeding + splitmix64) so output is stable across
    // runtimes and framework versions; System.Random makes no such promise.
    public class SeededRandom
    {
        private ulong _state;

        public SeededRandom(string seed)
        {
            ulong h = 14695981039346656037UL;
            foreach (var b in Encoding.UTF8.GetBytes(seed ?? ""))
            {
                h ^= b;
                unchecked { h *= 1099511628211UL; }
            }
            _state = h;
        }

        public ulong NextUInt64()
        {
            unchecked
            {
                _state += 0x9E3779B97F4A7C15UL;
                var z = _state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        // Uniform-enough index in [0, count).
        public int Next(int count)
        {
            return (int)(NextUInt64() % (ulong)count);
        }
    }
}
