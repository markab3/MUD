using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MongoDB.Bson;
using MUD.Core.Generation;
using MUD.Core.Generation.Terrains;
using MUD.Core.Import;
using Xunit;

namespace MUD.Tests
{
    // Card t_d4afabb3: the library wired into map generation, and map re-import (specs/TERRAIN_GENERATORS.md 9.2).
    // In-memory store and library only: nothing here ran against MongoDB.
    public class MapReimportTests
    {
        private static InMemoryLibrary SeedLibrary()
        {
            var errors = new List<string>();
            var data = LibrarySeed.Parse(LibrarySeed.ReadEmbedded(), errors, new List<string>());
            Assert.Empty(errors);
            var lib = new InMemoryLibrary { Version = data.LibraryVersion };
            lib.FragmentList.AddRange(data.Fragments);
            lib.FeatureList.AddRange(data.Features);
            lib.PhraseList.AddRange(data.Phrases);
            return lib;
        }

        private static string SampleMapPath()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "MUD.Main", "Content", "maps", "sample_land.map.yaml");
                if (File.Exists(candidate)) { return candidate; }
                dir = dir.Parent;
            }
            throw new FileNotFoundException("sample_land.map.yaml not found above " + AppContext.BaseDirectory);
        }

        private static GenerateResult GenerateSample(ILibrary lib, string map = null)
        {
            var result = MapGenerator.Generate(map ?? File.ReadAllText(SampleMapPath()), "sample_land", lib);
            Assert.True(result.Success, string.Join("; ", result.Errors));
            return result;
        }

        [Fact]
        public void SampleMapGeneratesAllFourTerrainsFromTheSeedLibrary()
        {
            var result = GenerateSample(SeedLibrary());
            var parsed = AreaParser.Parse(result.Yaml);
            Assert.Empty(parsed.Errors);
            var terrains = parsed.Area.Rooms.Select(r => r.Gen.Terrain).Distinct().OrderBy(t => t).ToList();
            Assert.Equal(new[] { "forest", "grass", "ocean", "river" }, terrains);
            Assert.All(parsed.Area.Rooms, r => Assert.Equal("sample_land", r.Gen.Map));
            // Library text reached the rooms: neighbour phrases, and features that carry their template reference.
            Assert.Contains(parsed.Area.Rooms, r => r.Long.Contains("A river flows just"));
            Assert.Contains(parsed.Area.Rooms, r => r.Long.Contains("sea"));
            Assert.Contains(parsed.Area.Rooms.SelectMany(r => r.Features), f => f.TemplateId != null && f.TemplateVersion.HasValue);
            // The colour never reaches room data.
            Assert.DoesNotContain("#", result.Yaml.Replace("# ", ""));
            Assert.Equal("#3A7BD5", result.Colors["river"]);
        }

        [Fact]
        public void SampleMapIsDeterministicAndOnlyTheLibraryVersionChangesProvenance()
        {
            Assert.Equal(GenerateSample(SeedLibrary()).Yaml, GenerateSample(SeedLibrary()).Yaml);
            var newer = SeedLibrary();
            newer.Version = 9;
            var yaml = GenerateSample(newer).Yaml;
            Assert.Contains("libVersion: 9", yaml);
            Assert.Equal(GenerateSample(SeedLibrary()).Yaml.Replace("libVersion: 1", "libVersion: 9"), yaml);
        }

        [Fact]
        public void EmptyTerrainsEntitiesAreNeverUsedByTheSampleMap()
        {
            var lib = SeedLibrary();
            lib.FeatureList.Add(new FeatureTemplate { Id = "unassigned", Short = "a zzz thing", Long = "Zzz.", Chance = 1.0, Weight = 1000 });
            lib.PhraseList.Add(new NeighborPhrase { Id = "unassigned-p", NeighborTerrain = "river", Text = "Zzz {dir}.", Weight = 1000 });
            Assert.DoesNotContain("zzz", GenerateSample(lib).Yaml, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void PhaseOneParsesProvenanceIntoTheDefinition()
        {
            var parsed = AreaParser.Parse(GenerateSample(SeedLibrary()).Yaml);
            var room = parsed.Area.Rooms.First(r => r.Gen.Terrain == "grass");
            Assert.Equal("sample-land-1", room.Gen.Seed);
            Assert.Equal(1, room.Gen.LibVersion);
        }

        [Fact]
        public void BadProvenanceFailsPhaseOne()
        {
            const string yaml = @"
area: {schema_version: 1, prefix: t, name: T}
rooms:
  - key: a
    short: a
    long: |
      Long.
    x-gen: {map: m, bogus: 1}
    features:
      - short: f
        long: F.
        x-gen: {templateId: x}
";
            var errors = AreaParser.Parse(yaml).Errors;
            Assert.Contains(errors, e => e.Contains("unknown key 'bogus'"));
            Assert.Contains(errors, e => e.Contains("templateVersion is required"));
        }

        private static ImportOptions Replace { get { return new ImportOptions { ReplaceGenerated = true }; } }

        private static string FirstWithFeature(FakeStore store)
        {
            return store.Docs.Values.First(d => d["Features"].AsBsonArray.Count > 0)["_id"].AsString;
        }

        // Finds a seed value (for the library) whose sample map yields at least one generated feature, so the tests below
        // are not at the mercy of one particular roll.
        private static string SeedWithFeatures()
        {
            var lib = SeedLibrary();
            var map = File.ReadAllText(SampleMapPath());
            for (int i = 0; i < 200; i++)
            {
                var candidate = map.Replace("sample-land-1", "sample-land-" + i);
                var parsed = AreaParser.Parse(GenerateSample(lib, candidate).Yaml);
                if (parsed.Area.Rooms.Count(r => r.Features.Count > 0) >= 2) { return candidate; }
            }
            throw new InvalidOperationException("no seed gave features");
        }

        [Fact]
        public void FirstImportStoresProvenanceAndTemplateReferences()
        {
            var store = new FakeStore();
            var result = new AreaImporter(store).Import(GenerateSample(SeedLibrary(), SeedWithFeatures()).Yaml, Replace);
            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.All(store.Docs.Values, d => Assert.Equal("sample_land", d["gen"]["map"].AsString));
            var feature = store.Docs[FirstWithFeature(store)]["Features"][0].AsBsonDocument;
            Assert.True(feature.Contains("TemplateId") && feature.Contains("TemplateVersion"));
        }

        [Fact]
        public void ReimportReplacesDescriptionFeaturesPropertiesAndBehaviorsAndKeepsExitsAndItems()
        {
            var yaml = GenerateSample(SeedLibrary(), SeedWithFeatures()).Yaml;
            var store = new FakeStore();
            var importer = new AreaImporter(store);
            Assert.True(importer.Import(yaml, Replace).Success);

            var key = FirstWithFeature(store);
            var generated = store.Docs[key].DeepClone().AsBsonDocument;

            // A creator edits the room by hand, and a player has dropped an item there.
            var doc = store.Docs[key];
            doc["LongDescription"] = "Hand written.";
            doc["Features"].AsBsonArray.Add(new BsonDocument { { "_id", key + "#hand" }, { "ShortDescription", "Hand" }, { "LongDescription", "Added by hand." } });
            doc["Features"][0].AsBsonDocument["Behavior"] = "stale";
            doc["Features"][0].AsBsonDocument["TemplateVersion"] = 99;
            doc["search"] = new BsonDocument("entries", new BsonArray { new BsonDocument { { "id", "hand" }, { "target", "x" } } });
            doc["behaviors"] = new BsonArray { new BsonDocument { { "hook", "h" }, { "module", "m" }, { "type", "t" } } };
            doc["Items"] = new BsonArray { new BsonDocument("ShortDescription", "a coin") };

            var result = importer.Import(yaml, Replace);
            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.Contains(key, result.Replaced);
            Assert.Empty(result.NotOwned);

            var after = store.Docs[key];
            Assert.Equal(generated["LongDescription"], after["LongDescription"]);
            Assert.Equal(generated["Features"], after["Features"]);          // hand-added gone, stale fields gone, version reset
            Assert.Equal(generated["Exits"], after["Exits"]);
            Assert.Equal(0, after["behaviors"].AsBsonArray.Count);
            Assert.Equal(0, after["search"]["entries"].AsBsonArray.Count);
            Assert.Single(after["Items"].AsBsonArray);                        // state is never touched
        }

        [Fact]
        public void ReimportingTheSameOutputChangesNothing()
        {
            var yaml = GenerateSample(SeedLibrary()).Yaml;
            var store = new FakeStore();
            var importer = new AreaImporter(store);
            Assert.True(importer.Import(yaml, Replace).Success);
            var writes = store.UpsertCount;
            var again = importer.Import(yaml, Replace);
            Assert.Equal(0, again.Count(ImportAction.Update) + again.Count(ImportAction.Insert));
            Assert.Empty(again.Replaced);
            Assert.Equal(writes, store.UpsertCount);
        }

        [Fact]
        public void ChangingTheLibraryRegeneratesTheRoomsOnReimport()
        {
            var store = new FakeStore();
            var importer = new AreaImporter(store);
            Assert.True(importer.Import(GenerateSample(SeedLibrary()).Yaml, Replace).Success);

            var lib = SeedLibrary();
            lib.Version = 2;
            foreach (var f in lib.FragmentList.Where(f => f.Terrain == "sample_land.grass" && f.Slot == "ground")) { f.Text = "Edited ground text."; }
            var result = importer.Import(GenerateSample(lib).Yaml, Replace);
            Assert.True(result.Success);
            Assert.NotEmpty(result.Replaced);
            Assert.All(store.Docs.Values.Where(d => d["gen"]["terrain"] == "grass"), d =>
            {
                Assert.Contains("Edited ground text.", d["LongDescription"].AsString);
                Assert.Equal(2, d["gen"]["libVersion"].AsInt32);
            });
        }

        [Fact]
        public void HandAuthoredRoomAtAMapKeyIsNotReplacedAndIsReported()
        {
            var yaml = GenerateSample(SeedLibrary(), SeedWithFeatures()).Yaml;
            var parsed = AreaParser.Parse(yaml);
            var target = parsed.Area.Rooms.First(r => r.Features.Count > 0);
            var store = new FakeStore();
            store.Docs[target.FullKey] = new BsonDocument
            {
                { "_id", target.FullKey }, { "Area", "sample_land" },
                { "Features", new BsonArray { new BsonDocument { { "ShortDescription", target.Features[0].Short }, { "Extra", "kept" } } } },
            };

            var result = new AreaImporter(store).Import(yaml, Replace);
            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.Equal(new[] { target.FullKey }, result.NotOwned);
            Assert.DoesNotContain(target.FullKey, result.Replaced);
            // Normal merge: the stored element field survives.
            Assert.Equal("kept", store.Docs[target.FullKey]["Features"][0]["Extra"].AsString);
        }

        [Fact]
        public void RoomGeneratedByAnotherMapIsNotReplaced()
        {
            var yaml = GenerateSample(SeedLibrary()).Yaml;
            var store = new FakeStore();
            var importer = new AreaImporter(store);
            Assert.True(importer.Import(yaml, Replace).Success);
            var key = store.Docs.Keys.First();
            store.Docs[key]["gen"]["map"] = "some_other_map";

            var result = importer.Import(yaml, Replace);
            Assert.Equal(new[] { key }, result.NotOwned);
        }

        [Fact]
        public void PlainImportKeepsTheMergeAndNeverReplaces()
        {
            var yaml = GenerateSample(SeedLibrary(), SeedWithFeatures()).Yaml;
            var store = new FakeStore();
            var importer = new AreaImporter(store);
            Assert.True(importer.Import(yaml).Success);
            var key = FirstWithFeature(store);
            store.Docs[key]["Features"][0].AsBsonDocument["Extra"] = "kept";
            store.Docs[key]["behaviors"] = new BsonArray { new BsonDocument { { "hook", "h" }, { "module", "m" }, { "type", "t" } } };

            var result = importer.Import(yaml);
            Assert.True(result.Success);
            Assert.Empty(result.Replaced);
            Assert.Equal("kept", store.Docs[key]["Features"][0]["Extra"].AsString);
            Assert.Equal(1, store.Docs[key]["behaviors"].AsBsonArray.Count);
        }

        [Fact]
        public void AuthoredFileWithoutProvenanceNeverWritesGenAndIsNeverReplaced()
        {
            const string yaml = @"
area: {schema_version: 1, prefix: t, name: T}
rooms:
  - key: a
    short: a
    long: |
      Long.
    features:
      - {short: f, long: F.}
";
            var store = new FakeStore();
            var importer = new AreaImporter(store);
            Assert.True(importer.Import(yaml, Replace).Success);
            Assert.False(store.Docs["t.a"].Contains("gen"));
            store.Docs["t.a"]["Features"][0].AsBsonDocument["Extra"] = "kept";
            var result = importer.Import(yaml, Replace);
            Assert.Empty(result.NotOwned);
            Assert.Equal("kept", store.Docs["t.a"]["Features"][0]["Extra"].AsString);
        }

        [Fact]
        public void ReplaceKeepsExitsInStepWithTheMap()
        {
            var store = new FakeStore();
            var importer = new AreaImporter(store);
            var yaml = GenerateSample(SeedLibrary()).Yaml;
            Assert.True(importer.Import(yaml, Replace).Success);
            var key = store.Docs.Keys.First(k => store.Docs[k]["Exits"].AsBsonArray.Count > 0);
            store.Docs[key]["Exits"] = new BsonArray();
            Assert.True(importer.Import(yaml, Replace).Success);
            Assert.NotEmpty(store.Docs[key]["Exits"].AsBsonArray);
        }

        // ---- MapFileService / genmap ----

        private static string TempContent()
        {
            var dir = Path.Combine(Path.GetTempPath(), "mapsvc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, MapFileService.MapSubdirectory));
            File.Copy(SampleMapPath(), Path.Combine(dir, MapFileService.MapSubdirectory, "sample_land.map.yaml"));
            return dir;
        }

        [Fact]
        public void GenmapWithALibraryWritesGeneratedFileThatImports()
        {
            var dir = TempContent();
            try
            {
                var lines = new MapFileService(dir, () => SeedLibrary()).Generate("sample_land", false);
                Assert.Contains(lines, l => l.Contains("wrote"));
                var text = File.ReadAllText(Path.Combine(dir, "sample_land.yaml"));
                Assert.Contains("x-gen:", text);
                Assert.True(new AreaImporter(new FakeStore()).Import(text, Replace).Success);

                // Same inputs: nothing rewritten.
                Assert.Contains(new MapFileService(dir, () => SeedLibrary()).Generate("sample_land", false), l => l.Contains("unchanged"));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void GenmapWritesNothingWhenTheLibraryIsMissingOrUnreadable()
        {
            var dir = TempContent();
            try
            {
                var none = new MapFileService(dir, () => null).Generate("sample_land", false);
                Assert.Contains(none, l => l.Contains("not available"));
                var down = new MapFileService(dir, () => { throw new InvalidOperationException("db down: secret-connection-string"); }).Generate("sample_land", false);
                Assert.Contains(down, l => l.Contains("could not be read"));
                Assert.DoesNotContain(down, l => l.Contains("secret"));
                Assert.False(File.Exists(Path.Combine(dir, "sample_land.yaml")));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void GenmapWithoutALibraryProviderIsTheOldBehaviour()
        {
            var dir = TempContent();
            try
            {
                File.WriteAllText(Path.Combine(dir, MapFileService.MapSubdirectory, "plain.map.yaml"), @"
generator: {prefix: plain, name: P, seed: s}
grid: ['..']
legend: {'.': grass}
templates:
  grass: {short: a meadow, long: [[Grass.]]}
");
                new MapFileService(dir).Generate("plain", false);
                Assert.DoesNotContain("x-gen:", File.ReadAllText(Path.Combine(dir, "plain.yaml")));
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
