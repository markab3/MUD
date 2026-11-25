using System;
using System.Collections.Generic;
using System.Linq;

namespace MUD.Core.Generation.Terrains
{
    // Library entities (specs/TERRAIN_GENERATORS.md section 6.1). Every entity has Terrains; an EMPTY list
    // means "not used by any generator" (disabled/unassigned), never "any terrain".
    public class DescriptionFragment
    {
        public string Id { get; set; }
        public List<string> Terrains { get; set; } = new List<string>();
        public string Slot { get; set; }
        public string Text { get; set; }
        public int Weight { get; set; } = 1;
        public List<string> Tags { get; set; } = new List<string>();
    }

    public class FeatureTemplate
    {
        public string Id { get; set; }
        public List<string> Terrains { get; set; } = new List<string>();
        public string Short { get; set; }
        public string Long { get; set; }
        public List<string> Handles { get; set; } = new List<string>();
        public List<string> Adjectives { get; set; } = new List<string>();
        public string RoomLine { get; set; }
        public string Detail { get; set; }
        public int Weight { get; set; } = 1;
        public double Chance { get; set; } = 1.0;
        public int Version { get; set; } = 1;
        public List<string> Tags { get; set; } = new List<string>();
    }

    public class NeighborPhrase
    {
        public string Id { get; set; }
        // The ROOM terrains the phrase may appear in.
        public List<string> Terrains { get; set; } = new List<string>();
        // The terrain of the neighbouring cell that triggers the phrase.
        public string NeighborTerrain { get; set; }
        // Contains the {dir} placeholder.
        public string Text { get; set; }
        public int Weight { get; set; } = 1;
        // True: several sides with the same neighbour terrain become one sentence ("north and east").
        public bool Merge { get; set; } = true;
    }

    // Read-only snapshot of the terrain library (Mongo-backed implementation is a separate card).
    // Implementations MUST NOT return entities whose Terrains does not contain the requested terrain
    // (so an empty Terrains list is never returned). Results are ordered by Id (ordinal) for stable draws.
    public interface ILibrary
    {
        int Version { get; }
        IReadOnlyList<DescriptionFragment> Fragments(string terrain, string slot);
        IReadOnlyList<FeatureTemplate> Features(string terrain);
        IReadOnlyList<NeighborPhrase> Phrases(string roomTerrain, string neighborTerrain);
        FeatureTemplate FeatureById(string id);
    }

    public class InMemoryLibrary : ILibrary
    {
        public int Version { get; set; } = 1;
        public List<DescriptionFragment> FragmentList { get; } = new List<DescriptionFragment>();
        public List<FeatureTemplate> FeatureList { get; } = new List<FeatureTemplate>();
        public List<NeighborPhrase> PhraseList { get; } = new List<NeighborPhrase>();

        public static bool Uses(IList<string> terrains, string terrain)
        {
            return terrains != null && terrain != null && terrains.Contains(terrain);
        }

        public IReadOnlyList<DescriptionFragment> Fragments(string terrain, string slot)
        {
            return FragmentList.Where(f => Uses(f.Terrains, terrain) && f.Slot == slot)
                .OrderBy(f => f.Id, StringComparer.Ordinal).ToList();
        }

        public IReadOnlyList<FeatureTemplate> Features(string terrain)
        {
            return FeatureList.Where(f => Uses(f.Terrains, terrain)).OrderBy(f => f.Id, StringComparer.Ordinal).ToList();
        }

        public IReadOnlyList<NeighborPhrase> Phrases(string roomTerrain, string neighborTerrain)
        {
            return PhraseList.Where(p => Uses(p.Terrains, roomTerrain) && p.NeighborTerrain == neighborTerrain)
                .OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        }

        public FeatureTemplate FeatureById(string id)
        {
            return FeatureList.FirstOrDefault(f => f.Id == id);
        }
    }
}
