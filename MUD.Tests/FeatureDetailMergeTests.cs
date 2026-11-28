using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Google.Protobuf;
using MongoDB.Bson;
using MUD.Contracts;
using MUD.Core.Generation.Terrains;
using Xunit;

namespace MUD.Tests
{
    // A feature has ONE look text, `long`: the stored `detail` is merged into it at start-up (specs/ROOM_FILES.md 4.4).
    public class FeatureDetailMergeTests
    {
        private sealed class FakeDetailStore : IFeatureDetailStore
        {
            public List<BsonDocument> Rooms = new List<BsonDocument>();
            public List<BsonDocument> Templates = new List<BsonDocument>();
            public int Writes;

            public IReadOnlyList<BsonDocument> RoomsWithFeatureDetail()
            {
                return Rooms.Where(r => r["Features"].AsBsonArray.Any(f => f.AsBsonDocument.Contains("DetailDescription"))).Select(r => r.DeepClone().AsBsonDocument).ToList();
            }

            public void UpdateRoom(string id, BsonDocument set, IReadOnlyList<string> unset)
            {
                Writes++;
                var room = Rooms.Single(r => r["_id"] == id);
                foreach (var e in set) { Apply(room, e.Name, e.Value); }
                foreach (var path in unset) { Remove(room, path); }
            }

            public IReadOnlyList<BsonDocument> TemplatesWithDetail() { return Templates.Where(t => t.Contains("detail")).Select(t => t.DeepClone().AsBsonDocument).ToList(); }

            public void UpdateTemplate(string id, BsonDocument set, IReadOnlyList<string> unset)
            {
                Writes++;
                var t = Templates.Single(x => x["_id"] == id);
                foreach (var e in set) { t[e.Name] = e.Value; }
                foreach (var path in unset) { t.Remove(path); }
            }

            // Dotted paths like Features.2.LongDescription.
            private static BsonValue Walk(BsonDocument root, string[] parts, out string last, out BsonValue parent)
            {
                BsonValue current = root;
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    current = current.IsBsonArray ? current.AsBsonArray[int.Parse(parts[i])] : current.AsBsonDocument[parts[i]];
                }
                parent = current;
                last = parts[parts.Length - 1];
                return current;
            }

            private static void Apply(BsonDocument root, string path, BsonValue value) { Walk(root, path.Split('.'), out var last, out var parent); parent.AsBsonDocument[last] = value; }
            private static void Remove(BsonDocument root, string path) { Walk(root, path.Split('.'), out var last, out var parent); parent.AsBsonDocument.Remove(last); }
        }

        private static BsonDocument RoomDoc(string id, params BsonDocument[] features)
        {
            return new BsonDocument { { "_id", id }, { "Features", new BsonArray(features) }, { "ShortDescription", "a room" } };
        }

        private static BsonDocument FeatureDoc(string shortText, string longText, BsonValue detail, string templateId = null, int? templateVersion = null)
        {
            var d = new BsonDocument { { "_id", "r#" + shortText }, { "ShortDescription", shortText }, { "LongDescription", longText }, { "RoomLine", "line" }, { "Handles", new BsonArray { "h" } } };
            if (detail != null) { d["DetailDescription"] = detail; }
            if (templateId != null) { d["TemplateId"] = templateId; d["TemplateVersion"] = templateVersion; }
            return d;
        }

        private static BsonDocument TemplateDoc(string id, string longText, BsonValue detail, int version)
        {
            var d = new BsonDocument { { "_id", id }, { "terrain", "demo.grass" }, { "short", "a rock" }, { "long", longText }, { "roomLine", "r" }, { "version", version }, { "weight", 2 } };
            if (detail != null) { d["detail"] = detail; }
            return d;
        }

        [Fact]
        public void RoomFeatures_DetailWins_EmptyDetailKeepsLong_OtherFieldsAndTheTemplateLinkSurvive()
        {
            var store = new FakeDetailStore();
            store.Rooms.Add(RoomDoc("t.a",
                FeatureDoc("table", "never shown", "what players saw", "tpl-table", 3),
                FeatureDoc("rug", "plain long", BsonNull.Value),
                FeatureDoc("lamp", "plain long", ""),
                FeatureDoc("vase", "same", "same"),
                FeatureDoc("cup", "just long", null)));
            var log = new List<string>();

            var result = FeatureDetailMigration.Run(store, log.Add);

            var features = store.Rooms[0]["Features"].AsBsonArray.Select(f => f.AsBsonDocument).ToList();
            Assert.Equal("what players saw", features[0]["LongDescription"].AsString);
            Assert.Equal("plain long", features[1]["LongDescription"].AsString);
            Assert.Equal("plain long", features[2]["LongDescription"].AsString);
            Assert.Equal("same", features[3]["LongDescription"].AsString);
            Assert.Equal("just long", features[4]["LongDescription"].AsString);
            Assert.All(features, f => Assert.False(f.Contains("DetailDescription")));
            // Untouched: the template link, the room line, the handles, the id.
            Assert.Equal("tpl-table", features[0]["TemplateId"].AsString);
            Assert.Equal(3, features[0]["TemplateVersion"].AsInt32);
            Assert.Equal("line", features[0]["RoomLine"].AsString);
            Assert.Equal("r#table", features[0]["_id"].AsString);
            Assert.Equal(1, result.RoomFeaturesMerged);
            Assert.Equal(3, result.RoomFeaturesCleaned);
            Assert.Equal(1, result.RoomsChanged);
            var line = Assert.Single(log);
            Assert.Contains("discarded", line);
        }

        [Fact]
        public void Templates_DetailWins_VersionBumpsOnlyWhenLongChanged()
        {
            var store = new FakeDetailStore();
            store.Templates.Add(TemplateDoc("a", "old long", "detail text", 4));
            store.Templates.Add(TemplateDoc("b", "kept long", "", 7));
            store.Templates.Add(TemplateDoc("c", "same", "same", 2));
            store.Templates.Add(TemplateDoc("d", "no detail at all", null, 1));

            var result = FeatureDetailMigration.Run(store, null);

            Assert.Equal(("detail text", 5), (store.Templates[0]["long"].AsString, store.Templates[0]["version"].AsInt32));
            Assert.Equal(("kept long", 7), (store.Templates[1]["long"].AsString, store.Templates[1]["version"].AsInt32));
            Assert.Equal(("same", 2), (store.Templates[2]["long"].AsString, store.Templates[2]["version"].AsInt32));
            Assert.Equal(("no detail at all", 1), (store.Templates[3]["long"].AsString, store.Templates[3]["version"].AsInt32));
            Assert.All(store.Templates, t => Assert.False(t.Contains("detail")));
            Assert.Equal(("demo.grass", 2), (store.Templates[0]["terrain"].AsString, store.Templates[0]["weight"].AsInt32));
            Assert.Equal(1, result.TemplatesMerged);
            Assert.Equal(2, result.TemplatesCleaned);
        }

        [Fact]
        public void ASecondRunChangesNothingAndWritesNothing()
        {
            var store = new FakeDetailStore();
            store.Rooms.Add(RoomDoc("t.a", FeatureDoc("table", "old", "new", "tpl", 1)));
            store.Templates.Add(TemplateDoc("a", "old", "new", 1));
            FeatureDetailMigration.Run(store, null);
            string roomsAfter = store.Rooms.ToJson();
            string templatesAfter = store.Templates.ToJson();
            int writes = store.Writes;
            var log = new List<string>();

            var second = FeatureDetailMigration.Run(store, log.Add);

            Assert.False(second.Changed);
            Assert.Empty(log);
            Assert.Equal(writes, store.Writes);
            Assert.Equal(roomsAfter, store.Rooms.ToJson());
            Assert.Equal(templatesAfter, store.Templates.ToJson());
            Assert.Equal(2, store.Templates[0]["version"].AsInt32);
        }

        [Fact]
        public void TheMigratedTemplateIsReadByTheTypedStoreWithoutAnyDetail()
        {
            var store = new FakeDetailStore();
            store.Templates.Add(TemplateDoc("a", "old", "new", 1));
            FeatureDetailMigration.Run(store, null);

            var template = LibraryDocuments.FeatureFrom(store.Templates[0]);
            Assert.Equal("new", template.Long);
            Assert.Equal(2, template.Version);
            Assert.False(LibraryDocuments.ToDocument(template).Contains("detail"));
        }

        [Fact]
        public void AnOldRoomDocumentStillDeserialisesAndLooksLikeItsDetail()
        {
            // A document the migration has not reached yet: the extra element is ignored, never an exception.
            var doc = FeatureDoc("table", "old long", "the detail");
            var feature = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<MUD.Core.GameObjects.Feature>(doc);
            Assert.Equal("old long", feature.Examine());
        }

        // ---- admin API compatibility

        [Fact]
        public void AnOldClientThatSendsDetailOnARoomFeatureIsIgnored()
        {
            var feature = new RoomFeature { Short = "a table", Long = "the look text", RoomLine = "line" };
            byte[] wire = feature.ToByteArray();
            // Field 4 (the retired detail), length-delimited "legacy", appended as an old client would send it.
            var legacy = new List<byte>(wire) { 0x22, 6 };
            legacy.AddRange(System.Text.Encoding.UTF8.GetBytes("legacy"));

            var parsed = RoomFeature.Parser.ParseFrom(legacy.ToArray());

            Assert.Equal("the look text", parsed.Long);
            Assert.Equal("line", parsed.RoomLine);
            Assert.DoesNotContain("legacy", parsed.ToString());
        }

        [Fact]
        public void AnOldClientThatSendsDetailOnAFeatureTemplateIsIgnored()
        {
            var record = new FeatureTemplateRecord { Id = "a", Short = "s", Long = "l" };
            var legacy = new List<byte>(record.ToByteArray()) { 0x42, 6 };   // field 8, length-delimited
            legacy.AddRange(System.Text.Encoding.UTF8.GetBytes("legacy"));

            var parsed = FeatureTemplateRecord.Parser.ParseFrom(legacy.ToArray());

            Assert.Equal("l", parsed.Long);
            Assert.DoesNotContain("legacy", parsed.ToString());
        }

        [Fact]
        public void TheProtoReservesDetailAndNeverReusesIt()
        {
            string proto = File.ReadAllText(Path.Combine(RepoRoot(), "contracts", "mud", "admin.proto"));
            Assert.Contains("reserved \"detail\";", proto);
            Assert.DoesNotContain("string detail =", proto.Replace("string detail = 3;", string.Empty));   // RoomGenLine.detail (an unrelated outcome note) stays
        }

        // ---- the guard: no code knows DetailDescription / GetDetailDescription any more

        [Fact]
        public void NoSourceFileReferencesDetailDescription_ExceptTheStartupMigrationAndItsTests()
        {
            string root = RepoRoot();
            var allowed = new[] { "FeatureDetailMigration.cs", "FeatureDetailMergeTests.cs", "FeatureYamlTests.cs" };
            var offenders = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".cs") || f.EndsWith(".razor") || f.EndsWith(".proto"))
                .Select(f => f.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar))
                .Where(rel => !rel.Split(Path.DirectorySeparatorChar).Any(part => part == "obj" || part == "bin" || part == ".worktrees"))
                .Where(rel => !allowed.Contains(Path.GetFileName(rel)))
                .Where(rel => File.ReadAllText(Path.Combine(root, rel)).Contains("DetailDescription"))
                .ToList();
            Assert.True(Directory.EnumerateFiles(root, "Feature.cs", SearchOption.AllDirectories).Any(), "the guard must actually see the source tree");
            Assert.True(offenders.Count == 0, "DetailDescription is retired (one look text, LongDescription); found in: " + string.Join(", ", offenders));
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MUD.sln"))) { dir = dir.Parent; }
            Assert.NotNull(dir);
            return dir.FullName;
        }
    }
}
