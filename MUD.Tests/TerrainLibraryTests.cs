using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MUD.Contracts;
using MUD.Core.Administration;
using MUD.Core.GameObjects;
using MUD.Core.Generation.Terrains;
using MUD.Core.Repositories.Interfaces;
using Xunit;

namespace MUD.Tests
{
    // Terrain library store, seed loader, migration and the Admin library calls (every entity scoped to exactly one terrain type). No database: in-memory stores and fakes.
    // The Mongo implementation (MongoLibraryStore) was NOT run here; see specs/ADMIN_API.md "Not verified here".
    public class FakeLibraryStore : ILibraryStore
    {
        public class Table<T> : ILibraryCollection<T> where T : class
        {
            private readonly Func<T, string> _id;
            public Dictionary<string, T> Items = new Dictionary<string, T>();
            public bool FailWrites;
            public bool FailReads;
            public bool RaceOnWrite;   // another writer got there first: Insert/Replace/Delete report false

            public Table(Func<T, string> id) { _id = id; }
            public IReadOnlyList<T> List() { Check(false); return Items.Values.OrderBy(_id, StringComparer.Ordinal).ToList(); }
            public T Get(string id) { Check(false); return Items.TryGetValue(id, out var v) ? v : null; }
            public bool Insert(T entity, string by) { Check(true); if (RaceOnWrite || Items.ContainsKey(_id(entity))) { return false; } Items[_id(entity)] = entity; return true; }
            public bool Replace(T entity, string by) { Check(true); if (RaceOnWrite || !Items.ContainsKey(_id(entity))) { return false; } Items[_id(entity)] = entity; return true; }
            public bool Delete(string id) { Check(true); return !RaceOnWrite && Items.Remove(id); }
            private void Check(bool write) { if (write ? FailWrites : FailReads) { throw new InvalidOperationException("db down"); } }
        }

        public Table<DescriptionFragment> FragmentTable = new Table<DescriptionFragment>(e => e.Id);
        public Table<FeatureTemplate> FeatureTable = new Table<FeatureTemplate>(e => e.Id);
        public Table<NeighborPhrase> PhraseTable = new Table<NeighborPhrase>(e => e.Id);
        public Table<TerrainType> TerrainTypeTable = new Table<TerrainType>(e => e.Id);
        public int? Version;

        public ILibraryCollection<DescriptionFragment> Fragments { get { return FragmentTable; } }
        public ILibraryCollection<FeatureTemplate> Features { get { return FeatureTable; } }
        public ILibraryCollection<NeighborPhrase> Phrases { get { return PhraseTable; } }
        public ILibraryCollection<TerrainType> TerrainTypes { get { return TerrainTypeTable; } }
        public int? GetVersion() { return Version; }
        public void InitMeta(int version) { if (!Version.HasValue) { Version = version; } }
        public int BumpVersion() { if (!Version.HasValue) { throw new InvalidOperationException("no meta"); } Version = Version.Value + 1; return Version.Value; }
    }

    public class TerrainLibrarySeedTests
    {
        private const string Good = @"
schema_version: 2
library_version: 4
area: { id: demo, name: ""Demo"", description: ""A demo area."" }
terrainTypes:
  - { id: grass, name: ""Grass"" }
  - { id: forest, name: ""Forest"" }
fragments:
  - { id: g1, terrain: grass, slot: short, text: ""a meadow"", weight: 2, tags: [dry] }
  - { id: silent, terrain: forest, slot: short, text: ""never picked"", weight: 0 }
features:
  - { id: b1, terrain: grass, short: ""a boulder"", long: ""A boulder."", handles: [boulder], weight: 2, chance: 0.5, version: 3 }
neighborPhrases:
  - { id: p1, terrain: grass, neighborTerrain: forest, text: ""Trees stand {dir} of here."", merge: false }
";

        private static List<string> ParseErrors(string yaml)
        {
            var errors = new List<string>();
            LibrarySeed.Parse(yaml, errors, new List<string>());
            return errors;
        }

        [Fact]
        public void AValidFileParsesWithAllFields_AndEveryEntryIsScopedToOneScopedTerrainTypeId()
        {
            var errors = new List<string>();
            LibrarySeedData data = LibrarySeed.Parse(Good, errors, new List<string>());
            Assert.Empty(errors);
            Assert.Equal(4, data.LibraryVersion);
            Assert.Equal(("demo", "Demo", "A demo area."), (data.Area.Id, data.Area.Name, data.Area.Description));
            Assert.Equal(new[] { "demo.grass", "demo.forest" }, data.TerrainTypes.Select(t => t.Id));
            Assert.Equal(2, data.Fragments.Count);
            Assert.Equal(("demo.grass", 2), (data.Fragments[0].Terrain, data.Fragments[0].Weight));
            Assert.Equal(0, data.Fragments[1].Weight);   // weight 0 is kept (never picked), not rejected
            Assert.Equal(new[] { "dry" }, data.Fragments[0].Tags);
            Assert.Equal((3, 0.5, 2, "demo.grass"), (data.Features[0].Version, data.Features[0].Chance, data.Features[0].Weight, data.Features[0].Terrain));
            Assert.Equal(("demo.grass", "demo.forest", false), (data.Phrases[0].Terrain, data.Phrases[0].NeighborTerrain, data.Phrases[0].Merge));
        }

        [Fact]
        public void TheEmbeddedSeedFileIsPresentValidAndAllInOneAreaWithFourTerrainTypes()
        {
            string yaml = LibrarySeed.ReadEmbedded();
            Assert.NotNull(yaml);
            var errors = new List<string>();
            LibrarySeedData data = LibrarySeed.Parse(yaml, errors, new List<string>());
            Assert.Empty(errors);
            Assert.Equal("sample_land", data.Area.Id);
            Assert.Equal(new[] { "sample_land.grass", "sample_land.forest", "sample_land.river", "sample_land.ocean" }, data.TerrainTypes.Select(t => t.Id));
            Assert.Equal((13, 4, 12), (data.Fragments.Count, data.Features.Count, data.Phrases.Count));
            var types = new HashSet<string>(data.TerrainTypes.Select(t => t.Id));
            Assert.All(data.Fragments, f => Assert.Contains(f.Terrain, types));
            Assert.All(data.Features, f => Assert.Contains(f.Terrain, types));
            Assert.All(data.Phrases, p => { Assert.Contains(p.Terrain, types); Assert.Contains(p.NeighborTerrain, types); });
        }

        [Fact]
        public void TheEmbeddedSeedSplitsSharedEntries_TheFirstTerrainKeepsTheOriginalId()
        {
            var data = LibrarySeed.Parse(LibrarySeed.ReadEmbedded(), new List<string>(), new List<string>());
            Assert.Equal("sample_land.grass", data.Features.Single(f => f.Id == "boulder-grey").Terrain);
            Assert.Equal("sample_land.forest", data.Features.Single(f => f.Id == "boulder-grey-forest").Terrain);
            Assert.Equal("sample_land.grass", data.Phrases.Single(p => p.Id == "river-near").Terrain);
            Assert.Equal("sample_land.forest", data.Phrases.Single(p => p.Id == "river-near-forest").Terrain);
            Assert.Equal(new[] { "sample_land.forest", "sample_land.grass", "sample_land.river" }, data.Phrases.Where(p => p.Id.StartsWith("ocean-near") && !p.Id.Contains("alt")).Select(p => p.Terrain).OrderBy(x => x));
        }

        [Fact]
        public void TheEmbeddedSeedGivesEveryTerrainItWorksOnAShortFragment()
        {
            var data = LibrarySeed.Parse(LibrarySeed.ReadEmbedded(), new List<string>(), new List<string>());
            foreach (string terrain in data.TerrainTypes.Select(t => t.Id))
            {
                Assert.Contains(data.Fragments, f => f.Slot == "short" && f.Terrain == terrain);
            }
        }

        [Fact]
        public void TheSeedAreaMatchesTheGeneratorBlockOfTheSampleLandMap()
        {
            string path = FindSampleMap();
            var map = MapAreaReader.Read(System.IO.File.ReadAllText(path));
            var data = LibrarySeed.Parse(LibrarySeed.ReadEmbedded(), new List<string>(), new List<string>());
            Assert.Equal((map.Prefix, map.Name, map.Description), (data.Area.Id, data.Area.Name, data.Area.Description));
            Assert.Equal(map.Terrains.OrderBy(x => x), data.TerrainTypes.Select(t => t.Local).OrderBy(x => x));
        }

        private static string FindSampleMap()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "MUD.sln"))) { dir = System.IO.Path.GetDirectoryName(dir); }
            return System.IO.Path.Combine(dir, "MUD.Main", "Content", "maps", "sample_land.map.yaml");
        }

        [Fact]
        public void AFreshStoreIsSeededAreaFirst_TerrainTypesThenEntries_AndTheMetaDocumentIsCreatedLast()
        {
            var store = new FakeLibraryStore();
            var areas = new FakeAreaStore();
            LibrarySeedResult result = LibrarySeed.SeedIfFresh(store, areas, Good);
            Assert.True(result.Seeded);
            Assert.Empty(result.Errors);
            Assert.True(result.AreaCreated);
            Assert.Equal(4, store.Version);
            Assert.Equal(("Demo", "A demo area."), (areas.Items["demo"].Name, areas.Items["demo"].Description));
            Assert.Equal(new[] { "demo.forest", "demo.grass" }, store.TerrainTypeTable.Items.Keys.OrderBy(x => x));
            Assert.Equal((2, 1, 1), (store.FragmentTable.Items.Count, store.FeatureTable.Items.Count, store.PhraseTable.Items.Count));
            Assert.Equal((2, 2, 1, 1), (result.TerrainTypes, result.Fragments, result.Features, result.Phrases));
        }

        [Fact]
        public void TheSeedNeverOverwritesAnExistingAreaOrTerrainType()
        {
            var store = new FakeLibraryStore();
            var areas = new FakeAreaStore().With("demo", "My own name", "mine");
            store.TerrainTypeTable.Items["demo.grass"] = new TerrainType { Id = "demo.grass", Name = "Meadow", Description = "edited" };

            LibrarySeedResult result = LibrarySeed.SeedIfFresh(store, areas, Good);

            Assert.True(result.Seeded);
            Assert.False(result.AreaCreated);
            Assert.Equal("My own name", areas.Items["demo"].Name);
            Assert.Equal("Meadow", store.TerrainTypeTable.Items["demo.grass"].Name);
            Assert.Equal("Forest", store.TerrainTypeTable.Items["demo.forest"].Name);
        }

        [Fact]
        public void TheEmbeddedSeedOnAFreshInstallCreatesSampleLandWith4TerrainTypes_13Fragments_4Features_12Phrases()
        {
            var store = new FakeLibraryStore();
            var areas = new FakeAreaStore();
            LibrarySeedResult result = LibrarySeed.SeedIfFresh(store, areas, LibrarySeed.ReadEmbedded());
            Assert.True(result.Seeded, string.Join("; ", result.Errors));
            Assert.Equal((4, 13, 4, 12), (store.TerrainTypeTable.Items.Count, store.FragmentTable.Items.Count, store.FeatureTable.Items.Count, store.PhraseTable.Items.Count));
            Assert.Equal("Sample Land", areas.Items["sample_land"].Name);
            Assert.Equal(1, store.Version);
        }

        [Fact]
        public void ASecondStartDoesNotReseedAndAdminEditsSurvive()
        {
            var store = new FakeLibraryStore();
            var areas = new FakeAreaStore();
            LibrarySeed.SeedIfFresh(store, areas, Good);
            store.FragmentTable.Items.Remove("g1");                  // an admin deleted it
            store.FragmentTable.Items["extra"] = new DescriptionFragment { Id = "extra" };

            LibrarySeedResult second = LibrarySeed.SeedIfFresh(store, areas, Good);

            Assert.False(second.Seeded);
            Assert.True(second.AlreadySeeded);
            Assert.False(store.FragmentTable.Items.ContainsKey("g1"));
            Assert.True(store.FragmentTable.Items.ContainsKey("extra"));
        }

        [Fact]
        public void AnInvalidSeedWritesNothingAndLeavesTheStoreFresh()
        {
            var store = new FakeLibraryStore();
            var areas = new FakeAreaStore();
            LibrarySeedResult result = LibrarySeed.SeedIfFresh(store, areas, Good.Replace("{dir}", "somewhere"));
            Assert.False(result.Seeded);
            Assert.NotEmpty(result.Errors);
            Assert.Null(store.Version);
            Assert.Empty(store.FragmentTable.Items);
            Assert.Empty(store.TerrainTypeTable.Items);
            Assert.Empty(areas.Items);
        }

        [Fact]
        public void AMissingSeedIsReportedAndNothingIsWritten()
        {
            var store = new FakeLibraryStore();
            LibrarySeedResult result = LibrarySeed.SeedIfFresh(store, new FakeAreaStore(), null);
            Assert.False(result.Seeded);
            Assert.Single(result.Errors);
            Assert.Null(store.Version);
        }

        [Theory]
        [InlineData("schema_version: 1\n", "schema_version")]
        [InlineData("schema_version: 2\nbogus: 1\n", "Unknown top-level key")]
        [InlineData("schema_version: 2\nterrainTypes:\n  - { id: t, name: T }\n", "'area' is required")]
        [InlineData("schema_version: 2\narea: { id: a, name: A }\n", "at least one terrain type")]
        [InlineData("schema_version: 2\narea: { id: a, name: A }\nterrainTypes:\n  - { id: Bad-Id, name: T }\n", "lowercase")]
        [InlineData("schema_version: 2\narea: { id: a, name: A }\nterrainTypes:\n  - { id: t, name: T }\nfragments:\n  - { id: a, terrain: t, slot: s, text: x, colour: red }\n", "Unknown fragment key")]
        [InlineData("schema_version: 2\narea: { id: a, name: A }\nterrainTypes:\n  - { id: t, name: T }\nfragments:\n  - { id: a, terrain: t, slot: s, text: x }\n  - { id: a, terrain: t, slot: s, text: y }\n", "Duplicate fragment id")]
        [InlineData("schema_version: 2\narea: { id: a, name: A }\nterrainTypes:\n  - { id: t, name: T }\nfragments:\n  - { id: a, terrain: t, slot: s, text: x, weight: -1 }\n", "weight")]
        [InlineData("schema_version: 2\narea: { id: a, name: A }\nterrainTypes:\n  - { id: t, name: T }\nfragments:\n  - { id: a, terrain: t, slot: s, text: x, weight: many }\n", "weight must be an integer")]
        [InlineData("schema_version: 2\narea: { id: a, name: A }\nterrainTypes:\n  - { id: t, name: T }\nfragments:\n  - { id: a, slot: s, text: x }\n", "terrain is required")]
        [InlineData("schema_version: 2\narea: { id: a, name: A }\nterrainTypes:\n  - { id: t, name: T }\nfragments:\n  - { id: a, terrain: nope, slot: s, text: x }\n", "not one of the seed's terrain types")]
        [InlineData("schema_version: 2\narea: { id: a, name: A }\nterrainTypes:\n  - { id: t, name: T }\nfragments:\n  - { id: a, terrains: [t], slot: s, text: x }\n", "Unknown fragment key")]
        [InlineData("schema_version: 2\narea: { id: a, name: A }\nterrainTypes:\n  - { id: t, name: T }\nfeatures:\n  - { id: a, terrain: t, short: s, long: l, chance: 1.5 }\n", "chance")]
        [InlineData("schema_version: 2\narea: { id: a, name: A }\nterrainTypes:\n  - { id: t, name: T }\nfeatures:\n  - { id: a, terrain: t, short: s, long: l, version: 0 }\n", "version")]
        [InlineData("schema_version: 2\narea: { id: a, name: A }\nterrainTypes:\n  - { id: t, name: T }\nfeatures:\n  - { id: a, terrain: t, long: l }\n", "short")]
        [InlineData("schema_version: 2\narea: { id: a, name: A }\nterrainTypes:\n  - { id: t, name: T }\nneighborPhrases:\n  - { id: a, terrain: t, neighborTerrain: t, text: \"no placeholder\" }\n", "{dir}")]
        [InlineData("schema_version: 2\narea: { id: a, name: A }\nterrainTypes:\n  - { id: t, name: T }\nneighborPhrases:\n  - { id: a, terrain: t, neighborTerrain: t, text: \"{dir} and {dir}\" }\n", "{dir}")]
        [InlineData("schema_version: 2\narea: { id: a, name: A }\nterrainTypes:\n  - { id: t, name: T }\nneighborPhrases:\n  - { id: a, terrain: t, text: \"x {dir}\" }\n", "neighbour terrain")]
        [InlineData("schema_version: 2\nfragments: [oops\n", "not valid YAML")]
        public void EachInvalidCaseIsRejected(string yaml, string expected)
        {
            List<string> errors = ParseErrors(yaml);
            Assert.Contains(errors, e => e.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void XKeysAreAllowed()
        {
            Assert.Empty(ParseErrors("schema_version: 2\nx-note: hi\narea: { id: a, name: A }\nterrainTypes:\n  - { id: t, name: T }\nfragments:\n  - { id: a, terrain: t, slot: s, text: x, x-why: y }\n"));
        }

        [Fact]
        public void AStoreFailureWhileSeedingPropagatesAndLeavesNoMetaDocument()
        {
            var store = new FakeLibraryStore();
            store.FragmentTable.FailWrites = true;
            Assert.Throws<InvalidOperationException>(() => LibrarySeed.SeedIfFresh(store, new FakeAreaStore(), Good));
            Assert.Null(store.Version);   // so the next start tries again
        }

        [Fact]
        public void ARetryAfterAHalfFinishedSeedDoesNotDuplicateOrFail()
        {
            var store = new FakeLibraryStore();
            var areas = new FakeAreaStore();
            store.FragmentTable.Items["g1"] = new DescriptionFragment { Id = "g1", Text = "left over" };   // inserted before a crash, no meta yet
            areas.With("demo");
            LibrarySeedResult result = LibrarySeed.SeedIfFresh(store, areas, Good);
            Assert.True(result.Seeded);
            Assert.Equal(2, store.FragmentTable.Items.Count);
        }
    }

    public class TerrainStoreLibraryTests
    {
        private static DescriptionFragment Frag(string id, string terrain) { return new DescriptionFragment { Id = id, Terrain = terrain, Slot = "short" }; }

        [Fact]
        public void ReturnsEntitiesOfThatTerrainTypeOnly_AndNeverALegacyEntityWithoutOne()
        {
            var store = new FakeLibraryStore { Version = 5 };
            store.FragmentTable.Items["a"] = Frag("a", "demo.grass");
            store.FragmentTable.Items["b"] = new DescriptionFragment { Id = "b", Terrain = null, LegacyTerrains = new List<string> { "grass" }, Slot = "short" };   // not migrated
            store.FeatureTable.Items["f"] = new FeatureTemplate { Id = "f", Terrain = null };
            store.PhraseTable.Items["p"] = new NeighborPhrase { Id = "p", Terrain = null, NeighborTerrain = "demo.river", Text = "{dir}" };
            var library = new StoreLibrary(store);

            Assert.Equal(5, library.Version);
            Assert.Equal(new[] { "a" }, library.Fragments("demo.grass", "short").Select(f => f.Id));
            Assert.Empty(library.Fragments("demo.forest", "short"));
            Assert.Empty(library.Fragments("grass", "short"));        // the plain name is not a terrain type id
            Assert.Empty(library.Fragments("", "short"));
            Assert.Empty(library.Features("demo.grass"));
            Assert.Empty(library.Phrases("demo.grass", "demo.river"));
            Assert.Null(library.FeatureById("nope"));
            Assert.NotNull(library.FeatureById("f"));
        }

        [Fact]
        public void TheSameLocalNameInTwoAreasIsTwoTerrainTypes()
        {
            var lib = new InMemoryLibrary();
            lib.FragmentList.Add(Frag("a1", "alpha.grass"));
            lib.FragmentList.Add(Frag("b1", "beta.grass"));
            Assert.Equal(new[] { "a1" }, lib.Fragments("alpha.grass", "short").Select(f => f.Id));
            Assert.Equal(new[] { "b1" }, lib.Fragments("beta.grass", "short").Select(f => f.Id));
        }

        [Fact]
        public void ReadsOneSnapshotUntilRefreshed()
        {
            var store = new FakeLibraryStore { Version = 1 };
            store.FragmentTable.Items["a"] = Frag("a", "demo.grass");
            var library = new StoreLibrary(store);
            Assert.Single(library.Fragments("demo.grass", "short"));

            store.FragmentTable.Items["b"] = Frag("b", "demo.grass");
            store.Version = 2;
            Assert.Single(library.Fragments("demo.grass", "short"));   // same run, same snapshot
            Assert.Equal(1, library.Version);

            library.Refresh();
            Assert.Equal(2, library.Fragments("demo.grass", "short").Count);
            Assert.Equal(2, library.Version);
        }

        [Fact]
        public void ARealMapRunUsesTheLibraryFromTheStore_MapPrefixAndLegendNameGiveTheScopedId()
        {
            var store = new FakeLibraryStore();
            var seeded = LibrarySeed.SeedIfFresh(store, new FakeAreaStore(), LibrarySeed.ReadEmbedded());
            Assert.True(seeded.Seeded, string.Join("; ", seeded.Errors));
            Assert.Equal(12, store.PhraseTable.Items.Count);
            var library = new StoreLibrary(store);
            // The map's prefix is sample_land, the legend names are the plain terrain names: sample_land.river / sample_land.grass.
            string map = "generator:\n  prefix: sample_land\n  name: Demo\n  seed: s1\ngrid:\n  - \"~~\"\n  - \"..\"\nlegend:\n  \"~\": {template: river}\n  \".\": grass\ntemplates:\n  river: {walkable: false}\n  grass: {short: a meadow, long: [[Grass grows here.]]}\n";
            var generated = MUD.Core.Generation.MapGenerator.Generate(map, "sample_land", library);
            Assert.True(generated.Success, string.Join("; ", generated.Errors));
            Assert.Contains("libVersion: 1", generated.Yaml);   // library_version of the seed file
            string flat = generated.Yaml.Replace("\r", "").Replace("\n    ", " ");
            Assert.True(flat.Contains("A river flows just north and northeast of here.") || flat.Contains("You hear running water to the north and northeast."), flat);   // a seeded river phrase
        }

        [Fact]
        public void AMapWithAnotherPrefixFindsNoLibraryContent_AndFallsBackToItsOwnText()
        {
            var store = new FakeLibraryStore();
            LibrarySeed.SeedIfFresh(store, new FakeAreaStore(), LibrarySeed.ReadEmbedded());
            var library = new StoreLibrary(store);
            // Same legend names as the library's terrains, but the area is "elsewhere": there are no terrain types elsewhere.grass / elsewhere.river.
            string map = "generator:\n  prefix: elsewhere\n  name: Demo\n  seed: s1\ngrid:\n  - \"~~\"\n  - \"..\"\nlegend:\n  \"~\": {template: river}\n  \".\": grass\ntemplates:\n  river: {walkable: false}\n  grass: {short: a meadow, long: [[Grass grows here.]]}\n";
            var generated = MUD.Core.Generation.MapGenerator.Generate(map, "elsewhere", library);
            Assert.True(generated.Success, string.Join("; ", generated.Errors));
            Assert.DoesNotContain("river flows", generated.Yaml);
            Assert.DoesNotContain("running water", generated.Yaml);
            Assert.Contains("Grass grows here.", generated.Yaml);
        }
    }

    public class TerrainDocumentTests
    {
        [Fact]
        public void EntitiesRoundTripThroughTheirDocuments_WithTheirOneTerrain()
        {
            var feature = new FeatureTemplate
            {
                Id = "b", Terrain = "demo.grass", Short = "s", Long = "l", Handles = new List<string> { "h" }, Adjectives = new List<string> { "a" },
                RoomLine = "r", Detail = "d", Weight = 3, Chance = 0.25, Version = 4, Tags = new List<string> { "t" },
            };
            BsonDocument document = LibraryDocuments.ToDocument(feature);
            Assert.Equal("b", document["_id"].AsString);
            Assert.Equal("demo.grass", document["terrain"].AsString);
            Assert.False(document.Contains("terrains"));   // a document that was never legacy has no retired array
            FeatureTemplate back = LibraryDocuments.FeatureFrom(document);
            Assert.Equal("demo.grass", back.Terrain);
            Assert.Equal((feature.Short, feature.Long, feature.RoomLine, feature.Detail, feature.Weight, feature.Chance, feature.Version),
                (back.Short, back.Long, back.RoomLine, back.Detail, back.Weight, back.Chance, back.Version));

            var fragment = new DescriptionFragment { Id = "f", Terrain = "demo.forest", Slot = "ground", Text = "x", Weight = 0, Tags = new List<string> { "wet" } };
            DescriptionFragment fragmentBack = LibraryDocuments.FragmentFrom(LibraryDocuments.ToDocument(fragment));
            Assert.Equal("demo.forest", fragmentBack.Terrain);
            Assert.Equal(0, fragmentBack.Weight);

            var phrase = new NeighborPhrase { Id = "p", Terrain = "demo.grass", NeighborTerrain = "demo.river", Text = "{dir}", Weight = 2, Merge = false };
            NeighborPhrase phraseBack = LibraryDocuments.PhraseFrom(LibraryDocuments.ToDocument(phrase));
            Assert.False(phraseBack.Merge);
            Assert.Equal(("demo.grass", "demo.river"), (phraseBack.Terrain, phraseBack.NeighborTerrain));
        }

        [Fact]
        public void ALegacyDocumentIsReadWithoutATerrain_AndKeepsItsTermsArrayWhenWrittenBack()
        {
            var legacy = new BsonDocument { { "_id", "x" }, { "terrains", new BsonArray { "grass", "forest" } }, { "slot", "short" }, { "text", "t" }, { "weight", 1 }, { "tags", new BsonArray() } };
            DescriptionFragment read = LibraryDocuments.FragmentFrom(legacy);
            Assert.Null(read.Terrain);
            Assert.Equal(new[] { "grass", "forest" }, read.LegacyTerrains);
            read.Terrain = "demo.grass";
            BsonDocument written = LibraryDocuments.ToDocument(read);
            Assert.Equal("demo.grass", written["terrain"].AsString);
            Assert.Equal(new[] { "grass", "forest" }, written["terrains"].AsBsonArray.Select(v => v.AsString));
        }

        [Fact]
        public void TerrainTypesRoundTrip_AndTheAreaIsTheIdPrefix()
        {
            var t = new TerrainType { Id = "a.b.c_1", Name = "N", Description = "D" };
            Assert.Equal(("a.b", "c_1"), (t.Area, t.Local));
            BsonDocument d = TerrainTypeDocuments.ToDocument(t);
            Assert.Equal("a.b", d["area"].AsString);
            TerrainType back = TerrainTypeDocuments.From(d);
            Assert.Equal(("a.b.c_1", "N", "D"), (back.Id, back.Name, back.Description));
        }

        [Theory]
        [InlineData("sample_land.grass", null)]
        [InlineData("a.b.grass", null)]
        [InlineData("grass", "must be")]
        [InlineData(".grass", "must be")]
        [InlineData("sample_land.", "name part")]
        [InlineData("sample_land.Grass", "name part")]
        [InlineData("sample_land.gr ass", "name part")]
        [InlineData("Sample.grass", "area part")]
        [InlineData("", "required")]
        public void TerrainTypeIdRules(string id, string problem)
        {
            string result = TerrainTypeRules.CheckId(id);
            if (problem == null) { Assert.Null(result); } else { Assert.Contains(problem, result); }
        }
    }

    [Collection("World construction")]
    public class AdminLibraryApiTests
    {
        private class Host : IAdminHost
        {
            public bool Available = true;
            public string StartRoomKey { get { return "t.start"; } }
            public bool TryRunOnGameThread<T>(Func<T> work, out T result) { if (!Available) { result = default(T); return false; } result = work(); return true; }
            public bool IsThrottled(string remoteKey) { return false; }
            public void RecordLoginOutcome(string remoteKey, bool success) { }
            public bool IsOnline(Player player) { return false; }
        }

        private class Audit : IAuditStore
        {
            public List<AuditRecord> Rows = new List<AuditRecord>();
            public bool FailInsert;
            public void Insert(IReadOnlyList<AuditRecord> rows) { if (FailInsert) { throw new InvalidOperationException("audit down"); } Rows.AddRange(rows); }
            public void SetOutcome(IReadOnlyList<string> ids, bool applied) { foreach (var r in Rows.Where(r => ids.Contains(r.Id))) { r.Applied = applied; r.Rejected = !applied; } }
            public AuditPage Query(string entity, string entityId, int page, int pageSize) { return new AuditPage { Items = Rows, Total = Rows.Count }; }
        }

        private class NoQueries : IAdminQueries
        {
            public List<BsonDocument> ListPlayers(string nameFilter, bool descending, int page, int pageSize, out int total) { total = 0; return new List<BsonDocument>(); }
            public BsonDocument FindPlayer(string nameKey) { return null; }
            public int CountAdmins() { return 0; }
            public List<BsonDocument> ListRooms() { return new List<BsonDocument>(); }
            public List<string> RoomIdsWithExitTo(string key) { return new List<string>(); }
            public List<BsonDocument> RoomsWithTemplateReward(string templateId) { return new List<BsonDocument>(); }
        }

        private class Person : Player
        {
            public Person(string name, IPlayerRepository repository) : base(null, null, repository)
            {
                Id = "p." + name.ToLowerInvariant(); PlayerName = name; NameKey = name.ToLowerInvariant(); ShortDescription = name; Password = "secret1";
                Race = "human"; Gender = "female"; Class = "fighter";
            }
        }

        private class Fixture
        {
            public Host Host = new Host();
            public Audit Audit = new Audit();
            public SyncPlayerRepo Players = new SyncPlayerRepo();
            public AdminSessions Sessions = new AdminSessions();
            public FakeLibraryStore Store = new FakeLibraryStore { Version = 10 };
            public FakeAreaStore Areas = new FakeAreaStore().With("demo").With("other");
            public StoreLibrary Cache;
            public List<string> Log = new List<string>();
            public AdminApi Api;

            public Fixture(bool withLibrary = true)
            {
                Cache = new StoreLibrary(Store);
                Api = new AdminApi(Host, Players, new SyncRoomRepo(), new NoQueries(), Audit, Sessions, new FakeClock(), Log.Add, max => 0, null,
                    withLibrary ? Store : null, withLibrary ? Cache : null, null, Areas);
                foreach (string id in new[] { "demo.grass", "demo.forest", "demo.river", "other.grass" })
                {
                    Store.TerrainTypeTable.Items[id] = new TerrainType { Id = id, Name = id };
                }
            }

            public string Login(string name, bool creator = false, bool admin = false)
            {
                var person = new Person(name, Players);
                person.Roles.Creator = creator || admin;
                person.Roles.Admin = admin;
                Players.Live.Add(person);
                Players.Stored[person.Id] = person.ToBsonDocument();
                return Api.WebAuthenticate(new WebAuthRequest { Name = name, Password = "secret1", RemoteKey = "k" }).Token;
            }

            public string Creator() { return Login("Cora", creator: true); }
        }

        // A plain terrain name ("grass") means the terrain type of the demo area ("demo.grass"); an id with a '.' is used as given.
        private static string Scoped(string terrain) { return terrain.Contains('.') ? terrain : "demo." + terrain; }

        private static FragmentRecord Fragment(string id = "f1", string terrain = "grass")
        {
            return new FragmentRecord { Id = id, Terrain = Scoped(terrain), Slot = "ground", Text = "Grass.", Weight = 1 };
        }

        private static FeatureTemplateRecord Template(string id = "t1", string terrain = "grass")
        {
            var r = new FeatureTemplateRecord { Id = id, Terrain = Scoped(terrain), Short = "a rock", Long = "A rock.", Weight = 1, Chance = 0.5, Version = 99 };
            r.Handles.Add("rock");
            return r;
        }

        private static NeighborPhraseRecord Phrase(string id = "p1", string terrain = "grass", string neighbor = "river")
        {
            return new NeighborPhraseRecord { Id = id, Terrain = Scoped(terrain), NeighborTerrain = Scoped(neighbor), Text = "A river flows just {dir} of here.", Weight = 1, Merge = true };
        }

        private static AdminCallException Throws(Action call) { return Assert.Throws<AdminCallException>(call); }

        // ---------------------------------------------------------------- Ok

        [Fact]
        public void CreateFragment_Ok_StoresIt_AuditsFirst_BumpsTheVersionAndRefreshesTheSnapshot()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Cache.Refresh();

            LibraryEditReply reply = f.Api.CreateFragment(new SaveFragmentRequest { Token = token, Fragment = Fragment("f1", "grass") });

            Assert.Equal(AdminResult.Ok, reply.Result);
            Assert.Equal(11, reply.LibraryVersion);
            Assert.Equal("f1", reply.Fragment.Id);
            Assert.Equal(11, f.Store.Version);
            Assert.Equal("demo.grass", f.Store.FragmentTable.Items["f1"].Terrain);
            AuditRecord row = Assert.Single(f.Audit.Rows);
            Assert.Equal((AdminApi.FragmentEntity, "f1", "*create*", true), (row.Entity, row.EntityId, row.Field, row.Applied));
            Assert.Equal("Cora", row.ActorName);
            Assert.Single(f.Cache.Fragments("demo.grass", "ground"));   // generators see it without a restart
            Assert.Equal(11, f.Cache.Version);
        }

        [Fact]
        public void CreateAndUpdateAndDelete_WorkForAllThreeKinds()
        {
            var f = new Fixture();
            string token = f.Creator();

            Assert.Equal(AdminResult.Ok, f.Api.CreateFeatureTemplate(new SaveFeatureTemplateRequest { Token = token, Template = Template("t1", "grass") }).Result);
            Assert.Equal(AdminResult.Ok, f.Api.CreateNeighborPhrase(new SaveNeighborPhraseRequest { Token = token, Phrase = Phrase("p1", "grass") }).Result);
            var changedPhrase = Phrase("p1", "grass");
            changedPhrase.Weight = 5;
            LibraryEditReply updated = f.Api.UpdateNeighborPhrase(new SaveNeighborPhraseRequest { Token = token, Phrase = changedPhrase });
            Assert.Equal(AdminResult.Ok, updated.Result);
            Assert.Equal(5, updated.Phrase.Weight);
            Assert.Equal(AdminResult.Ok, f.Api.DeleteNeighborPhrase(new LibraryIdRequest { Token = token, Id = "p1" }).Result);
            Assert.Equal(AdminResult.Ok, f.Api.DeleteFeatureTemplate(new LibraryIdRequest { Token = token, Id = "t1" }).Result);
            Assert.Empty(f.Store.PhraseTable.Items);
            Assert.Empty(f.Store.FeatureTable.Items);
            Assert.Equal(15, f.Store.Version);   // five writes
        }

        [Fact]
        public void UpdateFragment_AuditsOneRowPerChangedField_WithOldAndNew()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Api.CreateFragment(new SaveFragmentRequest { Token = token, Fragment = Fragment("f1", "grass") });
            f.Audit.Rows.Clear();

            var edit = Fragment("f1", "forest");
            edit.Text = "New text.";
            LibraryEditReply reply = f.Api.UpdateFragment(new SaveFragmentRequest { Token = token, Fragment = edit });

            Assert.Equal(AdminResult.Ok, reply.Result);
            Assert.Equal(new[] { "terrain", "text" }, f.Audit.Rows.Select(r => r.Field).OrderBy(x => x));
            AuditRecord text = f.Audit.Rows.Single(r => r.Field == "text");
            Assert.Equal(("Grass.", "New text."), (text.Old.AsString, text.New.AsString));
            Assert.All(f.Audit.Rows, r => Assert.True(r.Applied));
        }

        [Fact]
        public void Get_AndList_ReturnTheStoredEntities_AndGetOfAnUnknownIdIsNotFound()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Api.CreateFragment(new SaveFragmentRequest { Token = token, Fragment = Fragment("b") });
            f.Api.CreateFragment(new SaveFragmentRequest { Token = token, Fragment = Fragment("a", "grass") });

            ListFragmentsReply list = f.Api.ListFragments(new ListLibraryRequest { Token = token });
            Assert.Equal(new[] { "a", "b" }, list.Fragments.Select(x => x.Id));
            Assert.Equal(12, list.LibraryVersion);
            Assert.True(f.Api.GetFragment(new LibraryIdRequest { Token = token, Id = "a" }).Found);
            Assert.False(f.Api.GetFragment(new LibraryIdRequest { Token = token, Id = "zzz" }).Found);
            Assert.Empty(f.Api.ListFeatureTemplates(new ListLibraryRequest { Token = token }).Templates);
            Assert.False(f.Api.GetNeighborPhrase(new LibraryIdRequest { Token = token, Id = "x" }).Found);
        }

        // ---------------------------------------------------------------- scoping (exactly one terrain type)

        [Fact]
        public void ATerrainIsRequired_AnEmptyOneIsInvalid_ForEveryKind()
        {
            var f = new Fixture();
            string token = f.Creator();
            var frag = Fragment("x"); frag.Terrain = "";
            var tmpl = Template("x"); tmpl.Terrain = "";
            var phrase = Phrase("x"); phrase.Terrain = "";
            Assert.Contains(f.Api.CreateFragment(new SaveFragmentRequest { Token = token, Fragment = frag }).Errors, e => e.Field == "terrain");
            Assert.Contains(f.Api.CreateFeatureTemplate(new SaveFeatureTemplateRequest { Token = token, Template = tmpl }).Errors, e => e.Field == "terrain");
            Assert.Contains(f.Api.CreateNeighborPhrase(new SaveNeighborPhraseRequest { Token = token, Phrase = phrase }).Errors, e => e.Field == "terrain");
            Assert.Empty(f.Store.FragmentTable.Items);
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void ATerrainThatIsNotATerrainTypeIdIsInvalid_AndAnUnknownTerrainTypeIsInvalid()
        {
            var f = new Fixture();
            string token = f.Creator();
            var plain = Fragment("x"); plain.Terrain = "grass";          // the plain name is no longer a terrain
            Assert.Contains(f.Api.CreateFragment(new SaveFragmentRequest { Token = token, Fragment = plain }).Errors, e => e.Field == "terrain");
            var unknown = Fragment("x", "demo.swamp");                    // well formed, no such terrain type
            LibraryEditReply reply = f.Api.CreateFragment(new SaveFragmentRequest { Token = token, Fragment = unknown });
            Assert.Equal(AdminResult.Invalid, reply.Result);
            Assert.Equal((("terrain", AdminApi.UnknownTerrainMessage)), (reply.Errors[0].Field, reply.Errors[0].Message));
            Assert.Equal(AdminResult.Invalid, f.Api.CreateFeatureTemplate(new SaveFeatureTemplateRequest { Token = token, Template = Template("x", "nope.grass") }).Result);
            Assert.Empty(f.Store.FragmentTable.Items);
            Assert.Empty(f.Audit.Rows);
            Assert.Equal(10, f.Store.Version);
        }

        [Fact]
        public void AReplyCarriesTheTerrainAndTheAreaOfTheTerrain()
        {
            var f = new Fixture();
            string token = f.Creator();
            LibraryEditReply reply = f.Api.CreateFragment(new SaveFragmentRequest { Token = token, Fragment = Fragment("f1", "other.grass") });
            Assert.Equal(("other.grass", "other"), (reply.Fragment.Terrain, reply.Fragment.Area));
            Assert.Equal(("other.grass", "other"), (f.Api.GetFragment(new LibraryIdRequest { Token = token, Id = "f1" }).Fragment.Terrain, f.Api.GetFragment(new LibraryIdRequest { Token = token, Id = "f1" }).Fragment.Area));
        }

        [Fact]
        public void APhraseNeighbourMustExist_AndBeInTheSameAreaAsItsTerrain()
        {
            var f = new Fixture();
            string token = f.Creator();
            LibraryEditReply otherArea = f.Api.CreateNeighborPhrase(new SaveNeighborPhraseRequest { Token = token, Phrase = Phrase("p", "demo.grass", "other.grass") });
            Assert.Equal(AdminResult.Invalid, otherArea.Result);
            Assert.Equal(("neighborTerrain", AdminApi.NeighborOtherAreaMessage), (otherArea.Errors[0].Field, otherArea.Errors[0].Message));
            LibraryEditReply unknown = f.Api.CreateNeighborPhrase(new SaveNeighborPhraseRequest { Token = token, Phrase = Phrase("p", "demo.grass", "demo.swamp") });
            Assert.Equal(("neighborTerrain", AdminApi.UnknownTerrainMessage), (unknown.Errors[0].Field, unknown.Errors[0].Message));
            Assert.Equal(AdminResult.Ok, f.Api.CreateNeighborPhrase(new SaveNeighborPhraseRequest { Token = token, Phrase = Phrase("p", "demo.grass", "demo.river") }).Result);
            Assert.Single(f.Store.PhraseTable.Items);
        }

        [Fact]
        public void UpdateMayMoveAnEntryToAnotherTerrain_AndAMovedPhraseKeepsItsIdAndIsAuditedAsATerrainChange()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Api.CreateFragment(new SaveFragmentRequest { Token = token, Fragment = Fragment("f1", "demo.grass") });
            f.Audit.Rows.Clear();
            LibraryEditReply moved = f.Api.UpdateFragment(new SaveFragmentRequest { Token = token, Fragment = Fragment("f1", "demo.forest") });
            Assert.Equal(AdminResult.Ok, moved.Result);
            Assert.Equal("demo.forest", f.Store.FragmentTable.Items["f1"].Terrain);
            AuditRecord row = Assert.Single(f.Audit.Rows);
            Assert.Equal(("terrain", "demo.grass", "demo.forest"), (row.Field, row.Old.AsString, row.New.AsString));
            Assert.Empty(f.Cache.Fragments("demo.grass", "ground"));
            Assert.Single(f.Cache.Fragments("demo.forest", "ground"));
        }

        [Fact]
        public void AMovedPhraseMustKeepItsNeighbourInTheNewTerrainsArea()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Api.CreateNeighborPhrase(new SaveNeighborPhraseRequest { Token = token, Phrase = Phrase("p", "demo.grass", "demo.river") });
            f.Audit.Rows.Clear();

            LibraryEditReply refused = f.Api.UpdateNeighborPhrase(new SaveNeighborPhraseRequest { Token = token, Phrase = Phrase("p", "other.grass", "demo.river") });
            Assert.Equal(AdminResult.Invalid, refused.Result);
            Assert.Equal("neighborTerrain", refused.Errors[0].Field);
            Assert.Equal("demo.grass", f.Store.PhraseTable.Items["p"].Terrain);
            Assert.Empty(f.Audit.Rows);

            Assert.Equal(AdminResult.Ok, f.Api.UpdateNeighborPhrase(new SaveNeighborPhraseRequest { Token = token, Phrase = Phrase("p", "demo.forest", "demo.river") }).Result);
            Assert.Equal("demo.forest", f.Store.PhraseTable.Items["p"].Terrain);
        }

        [Fact]
        public void TheListsFilterByTerrainAndByArea_AndCarryTheScopeOfEveryRow()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Api.CreateFragment(new SaveFragmentRequest { Token = token, Fragment = Fragment("a", "demo.grass") });
            f.Api.CreateFragment(new SaveFragmentRequest { Token = token, Fragment = Fragment("b", "demo.forest") });
            f.Api.CreateFragment(new SaveFragmentRequest { Token = token, Fragment = Fragment("c", "other.grass") });
            f.Api.CreateFeatureTemplate(new SaveFeatureTemplateRequest { Token = token, Template = Template("t", "other.grass") });
            f.Api.CreateNeighborPhrase(new SaveNeighborPhraseRequest { Token = token, Phrase = Phrase("p", "demo.grass", "demo.river") });

            Assert.Equal(new[] { "a", "b", "c" }, f.Api.ListFragments(new ListLibraryRequest { Token = token }).Fragments.Select(x => x.Id));
            Assert.Equal(new[] { "b" }, f.Api.ListFragments(new ListLibraryRequest { Token = token, Terrain = "demo.forest" }).Fragments.Select(x => x.Id));
            Assert.Equal(new[] { "a", "b" }, f.Api.ListFragments(new ListLibraryRequest { Token = token, Area = "demo" }).Fragments.Select(x => x.Id));
            Assert.Empty(f.Api.ListFragments(new ListLibraryRequest { Token = token, Area = "demo", Terrain = "other.grass" }).Fragments);
            Assert.Equal(new[] { "t" }, f.Api.ListFeatureTemplates(new ListLibraryRequest { Token = token, Area = "other" }).Templates.Select(x => x.Id));
            Assert.Empty(f.Api.ListFeatureTemplates(new ListLibraryRequest { Token = token, Area = "demo" }).Templates);
            var phrase = Assert.Single(f.Api.ListNeighborPhrases(new ListLibraryRequest { Token = token, Terrain = "demo.grass" }).Phrases);
            Assert.Equal(("demo.grass", "demo", "demo.river"), (phrase.Terrain, phrase.Area, phrase.NeighborTerrain));
        }

        [Fact]
        public void AnUpdateKeepsTheLegacyTermsArrayOfAMigratedDocument()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Store.FragmentTable.Items["m"] = new DescriptionFragment { Id = "m", Terrain = "demo.grass", LegacyTerrains = new List<string> { "grass", "forest" }, Slot = "ground", Text = "Old.", Weight = 1 };
            var edit = Fragment("m", "demo.grass"); edit.Text = "New.";
            Assert.Equal(AdminResult.Ok, f.Api.UpdateFragment(new SaveFragmentRequest { Token = token, Fragment = edit }).Result);
            Assert.Equal(new[] { "grass", "forest" }, f.Store.FragmentTable.Items["m"].LegacyTerrains);
        }

        // ---------------------------------------------------------------- feature template version

        [Fact]
        public void TemplateEdits_BumpTheVersion_OnlyWhenTheTextOrHandlesChange_AndTheClientVersionIsIgnored()
        {
            var f = new Fixture();
            string token = f.Creator();
            LibraryEditReply created = f.Api.CreateFeatureTemplate(new SaveFeatureTemplateRequest { Token = token, Template = Template("t1", "grass") });
            Assert.Equal(1, created.Template.Version);   // the client sent 99

            var weightOnly = Template("t1", "grass");
            weightOnly.Weight = 7; weightOnly.Chance = 0.9; weightOnly.Tags.Add("x"); weightOnly.Version = 50;
            Assert.Equal(1, f.Api.UpdateFeatureTemplate(new SaveFeatureTemplateRequest { Token = token, Template = weightOnly }).Template.Version);

            var textEdit = Template("t1", "grass");
            textEdit.Long = "A different rock."; textEdit.Weight = 7; textEdit.Chance = 0.9; textEdit.Tags.Add("x");
            Assert.Equal(2, f.Api.UpdateFeatureTemplate(new SaveFeatureTemplateRequest { Token = token, Template = textEdit }).Template.Version);

            var handlesEdit = Template("t1", "grass");
            handlesEdit.Long = "A different rock."; handlesEdit.Weight = 7; handlesEdit.Chance = 0.9; handlesEdit.Tags.Add("x"); handlesEdit.Handles.Add("stone");
            Assert.Equal(3, f.Api.UpdateFeatureTemplate(new SaveFeatureTemplateRequest { Token = token, Template = handlesEdit }).Template.Version);
            Assert.Equal(3, f.Store.FeatureTable.Items["t1"].Version);
            Assert.Equal(3, f.Cache.FeatureById("t1").Version);   // update of rooms compares against this
        }

        [Fact]
        public void SavingATemplateWithNothingChanged_IsNoChange_NoAudit_NoVersionBump()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Api.CreateFeatureTemplate(new SaveFeatureTemplateRequest { Token = token, Template = Template("t1", "grass") });
            int audits = f.Audit.Rows.Count, version = f.Store.Version.Value;

            LibraryEditReply reply = f.Api.UpdateFeatureTemplate(new SaveFeatureTemplateRequest { Token = token, Template = Template("t1", "grass") });

            Assert.Equal(AdminResult.NoChange, reply.Result);
            Assert.Equal(1, reply.Template.Version);
            Assert.Equal(audits, f.Audit.Rows.Count);
            Assert.Equal(version, f.Store.Version);
        }

        // ---------------------------------------------------------------- AlreadyExists

        [Fact]
        public void Create_OfATakenId_IsAlreadyExists_AndChangesNothing()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Api.CreateFragment(new SaveFragmentRequest { Token = token, Fragment = Fragment("f1", "grass") });
            int audits = f.Audit.Rows.Count, version = f.Store.Version.Value;

            LibraryEditReply reply = f.Api.CreateFragment(new SaveFragmentRequest { Token = token, Fragment = Fragment("f1", "forest") });

            Assert.Equal(AdminResult.AlreadyExists, reply.Result);
            Assert.Equal(AdminApi.LibraryAlreadyExistsMessage, reply.Message);
            Assert.Equal("demo.grass", f.Store.FragmentTable.Items["f1"].Terrain);
            Assert.Equal(audits, f.Audit.Rows.Count);
            Assert.Equal(version, f.Store.Version);
        }

        [Fact]
        public void Create_LosingARaceForTheIdAfterTheAudit_IsAlreadyExists_AndTheAuditRowIsRejected()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Store.FragmentTable.RaceOnWrite = true;

            LibraryEditReply reply = f.Api.CreateFragment(new SaveFragmentRequest { Token = token, Fragment = Fragment("f1") });

            Assert.Equal(AdminResult.AlreadyExists, reply.Result);
            Assert.True(Assert.Single(f.Audit.Rows).Rejected);
            Assert.Equal(10, f.Store.Version);
        }

        // ---------------------------------------------------------------- NotFound

        [Fact]
        public void UpdateAndDelete_OfAnUnknownId_AreNotFound_ForEveryKind()
        {
            var f = new Fixture();
            string token = f.Creator();
            Assert.Equal(AdminResult.NotFound, f.Api.UpdateFragment(new SaveFragmentRequest { Token = token, Fragment = Fragment("zz") }).Result);
            Assert.Equal(AdminResult.NotFound, f.Api.UpdateFeatureTemplate(new SaveFeatureTemplateRequest { Token = token, Template = Template("zz") }).Result);
            Assert.Equal(AdminResult.NotFound, f.Api.UpdateNeighborPhrase(new SaveNeighborPhraseRequest { Token = token, Phrase = Phrase("zz") }).Result);
            Assert.Equal(AdminResult.NotFound, f.Api.DeleteFragment(new LibraryIdRequest { Token = token, Id = "zz" }).Result);
            Assert.Equal(AdminResult.NotFound, f.Api.DeleteFeatureTemplate(new LibraryIdRequest { Token = token, Id = "zz" }).Result);
            Assert.Equal(AdminResult.NotFound, f.Api.DeleteNeighborPhrase(new LibraryIdRequest { Token = token, Id = "" }).Result);
            Assert.Empty(f.Audit.Rows);
            Assert.Equal(10, f.Store.Version);
        }

        [Fact]
        public void Delete_LosingARaceAfterTheAudit_IsNotFound_AndTheAuditRowIsRejected()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Store.PhraseTable.Items["p1"] = new NeighborPhrase { Id = "p1", Terrain = "demo.grass" };
            f.Store.PhraseTable.RaceOnWrite = true;

            LibraryEditReply reply = f.Api.DeleteNeighborPhrase(new LibraryIdRequest { Token = token, Id = "p1" });

            Assert.Equal(AdminResult.NotFound, reply.Result);
            Assert.True(Assert.Single(f.Audit.Rows).Rejected);
        }

        [Fact]
        public void Delete_RecordsTheOldValueInTheAudit_AndTheNextUpdateRunSeesTheTemplateGone()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Api.CreateFeatureTemplate(new SaveFeatureTemplateRequest { Token = token, Template = Template("t1", "grass") });
            Assert.NotNull(f.Cache.FeatureById("t1"));
            f.Audit.Rows.Clear();

            Assert.Equal(AdminResult.Ok, f.Api.DeleteFeatureTemplate(new LibraryIdRequest { Token = token, Id = "t1" }).Result);

            AuditRecord row = Assert.Single(f.Audit.Rows);
            Assert.Equal("*delete*", row.Field);
            Assert.Equal("a rock", row.Old.AsBsonDocument["short"].AsString);
            Assert.True(row.New.IsBsonNull);
            Assert.Null(f.Cache.FeatureById("t1"));   // "template deleted" case of update
        }

        // ---------------------------------------------------------------- Refused

        [Fact]
        public void WithoutALibraryStore_EveryWriteIsRefused_AndReadsAreEmpty()
        {
            var f = new Fixture(withLibrary: false);
            string token = f.Creator();

            Assert.Equal(AdminResult.Refused, f.Api.CreateFragment(new SaveFragmentRequest { Token = token, Fragment = Fragment() }).Result);
            LibraryEditReply refused = f.Api.DeleteFeatureTemplate(new LibraryIdRequest { Token = token, Id = "x" });
            Assert.Equal((AdminResult.Refused, AdminApi.LibraryUnavailableMessage), (refused.Result, refused.Message));
            Assert.Equal(AdminResult.Refused, f.Api.UpdateNeighborPhrase(new SaveNeighborPhraseRequest { Token = token, Phrase = Phrase() }).Result);
            Assert.Empty(f.Api.ListFragments(new ListLibraryRequest { Token = token }).Fragments);
            Assert.False(f.Api.GetFeatureTemplate(new LibraryIdRequest { Token = token, Id = "x" }).Found);
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void APlayerWhoIsNotACreator_IsForbidden_ForEveryCall_AndNothingIsAudited()
        {
            var f = new Fixture();
            string token = f.Login("Pat");
            Assert.Equal(AdminFailure.Forbidden, Throws(() => f.Api.ListFragments(new ListLibraryRequest { Token = token })).Failure);
            Assert.Equal(AdminFailure.Forbidden, Throws(() => f.Api.GetFeatureTemplate(new LibraryIdRequest { Token = token, Id = "x" })).Failure);
            Assert.Equal(AdminFailure.Forbidden, Throws(() => f.Api.CreateFragment(new SaveFragmentRequest { Token = token, Fragment = Fragment() })).Failure);
            Assert.Equal(AdminFailure.Forbidden, Throws(() => f.Api.DeleteNeighborPhrase(new LibraryIdRequest { Token = token, Id = "x" })).Failure);
            Assert.Empty(f.Audit.Rows);
            Assert.Empty(f.Store.FragmentTable.Items);
        }

        [Fact]
        public void AnUnknownToken_IsUnauthenticated()
        {
            var f = new Fixture();
            Assert.Equal(AdminFailure.Unauthenticated, Throws(() => f.Api.CreateFragment(new SaveFragmentRequest { Token = "nope", Fragment = Fragment() })).Failure);
            Assert.Equal(AdminFailure.Unauthenticated, Throws(() => f.Api.ListNeighborPhrases(new ListLibraryRequest { Token = "" })).Failure);
        }

        [Fact]
        public void AnAdminMayEditToo()
        {
            var f = new Fixture();
            string token = f.Login("Ada", admin: true);
            Assert.Equal(AdminResult.Ok, f.Api.CreateFragment(new SaveFragmentRequest { Token = token, Fragment = Fragment("f1") }).Result);
            Assert.Equal("Admin", f.Audit.Rows.Single().ActorRole);
        }

        // ---------------------------------------------------------------- Invalid / audit / failure

        [Fact]
        public void InvalidInput_IsReportedPerField_AndNothingIsWrittenOrAudited()
        {
            var f = new Fixture();
            string token = f.Creator();

            LibraryEditReply badPhrase = f.Api.CreateNeighborPhrase(new SaveNeighborPhraseRequest { Token = token, Phrase = new NeighborPhraseRecord { Id = "p", NeighborTerrain = "river", Text = "no placeholder" } });
            Assert.Equal(AdminResult.Invalid, badPhrase.Result);
            Assert.Contains(badPhrase.Errors, e => e.Field == "text");

            var badTemplate = Template("t", "grass");
            badTemplate.Chance = 2;
            Assert.Contains(f.Api.CreateFeatureTemplate(new SaveFeatureTemplateRequest { Token = token, Template = badTemplate }).Errors, e => e.Field == "chance");

            var badFragment = Fragment("bad id!", "grass");
            badFragment.Weight = -1;
            LibraryEditReply fragmentReply = f.Api.CreateFragment(new SaveFragmentRequest { Token = token, Fragment = badFragment });
            Assert.Equal(new[] { "id", "weight" }, fragmentReply.Errors.Select(e => e.Field).OrderBy(x => x));

            Assert.Equal(AdminResult.Invalid, f.Api.CreateFragment(new SaveFragmentRequest { Token = token }).Result);
            Assert.Empty(f.Audit.Rows);
            Assert.Equal(10, f.Store.Version);
        }

        [Fact]
        public void WhenTheAuditCannotBeWritten_TheEditIsRefusedAsAuditFailed_AndNothingChanges()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Audit.FailInsert = true;

            LibraryEditReply reply = f.Api.CreateFragment(new SaveFragmentRequest { Token = token, Fragment = Fragment("f1", "grass") });

            Assert.Equal(AdminResult.AuditFailed, reply.Result);
            Assert.Empty(f.Store.FragmentTable.Items);
            Assert.Equal(10, f.Store.Version);
            Assert.Contains(f.Log, l => l.Contains("Audit write failed"));
        }

        [Fact]
        public void WhenTheDatabaseWriteFails_TheResultIsFailed_TheAuditRowIsRejected_AndNoValuesAreLogged()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Store.FragmentTable.FailWrites = true;

            LibraryEditReply reply = f.Api.CreateFragment(new SaveFragmentRequest { Token = token, Fragment = Fragment("secret-id", "grass") });

            Assert.Equal((AdminResult.Failed, AdminApi.LibraryWriteFailedMessage), (reply.Result, reply.Message));
            Assert.True(Assert.Single(f.Audit.Rows).Rejected);
            Assert.DoesNotContain(f.Log, l => l.Contains("secret-id") || l.Contains("db down"));
            Assert.Equal(10, f.Store.Version);
        }

        [Fact]
        public void WhenTheDatabaseCannotBeRead_AListIsUnavailable_NotAnEmptyAnswer()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Store.FragmentTable.FailReads = true;

            Assert.Equal(AdminFailure.Unavailable, Throws(() => f.Api.ListFragments(new ListLibraryRequest { Token = token })).Failure);
            Assert.Equal(AdminFailure.Unavailable, Throws(() => f.Api.GetFragment(new LibraryIdRequest { Token = token, Id = "x" })).Failure);
            Assert.Equal(AdminResult.Failed, f.Api.CreateFragment(new SaveFragmentRequest { Token = token, Fragment = Fragment() }).Result);
        }

        [Fact]
        public void WhenTheGameThreadIsNotAvailable_WritesAreUnavailable_AndNothingIsAudited()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Host.Available = false;

            Assert.Equal(AdminFailure.Unavailable, Throws(() => f.Api.CreateFragment(new SaveFragmentRequest { Token = token, Fragment = Fragment() })).Failure);
            Assert.Equal(AdminFailure.Unavailable, Throws(() => f.Api.DeleteFeatureTemplate(new LibraryIdRequest { Token = token, Id = "x" })).Failure);
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void TheLibraryCallsAreMappedToGrpcStatusesByTheService()
        {
            var f = new Fixture();
            var service = new AdminService(f.Api);
            var ex = Assert.ThrowsAsync<Grpc.Core.RpcException>(() => service.CreateFragment(new SaveFragmentRequest { Token = "nope", Fragment = Fragment() }, null));
            Assert.Equal(Grpc.Core.StatusCode.Unauthenticated, ex.GetAwaiter().GetResult().StatusCode);
        }
    }
}
