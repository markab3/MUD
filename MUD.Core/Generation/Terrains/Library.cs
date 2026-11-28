using System;
using System.Collections.Generic;
using System.Linq;

namespace MUD.Core.Generation.Terrains
{
    // Library entities (specs/TERRAIN_GENERATORS.md section 6.1). Every entity is scoped to EXACTLY ONE terrain type:
    // Terrain is the id of a terrain type ("<area>.<local>", see TerrainType). There is no unscoped entity and no entity shared
    // by several terrains (owner decision 2026-11-27: shared entries are one copy per terrain).
    public interface ITerrainScoped
    {
        string Id { get; set; }
        // The terrain type id. Null/empty only on a legacy document that the startup migration has not (or could not) move;
        // the generators never see such an entity.
        string Terrain { get; set; }
        // The retired `terrains` array of a legacy document. Kept on migrated documents, ignored by everything else.
        List<string> LegacyTerrains { get; set; }
    }

    public class DescriptionFragment : ITerrainScoped
    {
        public string Id { get; set; }
        public string Terrain { get; set; }
        public List<string> LegacyTerrains { get; set; } = new List<string>();
        public string Slot { get; set; }
        public string Text { get; set; }
        // Weight 0 = never picked (the weighted pick excludes weight <= 0).
        public int Weight { get; set; } = 1;
        public List<string> Tags { get; set; } = new List<string>();
    }

    public class FeatureTemplate : ITerrainScoped
    {
        public string Id { get; set; }
        public string Terrain { get; set; }
        public List<string> LegacyTerrains { get; set; } = new List<string>();
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

    public class NeighborPhrase : ITerrainScoped
    {
        public string Id { get; set; }
        // The ROOM terrain (terrain type id) the phrase appears in.
        public string Terrain { get; set; }
        public List<string> LegacyTerrains { get; set; } = new List<string>();
        // The terrain type id of the neighbouring cell that triggers the phrase. Always in the same area as Terrain.
        public string NeighborTerrain { get; set; }
        // Contains the {dir} placeholder.
        public string Text { get; set; }
        public int Weight { get; set; } = 1;
        // True: several sides with the same neighbour terrain become one sentence ("north and east").
        public bool Merge { get; set; } = true;
    }

    // Read-only snapshot of the terrain library. Every query is per terrain type id ("<area>.<local>"); an entity is returned only when
    // its Terrain equals the requested id. Results are ordered by Id (ordinal) for stable draws.
    public interface ILibrary
    {
        int Version { get; }
        IReadOnlyList<DescriptionFragment> Fragments(string terrainTypeId, string slot);
        IReadOnlyList<FeatureTemplate> Features(string terrainTypeId);
        IReadOnlyList<NeighborPhrase> Phrases(string roomTerrainTypeId, string neighborTerrainTypeId);
        FeatureTemplate FeatureById(string id);
    }

    public class InMemoryLibrary : ILibrary
    {
        public int Version { get; set; } = 1;
        public List<DescriptionFragment> FragmentList { get; } = new List<DescriptionFragment>();
        public List<FeatureTemplate> FeatureList { get; } = new List<FeatureTemplate>();
        public List<NeighborPhrase> PhraseList { get; } = new List<NeighborPhrase>();

        public IReadOnlyList<DescriptionFragment> Fragments(string terrainTypeId, string slot)
        {
            return FragmentList.Where(f => Is(f.Terrain, terrainTypeId) && f.Slot == slot)
                .OrderBy(f => f.Id, StringComparer.Ordinal).ToList();
        }

        public IReadOnlyList<FeatureTemplate> Features(string terrainTypeId)
        {
            return FeatureList.Where(f => Is(f.Terrain, terrainTypeId)).OrderBy(f => f.Id, StringComparer.Ordinal).ToList();
        }

        public IReadOnlyList<NeighborPhrase> Phrases(string roomTerrainTypeId, string neighborTerrainTypeId)
        {
            return PhraseList.Where(p => Is(p.Terrain, roomTerrainTypeId) && p.NeighborTerrain == neighborTerrainTypeId)
                .OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        }

        public FeatureTemplate FeatureById(string id)
        {
            return FeatureList.FirstOrDefault(f => f.Id == id);
        }

        private static bool Is(string entityTerrain, string wanted)
        {
            return !string.IsNullOrEmpty(entityTerrain) && entityTerrain == wanted;
        }
    }
}
