using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MUD.Data;

namespace MUD.Core.Generation.Terrains
{
    // A terrain type belongs to an AREA; fragments, feature templates and neighbour phrases belong to a terrain type
    // (specs/TERRAIN_GENERATORS.md section 6). Id = "<area>.<local>"; local equals the map legend template name.
    // The id (and so the area) never changes.
    public sealed class TerrainType
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }

        public string Area { get { return TerrainTypeRules.AreaOf(Id); } }
        public string Local { get { return TerrainTypeRules.LocalOf(Id); } }
    }

    public static class TerrainTypeRules
    {
        public static readonly Regex LocalPattern = new Regex("^[a-z0-9_]+$");
        public const int MaxName = AreaRules.MaxName;
        public const int MaxDescription = AreaRules.MaxDescription;

        // The scoped id a generator asks the library with: map prefix + legend template name.
        public static string IdFor(string area, string local)
        {
            return area + "." + local;
        }

        // Everything before the last '.', or "" when there is none. (An area prefix may itself contain dots.)
        public static string AreaOf(string id)
        {
            int dot = id == null ? -1 : id.LastIndexOf('.');
            return dot <= 0 ? string.Empty : id.Substring(0, dot);
        }

        public static string LocalOf(string id)
        {
            int dot = id == null ? -1 : id.LastIndexOf('.');
            return id == null ? string.Empty : id.Substring(dot + 1);
        }

        // Null when the id has the shape "<area>.<local>" (a valid area prefix and a local matching ^[a-z0-9_]+$). Existence is checked by the caller.
        public static string CheckId(string id)
        {
            if (string.IsNullOrEmpty(id)) { return "The terrain type id is required."; }
            string area = AreaOf(id);
            if (area.Length == 0) { return "The terrain type id must be '<area>.<name>'."; }
            if (RoomRules.CheckAreaPrefix(area) != null) { return "The area part of the terrain type id is not a valid area prefix."; }
            if (!LocalPattern.IsMatch(LocalOf(id))) { return "The name part of the terrain type id may only contain lowercase letters, digits and '_'."; }
            if (id.Length > RoomRules.MaxKey) { return "The terrain type id is too long."; }
            return null;
        }
    }

    public static class TerrainTypeDocuments
    {
        public const string Collection = "terrain_type";

        public static BsonDocument ToDocument(TerrainType t)
        {
            return new BsonDocument { { "_id", t.Id }, { "area", t.Area }, { "name", t.Name ?? string.Empty }, { "description", t.Description ?? string.Empty } };
        }

        public static TerrainType From(BsonDocument d)
        {
            return new TerrainType { Id = Text(d, "_id"), Name = Text(d, "name") ?? string.Empty, Description = Text(d, "description") ?? string.Empty };
        }

        private static string Text(BsonDocument d, string name) { return d.TryGetValue(name, out var v) && v.IsString ? v.AsString : null; }
    }

    // One map file's view of an area: the generator block and the legend template names (= the terrain locals the map uses).
    public sealed class MapAreaInfo
    {
        public string Prefix { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public List<string> Terrains { get; set; } = new List<string>();
    }
}
