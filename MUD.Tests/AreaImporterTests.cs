using System.Collections.Generic;
using System.IO;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MUD.Core.Import;
using Xunit;

namespace MUD.Tests
{
    // In-memory stand-in for the rooms collection with Mongo's $set / $setOnInsert upsert semantics.
    public class FakeStore : IRoomImportStore
    {
        public Dictionary<string, BsonDocument> Docs = new Dictionary<string, BsonDocument>();
        public int UpsertCount;

        public BsonDocument Find(string id) { return Docs.ContainsKey(id) ? Docs[id].DeepClone().AsBsonDocument : null; }

        public List<string> FindIdsByArea(string area)
        {
            return Docs.Values.Where(d => d.Contains("Area") && d["Area"] == area).Select(d => d["_id"].AsString).ToList();
        }

        public void Upsert(string id, BsonDocument set, BsonDocument setOnInsert)
        {
            UpsertCount++;
            BsonDocument doc;
            if (!Docs.TryGetValue(id, out doc))
            {
                doc = new BsonDocument("_id", id);
                if (setOnInsert != null) { doc.Merge(setOnInsert, true); }
                Docs[id] = doc;
            }
            foreach (var element in set) { doc[element.Name] = element.Value.DeepClone(); }
        }
    }

    public class AreaImporterTests
    {
        private const string Area = @"
area:
  schema_version: 1
  prefix: t
  name: Test
rooms:
  - key: hall
    short: a hall
    long: |
      A plain hall.
    exits:
      - {dir: north, to: study, reverse: south}
      - {dir: up, to: landing}
    features:
      - short: a table
        long: A table.
        handles: [table]
    properties:
      - behavior: search
        id: s1
        params:
          target: table
          refresh_ms: 1000
          reward: {short: a coin, long: A coin.}
  - key: study
    short: a study
    long: A study.
  - key: landing
    short: a landing
    long: A landing.
";

        private static string Replace(string yaml, string from, string to) { return yaml.Replace(from, to); }

        [Fact]
        public void FreshImportInsertsRoomsWithKeysAndEmptyItems()
        {
            var store = new FakeStore();
            var result = new AreaImporter(store).Import(Area);

            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.Equal(new[] { "t.hall", "t.landing", "t.study" }, store.Docs.Keys.OrderBy(k => k).ToArray());
            Assert.Equal(3, result.Count(ImportAction.Insert));
            Assert.Empty(store.Docs["t.hall"]["Items"].AsBsonArray);
            Assert.Equal("t", store.Docs["t.hall"]["Area"].AsString);
            Assert.Equal("A plain hall.", store.Docs["t.hall"]["LongDescription"].AsString);
        }

        [Fact]
        public void ReverseExitIsSynthesizedOnDestination()
        {
            var store = new FakeStore();
            new AreaImporter(store).Import(Area);

            var exits = store.Docs["t.study"]["Exits"].AsBsonArray;
            Assert.Single(exits);
            Assert.Equal("south", exits[0]["Name"].AsString);
            Assert.Equal("t.hall", exits[0]["DestinationId"].AsString);
            Assert.Equal(BsonType.String, exits[0]["DestinationId"].BsonType);
        }

        [Fact]
        public void ReimportIsIdempotentAndWritesNothing()
        {
            var store = new FakeStore();
            var importer = new AreaImporter(store);
            importer.Import(Area);
            var before = store.UpsertCount;

            var second = importer.Import(Area);

            Assert.True(second.Success);
            Assert.Equal(3, second.Count(ImportAction.Unchanged));
            Assert.Equal(before, store.UpsertCount);
        }

        [Fact]
        public void ReimportPreservesDroppedItemsAndUpdatesDescription()
        {
            var store = new FakeStore();
            var importer = new AreaImporter(store);
            importer.Import(Area);

            // A player drops a rope in the hall.
            store.Docs["t.hall"]["Items"].AsBsonArray.Add(new BsonDocument { { "_id", "abc" }, { "ShortDescription", "a rope" } });

            var changed = Replace(Area, "A plain hall.", "A grand hall.");
            var result = importer.Import(changed);

            Assert.True(result.Success);
            Assert.Equal("A grand hall.", store.Docs["t.hall"]["LongDescription"].AsString);
            var items = store.Docs["t.hall"]["Items"].AsBsonArray;
            Assert.Single(items);
            Assert.Equal("a rope", items[0]["ShortDescription"].AsString);
            // Only the changed definition field is written.
            var plan = result.Plan.Single(p => p.Action == ImportAction.Update);
            Assert.Equal("t.hall", plan.Key);
            Assert.Equal(new[] { "LongDescription" }, plan.ChangedFields.ToArray());
        }

        [Fact]
        public void ReimportPreservesPropertyRuntimeStateAndUnknownFields()
        {
            var store = new FakeStore();
            var importer = new AreaImporter(store);
            importer.Import(Area);

            var prop = store.Docs["t.hall"]["ExtendedProperties"].AsBsonArray[0].AsBsonDocument;
            prop["NextRewardTicks"] = 12345L; // simulated persisted cooldown state
            store.Docs["t.hall"]["SomeLaterSystemField"] = "keep me";

            var result = importer.Import(Replace(Area, "refresh_ms: 1000", "refresh_ms: 2000"));

            Assert.True(result.Success);
            var after = store.Docs["t.hall"]["ExtendedProperties"].AsBsonArray[0].AsBsonDocument;
            Assert.Equal(2000, after["RefreshInterval"].AsInt32);
            Assert.Equal(12345L, after["NextRewardTicks"].AsInt64);
            Assert.Equal("keep me", store.Docs["t.hall"]["SomeLaterSystemField"].AsString);
        }

        [Fact]
        public void RemovingExitFromFileRemovesItFromWorld()
        {
            var store = new FakeStore();
            var importer = new AreaImporter(store);
            importer.Import(Area);

            importer.Import(Replace(Area, "      - {dir: up, to: landing}\n", ""));

            Assert.Single(store.Docs["t.hall"]["Exits"].AsBsonArray);
        }

        [Fact]
        public void DanglingExitFailsValidationAndWritesNothing()
        {
            var store = new FakeStore();
            var result = new AreaImporter(store).Import(Replace(Area, "to: landing}", "to: nowhere}"));

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("t.nowhere"));
            Assert.Equal(0, store.UpsertCount);
            Assert.Empty(store.Docs);
        }

        [Fact]
        public void ExitToExistingRoomInDatabaseByFullKeyIsAccepted()
        {
            var store = new FakeStore();
            store.Docs["town.gate"] = new BsonDocument { { "_id", "town.gate" }, { "Area", "town" } };

            var result = new AreaImporter(store).Import(Replace(Area, "to: landing}", "to: town.gate}"));

            Assert.True(result.Success, string.Join("; ", result.Errors));
        }

        [Fact]
        public void DuplicateRoomKeysFailValidationAndWriteNothing()
        {
            var store = new FakeStore();
            var yaml = Area + "  - key: study\n    short: dup\n    long: dup\n";
            var result = new AreaImporter(store).Import(yaml);

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("Duplicate room key 't.study'"));
            Assert.Equal(0, store.UpsertCount);
        }

        [Fact]
        public void ErrorsAreAllReportedTogether()
        {
            var store = new FakeStore();
            var yaml = Replace(Replace(Area, "to: landing}", "to: nowhere}"), "key: study", "key: hall");
            var result = new AreaImporter(store).Import(yaml);

            Assert.False(result.Success);
            Assert.True(result.Errors.Count >= 2);
            Assert.Equal(0, store.UpsertCount);
        }

        [Fact]
        public void UnknownKeysAndUnknownBehaviorsAreRejected()
        {
            var store = new FakeStore();
            Assert.Contains(new AreaImporter(store).Import(Replace(Area, "    short: a hall", "    short: a hall\n    colour: red")).Errors, e => e.Contains("unknown key 'colour'"));
            Assert.Contains(new AreaImporter(store).Import(Replace(Area, "behavior: search", "behavior: teleport")).Errors, e => e.Contains("unknown behavior 'teleport'"));
            Assert.Equal(0, store.UpsertCount);
        }

        [Fact]
        public void ConflictingReverseExitIsRejected()
        {
            var yaml = Area.Replace("short: a study\n    long: A study.", "short: a study\n    long: A study.\n    exits:\n      - {dir: south, to: landing}");
            var result = new AreaImporter(new FakeStore()).Import(yaml);

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("conflicts"));
        }

        [Fact]
        public void RoomOwnedByAnotherAreaIsACollision()
        {
            var store = new FakeStore();
            store.Docs["t.hall"] = new BsonDocument { { "_id", "t.hall" }, { "Area", "other" } };

            var result = new AreaImporter(store).Import(Area);

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("belongs to area 'other'"));
            Assert.Equal(0, store.UpsertCount);
        }

        [Fact]
        public void RoomsRemovedFromFileAreReportedAsOrphansNotDeleted()
        {
            var store = new FakeStore();
            var importer = new AreaImporter(store);
            importer.Import(Area);

            var trimmed = Area.Substring(0, Area.IndexOf("  - key: landing")).Replace("      - {dir: up, to: landing}\n", "");
            var result = importer.Import(trimmed);

            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.Equal(new[] { "t.landing" }, result.Orphans.ToArray());
            Assert.True(store.Docs.ContainsKey("t.landing"));
        }

        [Fact]
        public void DryRunWritesNothing()
        {
            var store = new FakeStore();
            var result = new AreaImporter(store).Import(Area, dryRun: true);

            Assert.True(result.Success);
            Assert.Equal(3, result.Count(ImportAction.Insert));
            Assert.Empty(store.Docs);
        }

        [Fact]
        public void SampleAreaFileImportsCleanlyAndTwiceIdentically()
        {
            var dir = Directory.GetCurrentDirectory();
            string path = null;
            for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
            {
                var candidate = Path.Combine(d.FullName, "MUD.Main", "Content", "sample_area.yaml");
                if (File.Exists(candidate)) { path = candidate; break; }
            }
            Assert.NotNull(path);

            var store = new FakeStore();
            var importer = new AreaImporter(store);
            var first = importer.Import(File.ReadAllText(path));
            Assert.True(first.Success, string.Join("; ", first.Errors));
            Assert.Equal(3, first.Count(ImportAction.Insert));
            Assert.Equal(3, importer.Import(File.ReadAllText(path)).Count(ImportAction.Unchanged));
        }
    }
}
