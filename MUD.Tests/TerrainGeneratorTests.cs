using System.Collections.Generic;
using System.Linq;
using MUD.Core.Generation;
using MUD.Core.Generation.Terrains;
using MUD.Core.Import;
using Xunit;

namespace MUD.Tests
{
    public class TerrainGeneratorTests
    {
        // Row 0 is the north edge; the river row has no rooms (walkable: false).
        private const string Map = @"
generator:
  prefix: land
  name: Test Land
  seed: s1
grid:
  - '~~~'
  - '...'
  - 'FF.'
legend:
  '~': {template: river, color: '#3a7bd5'}
  '.': grass
  F: forest
templates:
  river: {walkable: false}
  grass:
    short: a meadow
    long: [[Grass grows here.]]
  forest:
    short: a forest
    long: [[Trees crowd close.]]
";

        private static InMemoryLibrary Library()
        {
            var lib = new InMemoryLibrary { Version = 7 };
            lib.PhraseList.Add(new NeighborPhrase { Id = "river-near", Terrain = "land.grass", NeighborTerrain = "land.river", Text = "A river flows just {dir} of here.", Weight = 1 });
            lib.PhraseList.Add(new NeighborPhrase { Id = "forest-near", Terrain = "land.grass", NeighborTerrain = "land.forest", Text = "Trees stand {dir} of here.", Weight = 1 });
            lib.FeatureList.Add(new FeatureTemplate
            {
                Id = "boulder", Terrain = "land.grass", Short = "a boulder", Long = "A boulder.", Handles = { "boulder" },
                Weight = 1, Chance = 1.0, Version = 3,
            });
            return lib;
        }

        private static Dictionary<string, RoomDefinition> Rooms(string yaml)
        {
            var parsed = AreaParser.Parse(yaml);
            Assert.True(parsed.Errors.Count == 0, string.Join("; ", parsed.Errors));
            return parsed.Area.Rooms.ToDictionary(r => r.FullKey);
        }

        private static GenerateResult Gen(string map, ILibrary lib)
        {
            var r = MapGenerator.Generate(map, "land_map", lib);
            Assert.True(r.Success, string.Join("; ", r.Errors));
            return r;
        }

        [Fact]
        public void WithoutLibraryOutputIsUnchangedAndHasNoProvenance()
        {
            var plain = MapGenerator.Generate(Map);
            Assert.True(plain.Success, string.Join("; ", plain.Errors));
            Assert.DoesNotContain("x-gen:", plain.Yaml);
            Assert.DoesNotContain("flows", plain.Yaml);
        }

        [Fact]
        public void LibraryRunIsByteIdenticalForSameInputs()
        {
            Assert.Equal(Gen(Map, Library()).Yaml, Gen(Map, Library()).Yaml);
        }

        [Fact]
        public void NeighbourPhraseMergesSidesAndLooksAtDiagonals()
        {
            var rooms = Rooms(Gen(Map, Library()).Yaml);
            // Middle cell of the grass row (x=1,y=1): river on NW, N, NE.
            var mid = rooms["land.x01y01"];
            Assert.Contains("A river flows just north, northeast and northwest of here.", mid.Long);
        }

        [Fact]
        public void NeighbourPhraseCapIsApplied()
        {
            // Cell (1,1): river N/NE/NW, forest S/SW/SE, grass E/W: three groups, cap is 2.
            var lib = Library();
            lib.PhraseList.Add(new NeighborPhrase { Id = "grass-near", Terrain = "land.grass", NeighborTerrain = "land.grass", Text = "Grass lies {dir}.", Weight = 1 });
            var rooms = Rooms(Gen(Map, lib).Yaml);
            var r = rooms["land.x01y01"];
            var sentences = new[] { "river", "Trees", "Grass" }.Count(w => r.Long.Contains(w + " ") && (r.Long.Contains(w + " flows") || r.Long.Contains(w + " stand") || r.Long.Contains(w + " lies")));
            Assert.Equal(2, sentences);
            Assert.Contains("A river flows", r.Long);
        }

        [Fact]
        public void EmptyTerrainsListIsNeverUsed()
        {
            var lib = Library();
            lib.PhraseList.Clear();
            lib.FeatureList.Clear();
            lib.PhraseList.Add(new NeighborPhrase { Id = "orphan", NeighborTerrain = "river", Text = "A river flows {dir}.", Weight = 5 });
            lib.FeatureList.Add(new FeatureTemplate { Id = "orphan-f", Short = "an orphan", Long = "x", Weight = 5, Chance = 1.0 });
            var rooms = Rooms(Gen(Map, lib).Yaml);
            Assert.All(rooms.Values, r => Assert.DoesNotContain("flows", r.Long));
            Assert.All(rooms.Values, r => Assert.Empty(r.Features));
            Assert.Empty(lib.Features("grass"));
            Assert.Empty(lib.Phrases("grass", "river"));
        }

        [Fact]
        public void TheGeneratorAsksTheLibraryWithScopedIds_MapPrefixPlusLegendName()
        {
            var recorder = new RecordingLibrary();
            Gen(Map, recorder);
            // Map prefix is "land"; the legend templates are river, grass, forest: never the plain names.
            Assert.NotEmpty(recorder.FeatureAsks);
            Assert.NotEmpty(recorder.PhraseAsks);   // (fragments are only asked for when a template has no text of its own)
            Assert.All(recorder.FragmentAsks.Select(a => a.Terrain).Distinct(), t => Assert.StartsWith("land.", t));
            Assert.All(recorder.FeatureAsks.Distinct(), t => Assert.Contains(t, new[] { "land.grass", "land.forest" }));
            Assert.All(recorder.PhraseAsks.Select(a => a.Room).Distinct(), t => Assert.StartsWith("land.", t));
            Assert.All(recorder.PhraseAsks.Select(a => a.Neighbor).Distinct(), t => Assert.StartsWith("land.", t));
            Assert.Contains(recorder.PhraseAsks, a => a.Room == "land.grass" && a.Neighbor == "land.river");
            Assert.DoesNotContain(recorder.PhraseAsks, a => a.Room == "grass" || a.Neighbor == "river");
        }

        [Fact]
        public void ATerrainWithNoTerrainTypeRecordFallsBackToTheMapFilesOwnText_NoErrorNoWarning()
        {
            // The library only knows land.forest; grass (no record) keeps the map file's own text, as a terrain with no entries always did.
            var lib = new InMemoryLibrary { Version = 1 };
            lib.PhraseList.Add(new NeighborPhrase { Id = "forest-grass", Terrain = "land.forest", NeighborTerrain = "land.grass", Text = "Water runs {dir}.", Weight = 1 });
            var result = Gen(Map, lib);
            Assert.Empty(result.Warnings);
            var rooms = Rooms(result.Yaml);
            Assert.Equal("a meadow", rooms["land.x00y01"].Short);
            Assert.Contains("Grass grows here.", rooms["land.x00y01"].Long);
            Assert.Contains("Water runs", rooms["land.x00y00"].Long);   // forest has a terrain type record: library phrases apply
            Assert.DoesNotContain("flows", rooms["land.x00y01"].Long);   // grass has none: no library content, only the map file text
        }

        [Fact]
        public void NeighbourPhrasesComeOnlyFromTheSameMap_APhraseOfAnotherAreaIsNeverUsed()
        {
            var lib = Library();
            // Same plain names, other area: must never appear in a "land" map even though the names match.
            lib.PhraseList.Add(new NeighborPhrase { Id = "alien-river", Terrain = "elsewhere.grass", NeighborTerrain = "elsewhere.river", Text = "ALIEN {dir}", Weight = 1000 });
            lib.PhraseList.Add(new NeighborPhrase { Id = "cross-river", Terrain = "land.grass", NeighborTerrain = "elsewhere.river", Text = "CROSS {dir}", Weight = 1000 });
            var yaml = Gen(Map, lib).Yaml;
            Assert.DoesNotContain("ALIEN", yaml);
            Assert.DoesNotContain("CROSS", yaml);
            Assert.Contains("A river flows just", yaml);
        }

        [Fact]
        public void TheSameMapUnderAnotherPrefixFindsNothingInThisAreasLibrary()
        {
            var other = Map.Replace("prefix: land", "prefix: elsewhere");
            var r = MapGenerator.Generate(other, "elsewhere_map", Library());
            Assert.True(r.Success, string.Join("; ", r.Errors));
            Assert.DoesNotContain("flows", r.Yaml);
            Assert.DoesNotContain("Trees stand", r.Yaml);
            Assert.DoesNotContain("boulder", r.Yaml);
        }

        [Fact]
        public void AGeneratedRoomsAreaIsTheMapPrefix_SoRegenCanResolveRoomAreaPlusPlainTerrain()
        {
            var rooms = Rooms(Gen(Map, Library()).Yaml);
            var room = rooms["land.x01y01"];
            Assert.Equal("grass", room.Gen.Terrain);                       // provenance keeps the PLAIN name
            Assert.Equal("land", room.FullKey.Substring(0, room.FullKey.IndexOf('.')));   // the key prefix is the area
            Assert.Equal("land.grass", TerrainTypeRules.IdFor("land", room.Gen.Terrain));
        }

        private class RecordingLibrary : ILibrary
        {
            public List<(string Terrain, string Slot)> FragmentAsks = new List<(string, string)>();
            public List<string> FeatureAsks = new List<string>();
            public List<(string Room, string Neighbor)> PhraseAsks = new List<(string, string)>();
            public int Version { get { return 1; } }
            public IReadOnlyList<DescriptionFragment> Fragments(string terrainTypeId, string slot) { FragmentAsks.Add((terrainTypeId, slot)); return new List<DescriptionFragment>(); }
            public IReadOnlyList<FeatureTemplate> Features(string terrainTypeId) { FeatureAsks.Add(terrainTypeId); return new List<FeatureTemplate>(); }
            public IReadOnlyList<NeighborPhrase> Phrases(string roomTerrainTypeId, string neighborTerrainTypeId) { PhraseAsks.Add((roomTerrainTypeId, neighborTerrainTypeId)); return new List<NeighborPhrase>(); }
            public FeatureTemplate FeatureById(string id) { return null; }
        }

        [Fact]
        public void RoomsCarryProvenanceAndFeaturesCarryTemplateReference()
        {
            var yaml = Gen(Map, Library()).Yaml;
            Assert.Contains("x-gen:", yaml);
            Assert.Contains("map: \"land_map\"", yaml);
            Assert.Contains("terrain: \"grass\"", yaml);
            Assert.Contains("seed: \"s1\"", yaml);
            Assert.Contains("libVersion: 7", yaml);
            Assert.Contains("templateId: \"boulder\"", yaml);
            Assert.Contains("templateVersion: 3", yaml);
            // Output still passes importer phase-1 validation.
            var rooms = Rooms(yaml);
            Assert.Equal("a boulder", rooms["land.x01y01"].Features.Single().Short);
        }

        [Fact]
        public void LegendColorIsParsedButNeverReachesRoomData()
        {
            var r = Gen(Map, Library());
            Assert.Equal("#3A7BD5", r.Colors["river"]);
            Assert.DoesNotContain("3a7bd5", r.Yaml.ToLowerInvariant());
            Assert.DoesNotContain("color", r.Yaml);
            var plain = MapGenerator.Generate(Map);
            Assert.Equal("#3A7BD5", plain.Colors["river"]);
        }

        [Fact]
        public void BadLegendColourAndUnknownLegendKeyAreErrors()
        {
            var bad = MapGenerator.Generate(Map.Replace("#3a7bd5", "blue"));
            Assert.False(bad.Success);
            Assert.Contains(bad.Errors, e => e.Contains("invalid color"));
            var unknown = MapGenerator.Generate(Map.Replace("color: '#3a7bd5'", "hue: 3"));
            Assert.False(unknown.Success);
        }

        [Fact]
        public void RegistryReturnsBespokeClassElseGenericTerrain()
        {
            var registry = new TerrainRegistry();
            Assert.IsType<Terrain>(registry.For("grass"));
            Assert.Equal("grass", registry.For("grass").TerrainId);
            var bespoke = new RiverTerrain();
            registry.Register(bespoke);
            Assert.Same(bespoke, registry.For("river"));
            Assert.IsType<Terrain>(registry.For("forest"));
        }

        private class RiverTerrain : Terrain
        {
            public RiverTerrain() : base("river") { }
            protected override string Orientation(GenContext ctx)
            {
                return ctx.At(Dir.E).TerrainId == "river" || ctx.At(Dir.W).TerrainId == "river" ? "It runs east to west." : null;
            }
        }

        [Fact]
        public void BespokeClassOverridesOrientation()
        {
            var map = Map.Replace("river: {walkable: false}", "river: {short: a river, long: [[Water.]]}");
            var registry = new TerrainRegistry();
            registry.Register(new RiverTerrain());
            var r = MapGenerator.Generate(map, "land_map", Library(), registry);
            Assert.True(r.Success, string.Join("; ", r.Errors));
            Assert.Contains("It runs east to west.", Rooms(r.Yaml)["land.x01y02"].Long);
        }

        [Fact]
        public void NeighbourScanHelpers()
        {
            Assert.Equal("north", NeighbourPhrases.JoinDirs(new[] { Dir.N }));
            Assert.Equal("north and east", NeighbourPhrases.JoinDirs(new[] { Dir.E, Dir.N }));
            Assert.Equal("north, east and southwest", NeighbourPhrases.JoinDirs(new[] { Dir.SW, Dir.E, Dir.N }));
            Assert.Equal("all around", NeighbourPhrases.JoinDirs(System.Enum.GetValues(typeof(Dir)).Cast<Dir>()));
        }

        [Fact]
        public void LibraryTextFillsInWhereTemplateHasNone()
        {
            var lib = Library();
            lib.FragmentList.Add(new DescriptionFragment { Id = "g-short", Terrain = "land.grass", Slot = "short", Text = "a lib meadow" });
            lib.FragmentList.Add(new DescriptionFragment { Id = "g-ground", Terrain = "land.grass", Slot = "ground", Text = "Library grass." });
            lib.FragmentList.Add(new DescriptionFragment { Id = "g-unscoped", Slot = "ground", Text = "Never.", Weight = 100 });
            // Generator driven directly: a template with no text falls back to library fragments.
            var ctx = new GenContext
            {
                TerrainId = "grass", Prefix = "land", Template = new TerrainTemplate { Name = "grass" }, Library = lib, Rng = new SeededRandom("x"),
                Neighbours = Enumerable.Range(0, 8).Select(i => new Neighbour { Dir = (Dir)i }).ToArray(),
            };
            var room = new Terrain("grass").Generate(ctx);
            Assert.Equal("a lib meadow", room.Short);
            Assert.Equal("Library grass.", room.Long);
        }
    }
}
