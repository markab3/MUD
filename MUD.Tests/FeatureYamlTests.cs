using System.Linq;
using MongoDB.Bson;
using MUD.Core.Generation;
using MUD.Core.Import;
using Xunit;

namespace MUD.Tests
{
    // Room features in area files: room_line and detail (specs/ROOM_FILES.md section 4.4).
    public class FeatureYamlTests
    {
        private const string Area = @"
area:
  schema_version: 1
  prefix: t
  name: Test
rooms:
  - key: hall
    short: a hall
    long: A plain hall.
    features:
      - short: a table
        long: A table.
        room_line: A heavy oak table stands against the wall.
        detail: |
          The table is scarred.
          Someone carved a name into it.
        handles: [table]
      - short: a rug
        long: A rug.
        handles: [rug]
";

        private static BsonDocument Feature(FakeStore store, int index)
        {
            return store.Docs["t.hall"]["Features"].AsBsonArray[index].AsBsonDocument;
        }

        [Fact]
        public void ImportReadsRoomLineAndDetail()
        {
            var store = new FakeStore();
            var result = new AreaImporter(store).Import(Area);

            Assert.True(result.Success, string.Join("; ", result.Errors));
            var table = Feature(store, 0);
            Assert.Equal("A heavy oak table stands against the wall.", table["RoomLine"].AsString);
            Assert.Equal("The table is scarred.\nSomeone carved a name into it.", table["DetailDescription"].AsString);
            Assert.Equal("A table.", table["LongDescription"].AsString);
        }

        [Fact]
        public void OmittedFieldsAreStoredAsNull()
        {
            var store = new FakeStore();
            new AreaImporter(store).Import(Area);

            var rug = Feature(store, 1);
            Assert.True(rug["RoomLine"].IsBsonNull);
            Assert.True(rug["DetailDescription"].IsBsonNull);
        }

        [Fact]
        public void EmptyRoomLineIsTheSameAsAbsent()
        {
            var store = new FakeStore();
            var importer = new AreaImporter(store);
            importer.Import(Area);
            var before = store.UpsertCount;

            var explicitEmpty = Area.Replace("        long: A rug.\n", "        long: A rug.\n        room_line: \"\"\n");
            var second = importer.Import(explicitEmpty);

            Assert.True(second.Success, string.Join("; ", second.Errors));
            Assert.Equal(1, second.Count(ImportAction.Unchanged));
            Assert.Equal(before, store.UpsertCount);
        }

        [Fact]
        public void ReimportIsIdempotent()
        {
            var store = new FakeStore();
            var importer = new AreaImporter(store);
            importer.Import(Area);
            var before = store.UpsertCount;

            var second = importer.Import(Area);

            Assert.Equal(1, second.Count(ImportAction.Unchanged));
            Assert.Equal(before, store.UpsertCount);
        }

        [Fact]
        public void UpsertUpdatesRoomLineAndDetailForAMatchedFeature()
        {
            var store = new FakeStore();
            var importer = new AreaImporter(store);
            importer.Import(Area);
            var originalId = Feature(store, 0)["_id"];

            var changed = Area.Replace("A heavy oak table stands against the wall.", "A table sits here.")
                              .Replace("The table is scarred.\n          Someone carved a name into it.", "Plain.");
            var result = importer.Import(changed);

            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.Equal(ImportAction.Update, result.Plan.Single().Action);
            Assert.Equal(new[] { "Features" }, result.Plan.Single().ChangedFields.ToArray());
            var table = Feature(store, 0);
            Assert.Equal("A table sits here.", table["RoomLine"].AsString);
            Assert.Equal("Plain.", table["DetailDescription"].AsString);
            Assert.Equal(originalId, table["_id"]);
        }

        [Fact]
        public void UpsertPreservesUnauthoredStateInsideAMatchedFeature()
        {
            var store = new FakeStore();
            var importer = new AreaImporter(store);
            importer.Import(Area);
            Feature(store, 0)["SomeRuntimeState"] = 42;

            importer.Import(Area.Replace("A heavy oak table stands against the wall.", "A table sits here."));

            var table = Feature(store, 0);
            Assert.Equal("A table sits here.", table["RoomLine"].AsString);
            Assert.Equal(42, table["SomeRuntimeState"].AsInt32);
        }

        [Fact]
        public void RemovingRoomLineAndDetailFromTheFileClearsThem()
        {
            var store = new FakeStore();
            var importer = new AreaImporter(store);
            importer.Import(Area);

            var stripped = Area.Replace("        room_line: A heavy oak table stands against the wall.\n", "")
                               .Replace("        detail: |\n          The table is scarred.\n          Someone carved a name into it.\n", "");
            var result = importer.Import(stripped);

            Assert.True(result.Success, string.Join("; ", result.Errors));
            var table = Feature(store, 0);
            Assert.True(table["RoomLine"].IsBsonNull);
            Assert.True(table["DetailDescription"].IsBsonNull);
        }

        [Fact]
        public void RemovingAFeatureFromTheFileRemovesItFromTheRoom()
        {
            var store = new FakeStore();
            var importer = new AreaImporter(store);
            importer.Import(Area);

            var withoutRug = Area.Replace("      - short: a rug\n        long: A rug.\n        handles: [rug]\n", "");
            var result = importer.Import(withoutRug);

            Assert.True(result.Success, string.Join("; ", result.Errors));
            var features = store.Docs["t.hall"]["Features"].AsBsonArray;
            Assert.Single(features);
            Assert.Equal("a table", features[0]["ShortDescription"].AsString);
        }

        [Fact]
        public void ExportWritesRoomLineAndDetailAndOmitsEmptyOnes()
        {
            var store = new FakeStore();
            new AreaImporter(store).Import(Area);

            var exported = new AreaExporter(store).ExportArea("t");

            Assert.True(exported.Success, string.Join("; ", exported.Errors));
            Assert.Equal(1, CountOf(exported.Yaml, "room_line:"));
            Assert.Equal(1, CountOf(exported.Yaml, "detail:"));
            Assert.Contains("A heavy oak table stands against the wall.", exported.Yaml);
        }

        [Fact]
        public void ExportThenImportIsStable()
        {
            var store = new FakeStore();
            var importer = new AreaImporter(store);
            importer.Import(Area);

            var exported = new AreaExporter(store).ExportArea("t");
            var before = store.UpsertCount;
            var reimport = importer.Import(exported.Yaml);

            Assert.True(reimport.Success, string.Join("; ", reimport.Errors));
            Assert.Equal(1, reimport.Count(ImportAction.Unchanged));
            Assert.Equal(before, store.UpsertCount);

            // And a second export of the re-imported data is byte-identical.
            Assert.Equal(exported.Yaml, new AreaExporter(store).ExportArea("t").Yaml);

            // A fresh store rebuilt from the export holds the same feature definitions.
            var fresh = new FakeStore();
            Assert.True(new AreaImporter(fresh).Import(exported.Yaml).Success);
            Assert.Equal(store.Docs["t.hall"]["Features"].ToJson(), fresh.Docs["t.hall"]["Features"].ToJson());
        }

        [Fact]
        public void RoomLineWithQuotesAndNewlinesSurvivesTheRoundTrip()
        {
            var store = new FakeStore();
            var importer = new AreaImporter(store);
            importer.Import(Area);
            Feature(store, 0)["RoomLine"] = "A \"quoted\": line\nwith a break.";

            var exported = new AreaExporter(store).ExportArea("t");
            var fresh = new FakeStore();
            Assert.True(new AreaImporter(fresh).Import(exported.Yaml).Success, string.Join("; ", exported.Errors));

            Assert.Equal("A \"quoted\": line\nwith a break.", Feature(fresh, 0)["RoomLine"].AsString);
        }

        [Fact]
        public void UnknownFeatureKeyIsStillRejected()
        {
            var result = new AreaImporter(new FakeStore()).Import(Area.Replace("room_line:", "roomline:"));

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("unknown key 'roomline'"));
        }

        [Fact]
        public void MapGeneratorPassesRoomLineThrough()
        {
            var map = @"
generator:
  prefix: city
  name: Test City
  seed: s1
grid:
  - '..'
legend:
  '.': street
templates:
  street: {short: a street, long: [[A street.]]}
overrides:
  - at: {x: 0, y: 0}
    features:
      - short: a statue
        long: A statue.
        room_line: A statue stands here.
        handles: [statue]
";
            var generated = MapGenerator.Generate(map);
            Assert.True(generated.Success, string.Join("; ", generated.Errors));
            var store = new FakeStore();
            var result = new AreaImporter(store).Import(generated.Yaml);
            Assert.True(result.Success, string.Join("; ", result.Errors));
            var withLine = store.Docs.Values.Single(d => d["Features"].AsBsonArray.Count == 1);
            Assert.Equal("A statue stands here.", withLine["Features"][0]["RoomLine"].AsString);
        }

        private static int CountOf(string text, string needle)
        {
            return text.Split(new[] { needle }, System.StringSplitOptions.None).Length - 1;
        }
    }
}
