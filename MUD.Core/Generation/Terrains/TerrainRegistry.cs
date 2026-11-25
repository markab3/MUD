using System;
using System.Collections.Generic;

namespace MUD.Core.Generation.Terrains
{
    public class TerrainRegistry
    {
        private readonly Dictionary<string, ITerrainGenerator> _generators = new Dictionary<string, ITerrainGenerator>(StringComparer.Ordinal);

        // Explicit list, no reflection scanning. No bespoke classes ship yet: every terrain uses the generic Terrain.
        public static TerrainRegistry Default() { return new TerrainRegistry(); }

        public void Register(ITerrainGenerator generator)
        {
            if (generator == null) { throw new ArgumentNullException("generator"); }
            _generators[generator.TerrainId] = generator;
        }

        public bool IsRegistered(string terrainId) { return _generators.ContainsKey(terrainId); }

        // The registered class, else the generic Terrain for that id.
        public ITerrainGenerator For(string terrainId)
        {
            ITerrainGenerator g;
            return _generators.TryGetValue(terrainId, out g) ? g : new Terrain(terrainId);
        }
    }
}
