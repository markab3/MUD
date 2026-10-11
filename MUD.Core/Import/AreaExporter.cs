using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MongoDB.Bson;

namespace MUD.Core.Import
{
    public class AreaExportResult
    {
        public string Yaml { get; set; }

        public int RoomCount { get; set; }

        public List<string> Errors { get; set; } = new List<string>();

        public bool Success { get { return Errors.Count == 0; } }
    }

    // The inverse of AreaImporter (specs/ROOM_FILES.md section 4). Reads stored rooms, keeps only
    // definition fields (section 5), and writes an area file. Items and property runtime state are
    // never written. The output is checked with a dry-run import before it is returned, so a file the
    // importer would reject is never produced.
    public class AreaExporter
    {
        // Identifiers written without quotes when unambiguous (keys, prefixes, exit names, room keys).
        private static readonly Regex PlainScalarPattern = new Regex("^[a-z0-9_.]+$");

        private readonly IRoomImportStore _store;

        public AreaExporter(IRoomImportStore store)
        {
            _store = store;
        }

        // Every room whose Area is the given prefix, as one area file.
        public AreaExportResult ExportArea(string prefix)
        {
            var result = new AreaExportResult();
            if (string.IsNullOrEmpty(prefix) || !AreaParser.PrefixPattern.IsMatch(prefix))
            {
                result.Errors.Add(string.Format("'{0}' is not a valid area prefix.", prefix));
                return result;
            }

            var ids = _store.FindIdsByArea(prefix).OrderBy(id => id, StringComparer.Ordinal).ToList();
            if (ids.Count == 0)
            {
                result.Errors.Add(string.Format("No rooms belong to area '{0}'.", prefix));
                return result;
            }

            var area = NewArea(prefix);
            foreach (var id in ids)
            {
                var doc = _store.Find(id);
                if (doc == null)
                {
                    result.Errors.Add(string.Format("Room '{0}' disappeared while exporting.", id));
                    continue;
                }
                var room = BuildRoom(doc, prefix, result.Errors);
                if (room != null) { area.Rooms.Add(room); }
            }
            return Finish(area, result);
        }

        // One room as a single-room area file. The room must be keyed '<Area>.<key>'.
        public AreaExportResult ExportRoom(string roomId)
        {
            var result = new AreaExportResult();
            var doc = _store.Find(roomId);
            if (doc == null)
            {
                result.Errors.Add(string.Format("No room with id '{0}'.", roomId));
                return result;
            }

            var prefix = Str(doc, "Area");
            if (string.IsNullOrEmpty(prefix) || !AreaParser.PrefixPattern.IsMatch(prefix))
            {
                result.Errors.Add(string.Format("Room '{0}' has no valid Area, so it cannot be exported to an area file.", roomId));
                return result;
            }

            var area = NewArea(prefix);
            var room = BuildRoom(doc, prefix, result.Errors);
            if (room != null) { area.Rooms.Add(room); }
            return Finish(area, result);
        }

        private AreaExportResult Finish(AreaDefinition area, AreaExportResult result)
        {
            if (!result.Success) { return result; }

            var yaml = Serialize(area);

            // The same checks a real import makes (duplicates, dangling exits, ownership), without writing.
            var check = new AreaImporter(_store).Import(yaml, dryRun: true);
            foreach (var error in check.Errors) { result.Errors.Add("Export would not import: " + error); }

            if (result.Success)
            {
                result.Yaml = yaml;
                result.RoomCount = area.Rooms.Count;
            }
            return result;
        }

        // The area name is not stored in the database, so the prefix is used as a placeholder.
        private static AreaDefinition NewArea(string prefix)
        {
            return new AreaDefinition { Prefix = prefix, Name = prefix };
        }

        private static RoomDefinition BuildRoom(BsonDocument doc, string prefix, List<string> errors)
        {
            var id = Str(doc, "_id");
            if (id == null || !id.StartsWith(prefix + ".", StringComparison.Ordinal) || !AreaParser.LocalKeyPattern.IsMatch(id.Substring(prefix.Length + 1)))
            {
                errors.Add(string.Format("Room '{0}' is not keyed as '{1}.<key>' (lowercase letters, digits and '_'), so it cannot be exported to area '{1}'.", id, prefix));
                return null;
            }

            var ctx = string.Format("Room '{0}'", id);
            var room = new RoomDefinition
            {
                LocalKey = id.Substring(prefix.Length + 1),
                FullKey = id,
                Short = Str(doc, "ShortDescription"),
                Long = Str(doc, "LongDescription"),
                Handles = StrList(doc, "Handles", ctx, errors),
                Adjectives = StrList(doc, "Adjectives", ctx, errors),
                Plurals = StrList(doc, "Plurals", ctx, errors),
                X = Int(doc, "X"),
                Y = Int(doc, "Y"),
                Z = Int(doc, "Z")
            };
            if (room.Short == null) { errors.Add(ctx + ": ShortDescription is required."); }
            if (room.Long == null) { errors.Add(ctx + ": LongDescription is required."); }

            foreach (var exitDoc in Docs(doc, "Exits", ctx, errors))
            {
                var dir = Str(exitDoc, "Name");
                var to = Str(exitDoc, "DestinationId");
                if (dir == null) { errors.Add(ctx + ": an exit has no Name."); }
                if (to == null || !to.Contains(".") || !AreaParser.PrefixPattern.IsMatch(to))
                {
                    errors.Add(string.Format("{0}: exit '{1}' leads to '{2}', which is not a room key a file can reference.", ctx, dir, to));
                }
                room.Exits.Add(new ExitDefinition { Dir = dir, To = to });
            }

            foreach (var featureDoc in Docs(doc, "Features", ctx, errors))
            {
                var fctx = string.Format("{0}, feature '{1}'", ctx, Str(featureDoc, "ShortDescription"));
                var feature = new FeatureDefinition
                {
                    Short = Str(featureDoc, "ShortDescription"),
                    Long = Str(featureDoc, "LongDescription"),
                    Handles = StrList(featureDoc, "Handles", fctx, errors),
                    Adjectives = StrList(featureDoc, "Adjectives", fctx, errors),
                    Plurals = StrList(featureDoc, "Plurals", fctx, errors),
                    Properties = BuildProperties(featureDoc, fctx, errors)
                };
                if (feature.Short == null) { errors.Add(fctx + ": ShortDescription is required."); }
                if (feature.Long == null) { errors.Add(fctx + ": LongDescription is required."); }
                room.Features.Add(feature);
            }

            room.Properties = BuildProperties(doc, ctx, errors);
            return room;
        }

        private static List<SearchPropertyDefinition> BuildProperties(BsonDocument owner, string ctx, List<string> errors)
        {
            var result = new List<SearchPropertyDefinition>();
            foreach (var propDoc in Docs(owner, "ExtendedProperties", ctx, errors))
            {
                var id = Str(propDoc, "_id");
                var pctx = string.Format("{0}, property '{1}'", ctx, id);
                if (id == null) { errors.Add(ctx + ": a property has no _id."); }

                // 'search' (BasicSearchProperty) is the only room-file behavior. Anything else cannot be
                // written, and would be removed from the room by the next import. RefreshInterval is an int,
                // so it is always stored; SearchTarget may be null.
                if (!propDoc.Contains("RefreshInterval"))
                {
                    errors.Add(pctx + ": not a room-file behavior, so it cannot be exported (only 'search' is supported).");
                    continue;
                }

                var prop = new SearchPropertyDefinition { Id = id, Target = Str(propDoc, "SearchTarget") };
                if (prop.Target == null) { errors.Add(pctx + ": SearchTarget is missing."); }

                var refresh = Int(propDoc, "RefreshInterval");
                if (refresh == null) { errors.Add(pctx + ": RefreshInterval is missing."); }
                else { prop.RefreshMs = refresh.Value; }

                var reward = propDoc.GetValue("Reward", BsonNull.Value);
                if (reward.IsBsonDocument) { prop.Reward = BuildItem(reward.AsBsonDocument, pctx + ", reward", errors); }
                else { errors.Add(pctx + ": Reward is missing."); }

                result.Add(prop);
            }
            return result;
        }

        private static ItemDefinition BuildItem(BsonDocument doc, string ctx, List<string> errors)
        {
            var item = new ItemDefinition
            {
                Short = Str(doc, "ShortDescription"),
                Long = Str(doc, "LongDescription"),
                Handles = StrList(doc, "Handles", ctx, errors),
                Adjectives = StrList(doc, "Adjectives", ctx, errors),
                Plurals = StrList(doc, "Plurals", ctx, errors)
            };
            if (item.Short == null) { errors.Add(ctx + ": ShortDescription is required."); }
            if (item.Long == null) { errors.Add(ctx + ": LongDescription is required."); }
            return item;
        }

        // ---- stored value readers ----

        private static string Str(BsonDocument doc, string name)
        {
            var value = doc.GetValue(name, BsonNull.Value);
            return value.IsString ? value.AsString : null;
        }

        private static int? Int(BsonDocument doc, string name)
        {
            var value = doc.GetValue(name, BsonNull.Value);
            if (value.IsInt32) { return value.AsInt32; }
            if (value.IsInt64) { return (int)value.AsInt64; }
            return null;
        }

        private static List<string> StrList(BsonDocument doc, string name, string ctx, List<string> errors)
        {
            var result = new List<string>();
            var value = doc.GetValue(name, BsonNull.Value);
            if (value.IsBsonNull) { return result; }
            if (!value.IsBsonArray) { errors.Add(ctx + ": " + name + " is not a list."); return result; }
            foreach (var item in value.AsBsonArray)
            {
                if (!item.IsString) { errors.Add(ctx + ": " + name + " contains a non-string value."); continue; }
                result.Add(item.AsString);
            }
            return result;
        }

        private static List<BsonDocument> Docs(BsonDocument doc, string name, string ctx, List<string> errors)
        {
            var result = new List<BsonDocument>();
            var value = doc.GetValue(name, BsonNull.Value);
            if (value.IsBsonNull) { return result; }
            if (!value.IsBsonArray) { errors.Add(ctx + ": " + name + " is not a list."); return result; }
            foreach (var item in value.AsBsonArray)
            {
                if (item.IsBsonDocument) { result.Add(item.AsBsonDocument); }
                else { errors.Add(ctx + ": " + name + " contains a non-document value."); }
            }
            return result;
        }

        // ---- YAML writer (mirrors AreaParser) ----

        private static string Serialize(AreaDefinition area)
        {
            var sb = new StringBuilder();
            sb.Append("# Exported from the database. Definition fields only; items and runtime state are not included.\n");
            sb.Append("area:\n");
            sb.Append("  schema_version: 1\n");
            sb.Append("  prefix: ").Append(Scalar(area.Prefix)).Append('\n');
            sb.Append("  # Not stored in the database; set the name by hand.\n");
            sb.Append("  name: ").Append(Quote(area.Name)).Append('\n');
            sb.Append('\n');
            sb.Append("rooms:\n");
            foreach (var room in area.Rooms) { WriteRoom(sb, room, area.Prefix); }
            return sb.ToString();
        }

        private static void WriteRoom(StringBuilder sb, RoomDefinition room, string prefix)
        {
            const string body = "    ";
            sb.Append("  - key: ").Append(Scalar(room.LocalKey)).Append('\n');
            sb.Append(body).Append("short: ").Append(Quote(room.Short)).Append('\n');
            WriteText(sb, body, "long", room.Long);
            WriteList(sb, body, "handles", room.Handles);
            WriteList(sb, body, "adjectives", room.Adjectives);
            WriteList(sb, body, "plurals", room.Plurals);
            WriteCoords(sb, body, room);
            WriteExits(sb, body, room.Exits, prefix);
            WriteFeatures(sb, body, room.Features);
            WriteProperties(sb, body, room.Properties);
        }

        private static void WriteCoords(StringBuilder sb, string indent, RoomDefinition room)
        {
            var parts = new List<string>();
            if (room.X.HasValue) { parts.Add("x: " + Num(room.X.Value)); }
            if (room.Y.HasValue) { parts.Add("y: " + Num(room.Y.Value)); }
            if (room.Z.HasValue) { parts.Add("z: " + Num(room.Z.Value)); }
            if (parts.Count > 0)
            {
                sb.Append(indent).Append("coords: {").Append(string.Join(", ", parts)).Append("}\n");
            }
        }

        private static void WriteExits(StringBuilder sb, string indent, List<ExitDefinition> exits, string prefix)
        {
            if (exits.Count == 0) { sb.Append(indent).Append("exits: []\n"); return; }
            sb.Append(indent).Append("exits:\n");
            foreach (var exit in exits)
            {
                sb.Append(indent).Append("  - dir: ").Append(Scalar(exit.Dir)).Append('\n');
                sb.Append(indent).Append("    to: ").Append(Scalar(ToReference(exit.To, prefix))).Append('\n');
            }
        }

        // Same-area destinations are written as local keys (spec section 3); others stay full keys.
        private static string ToReference(string key, string prefix)
        {
            var localPrefix = prefix + ".";
            if (key.StartsWith(localPrefix, StringComparison.Ordinal) && AreaParser.LocalKeyPattern.IsMatch(key.Substring(localPrefix.Length)))
            {
                return key.Substring(localPrefix.Length);
            }
            return key;
        }

        private static void WriteFeatures(StringBuilder sb, string indent, List<FeatureDefinition> features)
        {
            if (features.Count == 0) { sb.Append(indent).Append("features: []\n"); return; }
            sb.Append(indent).Append("features:\n");
            var item = indent + "    ";
            foreach (var feature in features)
            {
                sb.Append(indent).Append("  - short: ").Append(Quote(feature.Short)).Append('\n');
                WriteText(sb, item, "long", feature.Long);
                WriteList(sb, item, "handles", feature.Handles);
                WriteList(sb, item, "adjectives", feature.Adjectives);
                WriteList(sb, item, "plurals", feature.Plurals);
                WriteProperties(sb, item, feature.Properties);
            }
        }

        private static void WriteProperties(StringBuilder sb, string indent, List<SearchPropertyDefinition> properties)
        {
            if (properties.Count == 0) { sb.Append(indent).Append("properties: []\n"); return; }
            sb.Append(indent).Append("properties:\n");
            var item = indent + "    ";
            var parameters = item + "  ";
            foreach (var prop in properties)
            {
                sb.Append(indent).Append("  - behavior: search\n");
                sb.Append(item).Append("id: ").Append(Scalar(prop.Id)).Append('\n');
                sb.Append(item).Append("params:\n");
                sb.Append(parameters).Append("target: ").Append(Quote(prop.Target)).Append('\n');
                sb.Append(parameters).Append("refresh_ms: ").Append(Num(prop.RefreshMs)).Append('\n');
                sb.Append(parameters).Append("reward:\n");
                WriteItem(sb, parameters + "  ", prop.Reward);
            }
        }

        private static void WriteItem(StringBuilder sb, string indent, ItemDefinition item)
        {
            sb.Append(indent).Append("short: ").Append(Quote(item.Short)).Append('\n');
            WriteText(sb, indent, "long", item.Long);
            WriteList(sb, indent, "handles", item.Handles);
            WriteList(sb, indent, "adjectives", item.Adjectives);
            WriteList(sb, indent, "plurals", item.Plurals);
        }

        // Multi-line text uses a literal block when that is exact; otherwise a quoted string. Both load to the same value.
        private static void WriteText(StringBuilder sb, string indent, string key, string value)
        {
            if (IsBlockSafe(value))
            {
                sb.Append(indent).Append(key).Append(": |-\n");
                foreach (var line in value.Split('\n'))
                {
                    if (line.Length > 0) { sb.Append(indent).Append("  ").Append(line); }
                    sb.Append('\n');
                }
            }
            else
            {
                sb.Append(indent).Append(key).Append(": ").Append(Quote(value)).Append('\n');
            }
        }

        // A block scalar takes its indentation from its first line, so a value starting with whitespace is quoted.
        private static bool IsBlockSafe(string value)
        {
            if (value.IndexOf('\n') < 0) { return false; }
            if (char.IsWhiteSpace(value[0])) { return false; }
            return value.All(c => c == '\n' || !NeedsEscape(c));
        }

        private static void WriteList(StringBuilder sb, string indent, string key, List<string> items)
        {
            sb.Append(indent).Append(key).Append(": [").Append(string.Join(", ", items.Select(Quote))).Append("]\n");
        }

        private static bool NeedsEscape(char c)
        {
            return c < 0x20 || (c >= 0x7F && c <= 0x9F) || c == '\u2028' || c == '\u2029';
        }

        // Double-quoted YAML scalar. Always valid, and round-trips exactly.
        private static string Quote(string value)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (NeedsEscape(c)) { sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture)); }
                        else { sb.Append(c); }
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        private static string Scalar(string value)
        {
            if (PlainScalarPattern.IsMatch(value) && value != "null" && value != "~") { return value; }
            return Quote(value);
        }

        private static string Num(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
