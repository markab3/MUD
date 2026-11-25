using System;
using System.Collections.Generic;

namespace MUD.Core.Generation.Terrains
{
    // Neighbours[] order.
    public enum Dir { N, NE, E, SE, S, SW, W, NW }

    public class Neighbour
    {
        public Dir Dir { get; set; }
        // False: outside the grid, or a blank/unmapped cell.
        public bool Exists { get; set; }
        // Terrain id (legend template name) of the neighbouring cell. Set for non-walkable cells too
        // (water has a terrain but no room), which is what makes "a river to the north" possible.
        public string TerrainId { get; set; }
        public bool Walkable { get; set; }
    }

    // The map-file template for a terrain (existing `templates:` data). Terrain id = Name.
    public class TerrainTemplate
    {
        public string Name;
        public bool Walkable = true;
        public List<string> Short = new List<string>();
        public List<List<string>> Long = new List<List<string>>();
        public List<string> Handles = new List<string>();
        public List<string> Adjectives = new List<string>();
        public List<string> Plurals = new List<string>();
    }

    public class GenContext
    {
        public int X { get; set; }
        public int Y { get; set; }
        public string MapName { get; set; }
        public string Prefix { get; set; }
        public string Key { get; set; }
        public string TerrainId { get; set; }
        public TerrainTemplate Template { get; set; }
        // Per cell: seed + "\n" + prefix + "\n" + key.
        public SeededRandom Rng { get; set; }
        // Null when the map is generated without a library (existing behaviour).
        public ILibrary Library { get; set; }
        // Always 8 entries in Dir order.
        public Neighbour[] Neighbours { get; set; }

        public Neighbour At(Dir d) { return Neighbours[(int)d]; }
    }

    public class GeneratedFeature
    {
        public string Short;
        public string Long;
        public string RoomLine;
        public string Detail;
        public List<string> Handles = new List<string>();
        public List<string> Adjectives = new List<string>();
        public string TemplateId;
        public int TemplateVersion;
    }

    public class GeneratedRoom
    {
        // Null Short/Long: the generator found no text (MapGenerator reports the error, or an override supplies it).
        public string Short;
        public string Long;
        public List<string> Handles = new List<string>();
        public List<string> Adjectives = new List<string>();
        public List<string> Plurals = new List<string>();
        public List<GeneratedFeature> Features = new List<GeneratedFeature>();
    }

    public interface ITerrainGenerator
    {
        // = legend template name.
        string TerrainId { get; }
        // Pure: no I/O. Everything it may depend on is in ctx.
        GeneratedRoom Generate(GenContext ctx);
    }
}
