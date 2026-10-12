using System.IO;
using System.Linq;
using MongoDB.Bson;
using MUD.Core.Import;
using Xunit;

namespace MUD.Tests
{
    public class AreaExporterTests
    {
        // Covers reverse exits, a same-area and a cross-area exit, a feature with a search property,
        // a room property, coordinates, quoting and a multi-line description.
        private const string Area = @"
area:
  schema_version: 1
  prefix: t
  name: Test
rooms:
  - key: hall
    short: ""a hall: \""grand\""""
    long: |
      A plain hall.
        Indented line.
    handles: [hall]
    adjectives: [grand]
    coords: {x: 0, y: 0, z: 0}
    exits:
      - {dir: north, to: study, reverse: south}
      - {dir: up, to: landing}
      - {dir: out, to: town.gate}
    features:
      - short: a table
        long: A table.
        handles: [table]
        properties:
          - behavior: search
            id: f1
            params:
              target: table
              refresh_ms: 1000
              reward: {short: a coin, long: A coin.}
    properties:
      - behavior: search
        id: s1
        params:
          target: hall
          refresh_ms: 5000
          reward: {short: a sword, long: A sword., handles: [sword], plurals: [swords]}
  - key: study
    short: a study
    long: A study.
    coords: {x: 0, y: 1}
  - key: landing
    short: a landing
    long: A landing.
";

        // The fixture exits to town.gate in another area, which must already exist in the store.
        private static FakeStore Seeded()
        {
            var store = new FakeStore();
            store.Docs["town.gate"] = new BsonDocument { { "_id", "town.gate" }, { "Area", "town" } };
            return store;
        }

        private static BsonDocument Store(FakeStore store, string id)
        {
            return store.Docs[id];
        }

        // Asserts that every definition field matches between two stored rooms (spec section 5).
        private static void AssertSameDefinition(BsonDocument expected, BsonDocument actual)
        {
            foreach (var field in AreaImporter.DefinitionFields)
            {
                Assert.True(
                    expected.GetValue(field, BsonNull.Value).ToJson() == actual.GetValue(field, BsonNull.Value).ToJson(),
                    "Definition field differs: " + field + "\nexpected: " + expected.GetValue(field, BsonNull.Value).ToJson()
                    + "\nactual:   " + actual.GetValue(field, BsonNull.Value).ToJson());
            }
        }

        [Fact]
        public void ExportThenImportYieldsNoChanges()
        {
            var store = Seeded();
            var importer = new AreaImporter(store);
            Assert.True(importer.Import(Area).Success);

            var exported = new AreaExporter(store).ExportArea("t");
            Assert.True(exported.Success, string.Join("; ", exported.Errors));
            Assert.Equal(3, exported.RoomCount);

            var before = store.UpsertCount;
            var reimport = importer.Import(exported.Yaml);

            Assert.True(reimport.Success, string.Join("; ", reimport.Errors));
            Assert.Equal(3, reimport.Count(ImportAction.Unchanged));
            Assert.Equal(0, reimport.Count(ImportAction.Update));
            Assert.Equal(0, reimport.Count(ImportAction.Insert));
            Assert.Equal(before, store.UpsertCount);
        }

        [Fact]
        public void ExportedFileRebuildsTheSameDefinitionsInAFreshStore()
        {
            var original = Seeded();
            Assert.True(new AreaImporter(original).Import(Area).Success);

            var exported = new AreaExporter(original).ExportArea("t");
            Assert.True(exported.Success, string.Join("; ", exported.Errors));

            var fresh = Seeded();
            Assert.True(new AreaImporter(fresh).Import(exported.Yaml).Success);

            Assert.Equal(original.Docs.Keys.OrderBy(k => k).ToArray(), fresh.Docs.Keys.OrderBy(k => k).ToArray());
            foreach (var id in original.Docs.Keys)
            {
                AssertSameDefinition(Store(original, id), Store(fresh, id));
            }
        }

        [Fact]
        public void ExportWritesNoRuntimeState()
        {
            var store = Seeded();
            var importer = new AreaImporter(store);
            importer.Import(Area);

            // A dropped item and a persisted cooldown are state, and must not appear in the file.
            store.Docs["t.hall"]["Items"].AsBsonArray.Add(new BsonDocument { { "_id", "abc" }, { "ShortDescription", "a rope" } });
            store.Docs["t.hall"]["ExtendedProperties"].AsBsonArray[0].AsBsonDocument["NextRewardTicks"] = 12345L;

            var exported = new AreaExporter(store).ExportArea("t");

            Assert.True(exported.Success, string.Join("; ", exported.Errors));
            Assert.DoesNotContain("a rope", exported.Yaml);
            Assert.DoesNotContain("NextRewardTicks", exported.Yaml);
            Assert.DoesNotContain("Items", exported.Yaml);
        }

        [Fact]
        public void ExportRoomReturnsOnlyThatRoomAndItReimportsUnchanged()
        {
            var store = Seeded();
            var importer = new AreaImporter(store);
            importer.Import(Area);

            var exported = new AreaExporter(store).ExportRoom("t.hall");

            Assert.True(exported.Success, string.Join("; ", exported.Errors));
            Assert.Equal(1, exported.RoomCount);
            Assert.Equal(1, exported.Yaml.Split(new[] { "  - key: " }, System.StringSplitOptions.None).Length - 1);

            var reimport = importer.Import(exported.Yaml);
            Assert.True(reimport.Success, string.Join("; ", reimport.Errors));
            Assert.Equal(1, reimport.Count(ImportAction.Unchanged));
        }

        [Fact]
        public void QuotingAndTextSurviveTheRoundTrip()
        {
            var original = Seeded();
            Assert.True(new AreaImporter(original).Import(Area).Success);

            var exported = new AreaExporter(original).ExportArea("t");
            var fresh = Seeded();
            Assert.True(new AreaImporter(fresh).Import(exported.Yaml).Success);

            Assert.Equal("a hall: \"grand\"", fresh.Docs["t.hall"]["ShortDescription"].AsString);
            Assert.Equal("A plain hall.\n  Indented line.", fresh.Docs["t.hall"]["LongDescription"].AsString);
        }

        [Fact]
        public void TrailingWhitespaceFromAGameEditIsNormalizedByTheImporter()
        {
            // Stored text with trailing spaces (e.g. from an in-game edit). Spec section 11 says the importer
            // trims trailing whitespace, so the exported file cannot preserve it; it must still import cleanly.
            var store = Seeded();
            new AreaImporter(store).Import(Area);
            store.Docs["t.study"]["LongDescription"] = "Line one.   \nLine two.";

            var exported = new AreaExporter(store).ExportArea("t");
            Assert.True(exported.Success, string.Join("; ", exported.Errors));

            var fresh = Seeded();
            Assert.True(new AreaImporter(fresh).Import(exported.Yaml).Success);
            Assert.Equal("Line one.\nLine two.", fresh.Docs["t.study"]["LongDescription"].AsString);
        }

        [Fact]
        public void RoomWithoutImportableKeyIsRefused()
        {
            var store = Seeded();
            store.Docs["t.hall"] = new BsonDocument
            {
                { "_id", "t.hall" },
                { "Area", "t" },
                { "ShortDescription", "a hall" },
                { "LongDescription", "A hall." }
            };
            store.Docs["5f1d7f3e9b1e8a2c4d6e7f80"] = new BsonDocument
            {
                { "_id", "5f1d7f3e9b1e8a2c4d6e7f80" },
                { "Area", "t" },
                { "ShortDescription", "a generated room" },
                { "LongDescription", "Generated." }
            };

            var result = new AreaExporter(store).ExportArea("t");

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("5f1d7f3e9b1e8a2c4d6e7f80") && e.Contains("not keyed as"));
            Assert.Null(result.Yaml);
        }

        [Fact]
        public void ExportThatWouldNotImportIsRefused()
        {
            var store = Seeded();
            new AreaImporter(store).Import(Area);
            // An exit to a room that does not exist: the file would fail its own dangling-exit check.
            store.Docs["t.study"]["Exits"].AsBsonArray.Add(new BsonDocument { { "Name", "west" }, { "DestinationId", "t.missing" } });

            var result = new AreaExporter(store).ExportArea("t");

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("Export would not import") && e.Contains("t.missing"));
            Assert.Null(result.Yaml);
        }

        [Fact]
        public void UnknownAreaExportsNothing()
        {
            var result = new AreaExporter(Seeded()).ExportArea("nowhere");

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("No rooms belong to area 'nowhere'"));
        }

        [Fact]
        public void SampleAreaRoundTripsThroughTheExporter()
        {
            var store = Seeded();
            var importer = new AreaImporter(store);
            var sample = File.ReadAllText(SampleAreaPath());
            Assert.True(importer.Import(sample).Success);

            var exported = new AreaExporter(store).ExportArea("sample_area");
            Assert.True(exported.Success, string.Join("; ", exported.Errors));
            Assert.Equal(3, importer.Import(exported.Yaml).Count(ImportAction.Unchanged));
        }

        private static string SampleAreaPath()
        {
            var dir = System.IO.Directory.GetCurrentDirectory();
            for (var d = new System.IO.DirectoryInfo(dir); d != null; d = d.Parent)
            {
                var candidate = System.IO.Path.Combine(d.FullName, "MUD.Main", "Content", "sample_area.yaml");
                if (System.IO.File.Exists(candidate)) { return candidate; }
            }
            throw new System.IO.FileNotFoundException("sample_area.yaml not found from " + dir);
        }
    }
}
