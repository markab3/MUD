using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MongoDB.Bson;
using MUD.Contracts;
using MUD.Core.Administration;
using MUD.Core.GameObjects;
using MUD.Core.Generation;
using MUD.Core.Generation.Terrains;
using MUD.Core.Import;
using Xunit;
using RoomGenOutcome = MUD.Core.Generation.RoomGenOutcome;
using FeatureUpdateOutcome = MUD.Core.Generation.FeatureUpdateOutcome;

namespace MUD.Tests
{
    // Card t_9aa03d46: regen and update of generated rooms (specs/TERRAIN_GENERATORS.md section 9) and the admin rpcs.
    // In-memory store, library and audit only: nothing here ran against MongoDB.
    public class RegenUpdateTests : IDisposable
    {
        private const string Map = "sample_land";
        private readonly string _dir;

        public RegenUpdateTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "regenupdate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_dir, "maps"));
            File.WriteAllText(Path.Combine(_dir, "maps", Map + ".map.yaml"), File.ReadAllText(SampleMapPath()));
        }

        public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

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

        // The seed library, with its feature templates replaced by one that every grass room gets (chance 1), so the tests
        // do not depend on a roll.
        private static InMemoryLibrary Library(int rockVersion = 1)
        {
            var errors = new List<string>();
            var data = LibrarySeed.Parse(LibrarySeed.ReadEmbedded(), errors, new List<string>());
            Assert.Empty(errors);
            var lib = new InMemoryLibrary { Version = data.LibraryVersion };
            lib.FragmentList.AddRange(data.Fragments);
            lib.PhraseList.AddRange(data.Phrases);
            lib.FeatureList.Add(new FeatureTemplate
            {
                Id = "rock", Terrains = new List<string> { "grass" }, Short = "a grey rock", Long = "A grey rock, version " + rockVersion + ".",
                Handles = new List<string> { "rock" }, Weight = 1, Chance = 1.0, Version = rockVersion,
            });
            return lib;
        }

        private class Audit
        {
            public List<GenAuditEntry> Entries = new List<GenAuditEntry>();
            public List<bool> Outcomes = new List<bool>();
            public bool Fail;
            public int StoreWritesWhenAudited = -1;
            public FakeStore Store;

            public Action<bool> Begin(IReadOnlyList<GenAuditEntry> entries)
            {
                if (Fail) { return null; }
                if (Store != null) { StoreWritesWhenAudited = Store.UpsertCount; }
                Entries.AddRange(entries);
                return applied => Outcomes.Add(applied);
            }
        }

        private GenerationService Service(FakeStore store, ILibrary library, Audit audit = null)
        {
            audit = audit ?? new Audit();
            return new GenerationService(store, () => library, _dir, audit.Begin);
        }

        // Imports the map as a first import and returns the store.
        private FakeStore Imported(ILibrary library)
        {
            var store = new FakeStore();
            var generated = MapGenerator.Generate(File.ReadAllText(Path.Combine(_dir, "maps", Map + ".map.yaml")), Map, library);
            Assert.True(generated.Success, string.Join("; ", generated.Errors));
            Assert.True(new AreaImporter(store).Import(generated.Yaml, new ImportOptions { ReplaceGenerated = true }).Success);
            return store;
        }

        private static string GrassKey(FakeStore store)
        {
            return store.Docs.Values.First(d => d["gen"]["terrain"].AsString == "grass")["_id"].AsString;
        }

        private static BsonDocument HandFeature(string key)
        {
            return new BsonDocument { { "_id", key + "#hand" }, { "ShortDescription", "a hand made shrine" }, { "LongDescription", "Added by a creator." } };
        }

        // ---------------------------------------------------------------- regen

        [Fact]
        public void Regen_WipesAHandAddedFeatureAndHandEditsAndKeepsExitsAndItems()
        {
            var lib = Library();
            var store = Imported(lib);
            var key = GrassKey(store);
            var generated = store.Docs[key].DeepClone().AsBsonDocument;
            Assert.Contains(generated["Features"].AsBsonArray, f => f.AsBsonDocument.GetValue("TemplateId", BsonNull.Value) == "rock");

            var doc = store.Docs[key];
            doc["LongDescription"] = "Hand written.";
            doc["Features"].AsBsonArray.Add(HandFeature(key));
            doc["Items"] = new BsonArray { new BsonDocument("ShortDescription", "a coin") };

            var report = Service(store, lib).RegenerateRooms(new[] { key }, false, null);

            Assert.Equal(RoomGenOutcome.Replaced, Assert.Single(report.Rooms).Outcome);
            var after = store.Docs[key];
            Assert.DoesNotContain(after["Features"].AsBsonArray, f => f["ShortDescription"] == "a hand made shrine");
            Assert.Equal(generated["LongDescription"], after["LongDescription"]);
            Assert.Equal(generated["Features"], after["Features"]);
            Assert.Equal(generated["Exits"], after["Exits"]);
            Assert.Equal(generated["gen"], after["gen"]);
            Assert.Equal("a coin", after["Items"][0]["ShortDescription"].AsString);
        }

        [Fact]
        public void Regen_UsesTheStoredSeedAndANewSeedRerolls()
        {
            var lib = Library();
            var store = Imported(lib);
            var key = GrassKey(store);
            var before = store.Docs[key]["gen"]["seed"].AsString;

            var same = Service(store, lib).RegenerateRooms(new[] { key }, false, null);
            Assert.Equal(RoomGenOutcome.Unchanged, same.Rooms.Single().Outcome);   // idempotent: same seed, same library, same output

            var rerolled = Service(store, lib).RegenerateRooms(new[] { key }, false, "another-seed");
            Assert.Equal(RoomGenOutcome.Replaced, rerolled.Rooms.Single().Outcome);
            Assert.Equal("another-seed", store.Docs[key]["gen"]["seed"].AsString);
            Assert.NotEqual(before, store.Docs[key]["gen"]["seed"].AsString);
        }

        [Fact]
        public void Regen_PicksUpALibraryEditAndRecordsTheNewLibraryVersion()
        {
            var store = Imported(Library());
            var key = GrassKey(store);
            var newer = Library(rockVersion: 2);
            newer.Version = 7;

            Service(store, newer).RegenerateRooms(new[] { key }, false, null);

            var rock = store.Docs[key]["Features"].AsBsonArray.Single(f => f.AsBsonDocument.GetValue("TemplateId", BsonNull.Value) == "rock");
            Assert.Equal(2, rock["TemplateVersion"].ToInt32());
            Assert.Equal(7, store.Docs[key]["gen"]["libVersion"].ToInt32());
        }

        [Fact]
        public void Regen_OfTheWholeMapReplacesEveryRoomAndReportsOrphans()
        {
            var lib = Library();
            var store = Imported(lib);
            var rooms = store.Docs.Keys.ToList();
            foreach (var key in rooms) { store.Docs[key]["Features"].AsBsonArray.Add(HandFeature(key)); }
            store.Docs["sample_land.gone"] = new BsonDocument { { "_id", "sample_land.gone" }, { "Area", "sample_land" } };

            var report = Service(store, lib).RegenerateMap(Map, false, null);

            Assert.Empty(report.Errors);
            Assert.Equal(rooms.Count, report.Rooms.Count);
            Assert.All(rooms, k => Assert.DoesNotContain(store.Docs[k]["Features"].AsBsonArray, f => f["ShortDescription"] == "a hand made shrine"));
            Assert.Equal(new[] { "sample_land.gone" }, report.Orphans);
            Assert.True(store.Docs.ContainsKey("sample_land.gone"));   // never deleted
        }

        [Fact]
        public void Regen_DryRunWritesNothingAndAuditsNothing()
        {
            var lib = Library();
            var store = Imported(lib);
            var key = GrassKey(store);
            store.Docs[key]["Features"].AsBsonArray.Add(HandFeature(key));
            var before = store.Docs[key].DeepClone();
            var writes = store.UpsertCount;
            var audit = new Audit();

            var report = Service(store, lib, audit).RegenerateRooms(new[] { key }, true, null);

            Assert.True(report.DryRun);
            Assert.Equal(RoomGenOutcome.Replaced, report.Rooms.Single().Outcome);
            Assert.Equal(writes, store.UpsertCount);
            Assert.Equal(before, store.Docs[key]);
            Assert.Empty(audit.Entries);
        }

        [Fact]
        public void Regen_AuditsFirstAndMarksTheRowsAppliedAfterTheWrite()
        {
            var lib = Library();
            var store = Imported(lib);
            var key = GrassKey(store);
            store.Docs[key]["Features"].AsBsonArray.Add(HandFeature(key));
            var audit = new Audit { Store = store };
            var writesBefore = store.UpsertCount;

            Service(store, lib, audit).RegenerateRooms(new[] { key }, false, null);

            var entry = Assert.Single(audit.Entries);
            Assert.Equal((key, GenerationService.RegenField), (entry.Key, entry.Field));
            Assert.Equal(writesBefore, audit.StoreWritesWhenAudited);   // the audit came before any write
            Assert.True(store.UpsertCount > writesBefore);
            Assert.Equal(new[] { true }, audit.Outcomes);
        }

        [Fact]
        public void Regen_WhenTheAuditCannotBeWrittenNothingChanges()
        {
            var lib = Library();
            var store = Imported(lib);
            var key = GrassKey(store);
            store.Docs[key]["Features"].AsBsonArray.Add(HandFeature(key));
            var before = store.Docs[key].DeepClone();

            var report = Service(store, lib, new Audit { Fail = true }).RegenerateRooms(new[] { key }, false, null);

            Assert.True(report.AuditFailed);
            Assert.Equal(RoomGenOutcome.Error, report.Rooms.Single().Outcome);
            Assert.Equal(before, store.Docs[key]);
        }

        [Fact]
        public void Regen_ReportsAHandAuthoredRoomAndAMissingMapAndChangesNeitherRoom()
        {
            var lib = Library();
            var store = Imported(lib);
            store.Docs["hand.room"] = new BsonDocument { { "_id", "hand.room" }, { "Area", "hand" }, { "LongDescription", "mine" } };
            var generatedKey = GrassKey(store);
            store.Docs[generatedKey]["gen"]["map"] = "vanished";

            var report = Service(store, lib).RegenerateRooms(new[] { "hand.room", generatedKey, "no.such" }, false, null);

            Assert.Equal(RoomGenOutcome.NotGenerated, report.Rooms.Single(r => r.Key == "hand.room").Outcome);
            Assert.Equal(RoomGenOutcome.MapNotFound, report.Rooms.Single(r => r.Key == generatedKey).Outcome);
            Assert.Equal(RoomGenOutcome.Error, report.Rooms.Single(r => r.Key == "no.such").Outcome);
            Assert.Equal("mine", store.Docs["hand.room"]["LongDescription"].AsString);
        }

        // ---------------------------------------------------------------- update

        [Fact]
        public void Update_RewritesAReferencedFeatureWhenTheTemplateIsNewerAndLeavesAHandAddedOneAndTheDescriptionAlone()
        {
            var store = Imported(Library());
            var key = GrassKey(store);
            var doc = store.Docs[key];
            doc["Features"].AsBsonArray.Add(HandFeature(key));
            doc["LongDescription"] = "Hand written description.";
            var handBefore = HandFeature(key);
            var longBefore = doc["LongDescription"];
            var exitsBefore = doc["Exits"].DeepClone();
            var genBefore = doc["gen"].DeepClone();

            var report = Service(store, Library(rockVersion: 2)).UpdateRooms(new[] { key }, false);

            var line = report.Rooms.Single();
            Assert.Equal(RoomGenOutcome.Updated, line.Outcome);
            var update = Assert.Single(line.Features);
            Assert.Equal((FeatureUpdateOutcome.Updated, 1, 2), (update.Outcome, update.FromVersion, update.ToVersion));
            var features = store.Docs[key]["Features"].AsBsonArray;
            var rock = features.Single(f => f.AsBsonDocument.GetValue("TemplateId", BsonNull.Value) == "rock");
            Assert.Equal(2, rock["TemplateVersion"].ToInt32());
            Assert.Contains("version 2", rock["LongDescription"].AsString);
            Assert.Contains(handBefore, features);                       // the hand-added feature is byte for byte what it was
            Assert.Equal(longBefore, store.Docs[key]["LongDescription"]);   // update never touches the description
            Assert.Equal(exitsBefore, store.Docs[key]["Exits"]);
            Assert.Equal(genBefore, store.Docs[key]["gen"]);
        }

        [Fact]
        public void Update_DoesNothingWhenTheVersionIsCurrentAndReportsADeletedTemplateWithoutChangingIt()
        {
            var store = Imported(Library());
            var key = GrassKey(store);
            var before = store.Docs[key].DeepClone();
            var writes = store.UpsertCount;

            var current = Service(store, Library()).UpdateRooms(new[] { key }, false);
            Assert.Equal(FeatureUpdateOutcome.Current, current.Rooms.Single().Features.Single().Outcome);
            Assert.Equal(1, current.CountFeatures(FeatureUpdateOutcome.Current));

            var noRock = Library();
            noRock.FeatureList.Clear();
            var deleted = Service(store, noRock).UpdateRooms(new[] { key }, false);
            Assert.Equal(FeatureUpdateOutcome.TemplateDeleted, deleted.Rooms.Single().Features.Single().Outcome);
            Assert.Equal(1, deleted.CountFeatures(FeatureUpdateOutcome.TemplateDeleted));
            Assert.Equal(before, store.Docs[key]);
            Assert.Equal(writes, store.UpsertCount);
        }

        [Fact]
        public void Update_ReportsARollbackAsNewerInRoomAndADuplicateShortAsAConflictAndChangesNeither()
        {
            var store = Imported(Library(rockVersion: 3));
            var key = GrassKey(store);
            var before = store.Docs[key].DeepClone();
            Assert.Equal(FeatureUpdateOutcome.NewerInRoom, Service(store, Library(rockVersion: 2)).UpdateRooms(new[] { key }, false).Rooms.Single().Features.Single().Outcome);

            // The newer template is renamed to what a hand-added feature is already called.
            var renamed = Library(rockVersion: 4);
            renamed.FeatureList.Single().Short = "a hand made shrine";
            store.Docs[key]["Features"].AsBsonArray.Add(HandFeature(key));
            var withHand = store.Docs[key].DeepClone();
            Assert.Equal(FeatureUpdateOutcome.Conflict, Service(store, renamed).UpdateRooms(new[] { key }, false).Rooms.Single().Features.Single().Outcome);
            Assert.Equal(withHand, store.Docs[key]);
            Assert.NotEqual(before, store.Docs[key]);   // (only because of the hand feature added above)
        }

        [Fact]
        public void Update_DryRunReportsButWritesAndAuditsNothing()
        {
            var store = Imported(Library());
            var key = GrassKey(store);
            var before = store.Docs[key].DeepClone();
            var audit = new Audit();

            var report = Service(store, Library(rockVersion: 2), audit).UpdateRooms(new[] { key }, true);

            Assert.Equal(1, report.CountFeatures(FeatureUpdateOutcome.Updated));
            Assert.Equal(before, store.Docs[key]);
            Assert.Empty(audit.Entries);
        }

        [Fact]
        public void Update_AuditsFirstAndRefusesWhenTheAuditFails()
        {
            var store = Imported(Library());
            var key = GrassKey(store);
            var before = store.Docs[key].DeepClone();
            var audit = new Audit { Store = store };
            var writes = store.UpsertCount;

            Service(store, Library(rockVersion: 2), audit).UpdateRooms(new[] { key }, false);
            Assert.Equal(writes, audit.StoreWritesWhenAudited);
            Assert.Equal((key, GenerationService.UpdateField), (audit.Entries.Single().Key, audit.Entries.Single().Field));
            Assert.Equal(new[] { true }, audit.Outcomes);

            var other = Imported(Library());
            var failed = Service(other, Library(rockVersion: 2), new Audit { Fail = true }).UpdateRooms(new[] { key }, false);
            Assert.True(failed.AuditFailed);
            Assert.Equal(before, other.Docs[key]);
        }

        [Fact]
        public void Update_SkipsRoomsThatAreNotGenerated()
        {
            var store = Imported(Library());
            store.Docs["hand.room"] = new BsonDocument { { "_id", "hand.room" }, { "Area", "hand" }, { "Features", new BsonArray { new BsonDocument { { "ShortDescription", "x" }, { "TemplateId", "rock" }, { "TemplateVersion", 1 } } } } };
            var report = Service(store, Library(rockVersion: 2)).UpdateRooms(new[] { "hand.room" }, false);
            Assert.Equal(RoomGenOutcome.NotGenerated, report.Rooms.Single().Outcome);
            Assert.Equal(1, store.Docs["hand.room"]["Features"][0]["TemplateVersion"].ToInt32());
        }

        [Fact]
        public void Selection_ByAreaAndMapFindsGeneratedRoomsOnly()
        {
            var store = Imported(Library());
            store.Docs["sample_land.hand"] = new BsonDocument { { "_id", "sample_land.hand" }, { "Area", "sample_land" } };
            var service = Service(store, Library());
            var byArea = service.GeneratedKeysOfArea("sample_land");
            Assert.DoesNotContain("sample_land.hand", byArea);
            Assert.Equal(store.Docs.Count - 1, byArea.Count);
            Assert.Equal(byArea, service.GeneratedKeysOfMap(Map));
        }

        // ---------------------------------------------------------------- admin rpcs (and live objects)

        private class Host : IAdminHost
        {
            public string StartRoomKey { get { return "t.start"; } }
            public bool TryRunOnGameThread<T>(Func<T> work, out T result) { result = work(); return true; }
            public bool IsThrottled(string remoteKey) { return false; }
            public void RecordLoginOutcome(string remoteKey, bool success) { }
            public bool IsOnline(Player player) { return false; }
        }

        private class AuditStore : IAuditStore
        {
            public List<AuditRecord> Rows = new List<AuditRecord>();
            public bool Fail;
            public void Insert(IReadOnlyList<AuditRecord> rows) { if (Fail) { throw new InvalidOperationException("audit down"); } Rows.AddRange(rows); }
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
        }

        private class Person : Player
        {
            public Person(string name, MUD.Core.Repositories.Interfaces.IPlayerRepository repository) : base(null, null, repository)
            {
                Id = "p." + name.ToLowerInvariant(); PlayerName = name; NameKey = name.ToLowerInvariant(); ShortDescription = name; Password = "secret1";
                Race = "human"; Gender = "female"; Class = "fighter";
            }
        }

        private class Api
        {
            public AuditStore Audit = new AuditStore();
            public SyncRoomRepo Rooms = new SyncRoomRepo();
            public SyncPlayerRepo Players = new SyncPlayerRepo();
            public FakeLibraryStore Store = new FakeLibraryStore { Version = 5 };
            public List<string> Log = new List<string>();
            public AdminApi Admin;
            private readonly AdminSessions _sessions = new AdminSessions();

            public Api(string contentDirectory, InMemoryLibrary library)
            {
                foreach (var f in library.FragmentList) { Store.FragmentTable.Items[f.Id] = f; }
                foreach (var f in library.FeatureList) { Store.FeatureTable.Items[f.Id] = f; }
                foreach (var p in library.PhraseList) { Store.PhraseTable.Items[p.Id] = p; }
                Admin = new AdminApi(new Host(), Players, Rooms, new NoQueries(), Audit, _sessions, new FakeClock(), Log.Add, max => 0, null, Store, new StoreLibrary(Store), contentDirectory);
            }

            public string Login(string name, bool creator)
            {
                var person = new Person(name, Players);
                person.Roles.Creator = creator;
                Players.Live.Add(person);
                Players.Stored[person.Id] = person.ToBsonDocument();
                return Admin.WebAuthenticate(new WebAuthRequest { Name = name, Password = "secret1", RemoteKey = "k" }).Token;
            }

            // Stores the generated rooms of the sample map and caches a live copy of each.
            public void Load(FakeStore generated)
            {
                foreach (var doc in generated.Docs.Values)
                {
                    Rooms.Stored[doc["_id"].AsString] = doc.DeepClone().AsBsonDocument;
                    // new Room(...) so field initialisers run (BSON deserialisation skips them); the definition is then copied in.
                    var live = new Room(Rooms) { Id = doc["_id"].AsString };
                    live.ApplyDefinition(MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Room>(doc));
                    Rooms.Cache[live.Id] = live;
                }
            }
        }

        [Fact]
        public void AdminRegenerateRooms_WipesHandEditsStoresThemAndRefreshesTheLiveRoomAndAudits()
        {
            var lib = Library();
            var generated = Imported(lib);
            var key = GrassKey(generated);
            var api = new Api(_dir, lib);
            api.Load(generated);
            var live = api.Rooms.Cache[key];
            var generatedLong = live.LongDescription;
            // A creator's hand edit, in the database and on the live object.
            api.Rooms.Stored[key]["Features"].AsBsonArray.Add(HandFeature(key));
            live.Features.Add(new Feature { Id = key + "#hand", ShortDescription = "a hand made shrine", LongDescription = "Added by a creator." });
            live.LongDescription = "Hand written.";
            api.Rooms.Stored[key]["LongDescription"] = "Hand written.";
            var token = api.Login("Cora", creator: true);

            var reply = api.Admin.RegenerateRooms(new RegenerateRoomsRequest { Token = token, Keys = { key } });

            Assert.True(AdminResult.Ok == reply.Result, string.Join("|", api.Log) + " errors: " + string.Join("|", reply.Errors));
            Assert.Equal(1, reply.UpdatedCount);
            Assert.Equal(ROOM(RoomGenOutcome_Replaced), reply.Rooms.Single().Outcome);
            Assert.Same(live, api.Rooms.Cache[key]);                     // the same live object, refreshed in place
            Assert.Equal(generatedLong, live.LongDescription);
            Assert.DoesNotContain(live.Features, f => f.ShortDescription == "a hand made shrine");
            Assert.DoesNotContain(api.Rooms.Stored[key]["Features"].AsBsonArray, f => f["ShortDescription"] == "a hand made shrine");
            var row = Assert.Single(api.Audit.Rows);
            Assert.Equal(("room", key, GenerationService.RegenField, "Cora", true), (row.Entity, row.EntityId, row.Field, row.ActorName, row.Applied));
        }

        private const MUD.Contracts.RoomGenOutcome RoomGenOutcome_Replaced = MUD.Contracts.RoomGenOutcome.RoomGenReplaced;
        private static MUD.Contracts.RoomGenOutcome ROOM(MUD.Contracts.RoomGenOutcome o) { return o; }

        [Fact]
        public void AdminUpdateRooms_RewritesTheLiveReferencedFeatureAndKeepsTheHandAddedOne()
        {
            var generated = Imported(Library());
            var key = GrassKey(generated);
            var api = new Api(_dir, Library(rockVersion: 2));
            api.Load(generated);
            var live = api.Rooms.Cache[key];
            api.Rooms.Stored[key]["Features"].AsBsonArray.Add(HandFeature(key));
            live.Features.Add(new Feature { Id = key + "#hand", ShortDescription = "a hand made shrine" });
            var token = api.Login("Cora", creator: true);

            var reply = api.Admin.UpdateRooms(new UpdateRoomsRequest { Token = token, Keys = { key } });

            Assert.True(AdminResult.Ok == reply.Result, string.Join("|", api.Log) + " errors: " + string.Join("|", reply.Errors));
            Assert.Equal((1, 0, 0), (reply.UpdatedCount, reply.UnchangedCount, reply.DeletedTemplateCount));
            Assert.Equal(2, live.Features.Single(f => f.TemplateId == "rock").TemplateVersion);
            Assert.Contains(live.Features, f => f.ShortDescription == "a hand made shrine");
            Assert.Equal(GenerationService.UpdateField, Assert.Single(api.Audit.Rows).Field);
        }

        [Fact]
        public void AdminRegenerateMap_ReplacesEveryRoomAndDryRunAndAuditFailureChangeNothing()
        {
            var lib = Library();
            var generated = Imported(lib);
            var api = new Api(_dir, lib);
            api.Load(generated);
            foreach (var doc in api.Rooms.Stored.Values) { doc["Features"].AsBsonArray.Add(HandFeature(doc["_id"].AsString)); }
            var token = api.Login("Cora", creator: true);
            var snapshot = api.Rooms.Stored.ToDictionary(kv => kv.Key, kv => kv.Value.DeepClone());

            var dry = api.Admin.RegenerateMap(new RegenerateMapRequest { Token = token, Map = Map, DryRun = true });
            Assert.True(dry.DryRun);
            Assert.Equal(generated.Docs.Count, dry.UpdatedCount);
            Assert.Empty(api.Audit.Rows);
            Assert.All(snapshot, kv => Assert.Equal(kv.Value, api.Rooms.Stored[kv.Key]));

            api.Audit.Fail = true;
            var refused = api.Admin.RegenerateMap(new RegenerateMapRequest { Token = token, Map = Map });
            Assert.Equal(AdminResult.AuditFailed, refused.Result);
            Assert.All(snapshot, kv => Assert.Equal(kv.Value, api.Rooms.Stored[kv.Key]));

            api.Audit.Fail = false;
            var done = api.Admin.RegenerateMap(new RegenerateMapRequest { Token = token, Map = Map });
            Assert.Equal(AdminResult.Ok, done.Result);
            Assert.Equal(generated.Docs.Count, api.Audit.Rows.Count);
            Assert.All(api.Audit.Rows, r => Assert.True(r.Applied));
            Assert.All(api.Rooms.Cache.Values, r => Assert.DoesNotContain(r.Features, f => f.ShortDescription == "a hand made shrine"));
        }

        [Fact]
        public void AdminCalls_NeedACreatorAndAValidToken()
        {
            var api = new Api(_dir, Library());
            var player = api.Login("Pip", creator: false);
            Assert.Equal(AdminFailure.Forbidden, Assert.Throws<AdminCallException>(() => api.Admin.RegenerateMap(new RegenerateMapRequest { Token = player, Map = Map })).Failure);
            Assert.Equal(AdminFailure.Forbidden, Assert.Throws<AdminCallException>(() => api.Admin.UpdateRooms(new UpdateRoomsRequest { Token = player })).Failure);
            Assert.Equal(AdminFailure.Unauthenticated, Assert.Throws<AdminCallException>(() => api.Admin.RegenerateRooms(new RegenerateRoomsRequest { Token = "nope" })).Failure);
        }

        [Fact]
        public void AdminGetRoom_ShowsProvenanceAndTemplateReferences()
        {
            var lib = Library();
            var generated = Imported(lib);
            var key = GrassKey(generated);
            var api = new Api(_dir, lib);
            api.Load(generated);
            var token = api.Login("Cora", creator: true);

            var room = api.Admin.GetRoom(new GetRoomRequest { Token = token, Key = key }).Room;

            Assert.Equal(("sample_land", "grass"), (room.Gen.Map, room.Gen.Terrain));
            Assert.Equal(("rock", 1), (room.Features.Single(f => f.TemplateId == "rock").TemplateId, room.Features.Single(f => f.TemplateId == "rock").TemplateVersion));
        }
    }
}
