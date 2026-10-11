using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace MUD.Core.Import
{
    // Phase 1a of specs/ROOM_FILES.md: parse YAML into an AreaDefinition, collecting every
    // structural error (unknown keys, wrong types, missing fields, bad key grammar) rather
    // than stopping at the first. Defaults are resolved here.
    public class AreaParser
    {
        public static readonly Regex LocalKeyPattern = new Regex("^[a-z0-9_]+$");
        public static readonly Regex PrefixPattern = new Regex("^[a-z0-9_]+(\\.[a-z0-9_]+)*$");

        private readonly List<string> _errors = new List<string>();

        public static AreaParseResult Parse(string yaml)
        {
            return new AreaParser().ParseInternal(yaml);
        }

        private AreaParseResult ParseInternal(string yaml)
        {
            YamlMappingNode root;
            try
            {
                var stream = new YamlStream();
                stream.Load(new StringReader(yaml ?? ""));
                if (stream.Documents.Count == 0) { return Fail("File is empty."); }
                root = stream.Documents[0].RootNode as YamlMappingNode;
                if (root == null) { return Fail("Top level must be a mapping with 'area' and 'rooms'."); }
            }
            catch (YamlException ex)
            {
                return Fail(string.Format("YAML syntax error at line {0}: {1}", ex.Start.Line, ex.Message));
            }

            CheckKeys(root, new[] { "area", "rooms" }, "top level");
            var areaNode = Get(root, "area") as YamlMappingNode;
            var roomsNode = Get(root, "rooms") as YamlSequenceNode;
            if (areaNode == null) { _errors.Add("'area' block is required and must be a mapping."); }
            if (roomsNode == null) { _errors.Add("'rooms' is required and must be a list."); }
            if (areaNode == null || roomsNode == null) { return new AreaParseResult { Errors = _errors }; }

            var area = new AreaDefinition();
            int? defaultZ = null;
            ParseArea(areaNode, area, out defaultZ);

            int index = 0;
            foreach (var node in roomsNode.Children)
            {
                index++;
                var roomMap = node as YamlMappingNode;
                if (roomMap == null) { _errors.Add(string.Format("rooms[{0}] must be a mapping.", index)); continue; }
                var room = ParseRoom(roomMap, index, area.Prefix, defaultZ);
                if (room != null) { area.Rooms.Add(room); }
            }

            return new AreaParseResult { Area = _errors.Count == 0 ? area : null, Errors = _errors };
        }

        private AreaParseResult Fail(string message)
        {
            _errors.Add(message);
            return new AreaParseResult { Errors = _errors };
        }

        private void ParseArea(YamlMappingNode node, AreaDefinition area, out int? defaultZ)
        {
            defaultZ = null;
            CheckKeys(node, new[] { "schema_version", "prefix", "name", "description", "defaults" }, "area");

            var version = Scalar(node, "schema_version", "area", true);
            if (version != null && version != "1")
            {
                _errors.Add(string.Format("area.schema_version: unsupported value '{0}' (supported: 1).", version));
            }

            area.Prefix = Scalar(node, "prefix", "area", true);
            if (area.Prefix != null && !PrefixPattern.IsMatch(area.Prefix))
            {
                _errors.Add(string.Format("area.prefix '{0}' is not a valid key prefix (lowercase letters, digits, '_', segments joined by '.').", area.Prefix));
            }
            area.Name = Scalar(node, "name", "area", true);

            var defaults = Get(node, "defaults");
            if (defaults != null)
            {
                var defaultsMap = defaults as YamlMappingNode;
                if (defaultsMap == null) { _errors.Add("area.defaults must be a mapping."); return; }
                CheckKeys(defaultsMap, new[] { "coords", "properties" }, "area.defaults");
                var coords = Get(defaultsMap, "coords");
                if (coords != null)
                {
                    var coordsMap = coords as YamlMappingNode;
                    if (coordsMap == null) { _errors.Add("area.defaults.coords must be a mapping."); }
                    else
                    {
                        CheckKeys(coordsMap, new[] { "z" }, "area.defaults.coords");
                        defaultZ = Int(coordsMap, "z", "area.defaults.coords");
                    }
                }
                var props = Get(defaultsMap, "properties");
                if (props != null && !(props is YamlSequenceNode && ((YamlSequenceNode)props).Children.Count == 0))
                {
                    _errors.Add("area.defaults.properties: only an empty list is supported for now.");
                }
            }
        }

        private RoomDefinition ParseRoom(YamlMappingNode node, int index, string prefix, int? defaultZ)
        {
            var ctx = string.Format("rooms[{0}]", index);
            CheckKeys(node, new[] { "key", "short", "long", "handles", "adjectives", "plurals", "coords", "exits", "features", "properties", "class" }, ctx);

            var room = new RoomDefinition();
            room.LocalKey = Scalar(node, "key", ctx, true);
            if (room.LocalKey != null)
            {
                ctx = string.Format("room '{0}'", room.LocalKey);
                if (!LocalKeyPattern.IsMatch(room.LocalKey))
                {
                    _errors.Add(string.Format("{0}: key must match ^[a-z0-9_]+$.", ctx));
                }
                room.FullKey = prefix == null ? room.LocalKey : prefix + "." + room.LocalKey;
            }
            room.Short = Text(node, "short", ctx, true);
            room.Long = Text(node, "long", ctx, true);
            room.Handles = StringList(node, "handles", ctx);
            room.Adjectives = StringList(node, "adjectives", ctx);
            room.Plurals = StringList(node, "plurals", ctx);

            if (Get(node, "class") != null && Scalar(node, "class", ctx, false) != null)
            {
                _errors.Add(string.Format("{0}: 'class' is not supported by this importer yet.", ctx));
            }

            room.Z = defaultZ;
            var coords = Get(node, "coords");
            if (coords != null)
            {
                var coordsMap = coords as YamlMappingNode;
                if (coordsMap == null) { _errors.Add(ctx + ": coords must be a mapping."); }
                else
                {
                    CheckKeys(coordsMap, new[] { "x", "y", "z" }, ctx + ".coords");
                    room.X = Int(coordsMap, "x", ctx + ".coords");
                    room.Y = Int(coordsMap, "y", ctx + ".coords");
                    var z = Int(coordsMap, "z", ctx + ".coords");
                    if (z.HasValue) { room.Z = z; }
                }
            }

            foreach (var exitNode in Sequence(node, "exits", ctx))
            {
                var exitMap = exitNode as YamlMappingNode;
                if (exitMap == null) { _errors.Add(ctx + ": each exit must be a mapping."); continue; }
                CheckKeys(exitMap, new[] { "dir", "to", "reverse" }, ctx + ".exits");
                var exit = new ExitDefinition
                {
                    Dir = Scalar(exitMap, "dir", ctx + ".exits", true),
                    To = Scalar(exitMap, "to", ctx + ".exits", true),
                    Reverse = Scalar(exitMap, "reverse", ctx + ".exits", false)
                };
                if (exit.To != null && !exit.To.Contains(".") && prefix != null) { exit.To = prefix + "." + exit.To; }
                room.Exits.Add(exit);
            }

            foreach (var featureNode in Sequence(node, "features", ctx))
            {
                var featureMap = featureNode as YamlMappingNode;
                if (featureMap == null) { _errors.Add(ctx + ": each feature must be a mapping."); continue; }
                CheckKeys(featureMap, new[] { "short", "long", "handles", "adjectives", "plurals", "properties" }, ctx + ".features");
                var fctx = ctx + ".features";
                var feature = new FeatureDefinition
                {
                    Short = Text(featureMap, "short", fctx, true),
                    Long = Text(featureMap, "long", fctx, true),
                    Handles = StringList(featureMap, "handles", fctx),
                    Adjectives = StringList(featureMap, "adjectives", fctx),
                    Plurals = StringList(featureMap, "plurals", fctx)
                };
                feature.Properties = ParseProperties(featureMap, fctx);
                room.Features.Add(feature);
            }

            room.Properties = ParseProperties(node, ctx);
            return room;
        }

        private List<SearchPropertyDefinition> ParseProperties(YamlMappingNode owner, string ctx)
        {
            var result = new List<SearchPropertyDefinition>();
            foreach (var propNode in Sequence(owner, "properties", ctx))
            {
                var propMap = propNode as YamlMappingNode;
                if (propMap == null) { _errors.Add(ctx + ": each property must be a mapping."); continue; }
                var pctx = ctx + ".properties";
                CheckKeys(propMap, new[] { "behavior", "id", "params" }, pctx);
                var behavior = Scalar(propMap, "behavior", pctx, true);
                var id = Scalar(propMap, "id", pctx, true);
                if (behavior == null) { continue; }
                if (behavior != "search")
                {
                    _errors.Add(string.Format("{0}: unknown behavior '{1}' (registered: search).", pctx, behavior));
                    continue;
                }

                var prop = new SearchPropertyDefinition { Id = id };
                var paramsMap = Get(propMap, "params") as YamlMappingNode;
                if (paramsMap == null) { _errors.Add(pctx + ": 'params' is required and must be a mapping."); result.Add(prop); continue; }
                var sctx = pctx + " (search" + (id == null ? "" : " '" + id + "'") + ")";
                CheckKeys(paramsMap, new[] { "target", "refresh_ms", "reward" }, sctx);
                prop.Target = Scalar(paramsMap, "target", sctx, true);
                prop.RefreshMs = Int(paramsMap, "refresh_ms", sctx) ?? 0;
                if (Get(paramsMap, "refresh_ms") == null) { _errors.Add(sctx + ": 'refresh_ms' is required."); }
                else if (prop.RefreshMs < 0) { _errors.Add(sctx + ": 'refresh_ms' must not be negative."); }

                var rewardMap = Get(paramsMap, "reward") as YamlMappingNode;
                if (rewardMap == null) { _errors.Add(sctx + ": 'reward' is required and must be a mapping."); }
                else
                {
                    CheckKeys(rewardMap, new[] { "short", "long", "handles", "adjectives", "plurals" }, sctx + ".reward");
                    prop.Reward = new ItemDefinition
                    {
                        Short = Text(rewardMap, "short", sctx + ".reward", true),
                        Long = Text(rewardMap, "long", sctx + ".reward", true),
                        Handles = StringList(rewardMap, "handles", sctx + ".reward"),
                        Adjectives = StringList(rewardMap, "adjectives", sctx + ".reward"),
                        Plurals = StringList(rewardMap, "plurals", sctx + ".reward")
                    };
                }
                result.Add(prop);
            }
            return result;
        }

        // ---- helpers ----

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
            if (scalar == null) { _errors.Add(string.Format("{0}: '{1}' must be a scalar.", ctx, key)); return null; }
            if (scalar.Value == null || scalar.Value == "~" || scalar.Value == "null")
            {
                if (required) { _errors.Add(string.Format("{0}: '{1}' is required.", ctx, key)); }
                return null;
            }
            return scalar.Value;
        }

        private string Text(YamlMappingNode map, string key, string ctx, bool required)
        {
            var value = Scalar(map, key, ctx, required);
            return value == null ? null : NormalizeText(value);
        }

        // Spec section 11: trim trailing whitespace per line and overall, LF line endings.
        public static string NormalizeText(string value)
        {
            var lines = value.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').Select(l => l.TrimEnd());
            return string.Join("\n", lines).Trim();
        }

        private int? Int(YamlMappingNode map, string key, string ctx)
        {
            var value = Scalar(map, key, ctx, false);
            if (value == null) { return null; }
            int parsed;
            if (!int.TryParse(value, out parsed)) { _errors.Add(string.Format("{0}: '{1}' must be an integer.", ctx, key)); return null; }
            return parsed;
        }

        private List<string> StringList(YamlMappingNode map, string key, string ctx)
        {
            var result = new List<string>();
            foreach (var item in Sequence(map, key, ctx))
            {
                var scalar = item as YamlScalarNode;
                if (scalar == null || scalar.Value == null) { _errors.Add(string.Format("{0}: '{1}' must be a list of strings.", ctx, key)); continue; }
                result.Add(scalar.Value);
            }
            return result;
        }

        private IEnumerable<YamlNode> Sequence(YamlMappingNode map, string key, string ctx)
        {
            var node = Get(map, key);
            if (node == null) { return Enumerable.Empty<YamlNode>(); }
            var seq = node as YamlSequenceNode;
            if (seq == null)
            {
                var scalar = node as YamlScalarNode;
                if (scalar != null && (scalar.Value == null || scalar.Value == "~" || scalar.Value == "null")) { return Enumerable.Empty<YamlNode>(); }
                _errors.Add(string.Format("{0}: '{1}' must be a list.", ctx, key));
                return Enumerable.Empty<YamlNode>();
            }
            return seq.Children;
        }
    }

    public class AreaParseResult
    {
        public AreaDefinition Area { get; set; }

        public List<string> Errors { get; set; } = new List<string>();
    }
}
