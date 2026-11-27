using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MUD.Contracts;
using MUD.Core.Administration;
using MUD.Core.Areas;
using MUD.Core.GameObjects;
using MUD.Core.Import;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;
using Xunit;

namespace MUD.Tests
{
    // Area entity (collection "area"): rules, backfill, importer behaviour and the Admin calls. No database: an in-memory IAreaStore.
    // MongoAreaStore was NOT run here (no MongoDB in the sandbox); see specs/ADMIN_API.md "Not verified here".
    public class FakeAreaStore : IAreaStore
    {
        public Dictionary<string, AreaEntry> Items = new Dictionary<string, AreaEntry>();
        public bool FailReads;
        public bool FailWrites;
        public bool RaceOnWrite;   // another writer got there first: Insert/Replace/Delete report false
        public int Inserts;

        public IReadOnlyList<AreaEntry> List() { Check(false); return Items.Values.OrderBy(a => a.Id, StringComparer.Ordinal).ToList(); }
        public AreaEntry Get(string id) { Check(false); return Items.TryGetValue(id, out var v) ? v : null; }
        public bool Insert(AreaEntry e, string by) { Check(true); if (RaceOnWrite || Items.ContainsKey(e.Id)) { return false; } Inserts++; Items[e.Id] = e; return true; }
        public bool Replace(AreaEntry e, string by) { Check(true); if (RaceOnWrite || !Items.ContainsKey(e.Id)) { return false; } Items[e.Id] = e; return true; }
        public bool Delete(string id) { Check(true); return !RaceOnWrite && Items.Remove(id); }
        private void Check(bool write) { if (write ? FailWrites : FailReads) { throw new InvalidOperationException("db down"); } }
        public FakeAreaStore With(string id, string name = null, string description = "") { Items[id] = new AreaEntry { Id = id, Name = name ?? id, Description = description }; return this; }
    }

    public class AreaRulesTests
    {
        [Theory]
        [InlineData("sample_area", "Sample area")]
        [InlineData("tower_of_aellioc", "Tower of aellioc")]
        [InlineData("a.b_c", "A b c")]
        [InlineData("x", "X")]
        public void ReadableName_TurnsAPrefixIntoWords(string prefix, string expected) { Assert.Equal(expected, AreaRules.ReadableName(prefix)); }

        [Fact]
        public void ReadableName_OfNothing_IsEmpty_AndIsCapped()
        {
            Assert.Equal(string.Empty, AreaRules.ReadableName(null));
            Assert.Equal(string.Empty, AreaRules.ReadableName("_"));
            Assert.Equal(AreaRules.MaxName, AreaRules.ReadableName(new string('a', 300)).Length);
        }

        [Fact]
        public void Name_IsRequired_OneLine_AndLimited()
        {
            Assert.Null(AreaRules.CheckName("The desert"));
            Assert.NotNull(AreaRules.CheckName(""));
            Assert.NotNull(AreaRules.CheckName(null));
            Assert.NotNull(AreaRules.CheckName(new string('a', AreaRules.MaxName + 1)));
            Assert.Null(AreaRules.CheckName(new string('a', AreaRules.MaxName)));
            Assert.NotNull(AreaRules.CheckName("two\nlines"));
        }

        [Fact]
        public void Description_IsOptional_OneLine_AndLimitedTo200()
        {
            Assert.Null(AreaRules.CheckDescription(""));
            Assert.Null(AreaRules.CheckDescription(null));
            Assert.Null(AreaRules.CheckDescription(new string('a', 200)));
            Assert.NotNull(AreaRules.CheckDescription(new string('a', 201)));
            Assert.NotNull(AreaRules.CheckDescription("two\nlines"));
            Assert.NotNull(AreaRules.CheckDescription("bell\a"));
        }

        [Fact]
        public void TheDocumentRoundTrips_AndTolerantOfMissingFields()
        {
            var entry = new AreaEntry { Id = "t", Name = "Test", Description = "d" };
            BsonDocument document = AreaDocuments.ToDocument(entry);
            Assert.Equal("t", document["_id"].AsString);
            AreaEntry back = AreaDocuments.From(document);
            Assert.Equal(("t", "Test", "d"), (back.Id, back.Name, back.Description));
            AreaEntry bare = AreaDocuments.From(new BsonDocument("_id", "x"));
            Assert.Equal((string.Empty, string.Empty), (bare.Name, bare.Description));
        }
    }

    public class AreaBackfillTests
    {
        [Fact]
        public void CreatesARecordForEveryDistinctRoomArea_WithAReadableName_AndNoDescription()
        {
            var store = new FakeAreaStore();
            var result = AreaBackfill.Run(store, new[] { "sample_area", "desert", "desert", "", null });
            Assert.Equal(2, result.Created);
            Assert.Equal("Sample area", store.Items["sample_area"].Name);
            Assert.Equal(string.Empty, store.Items["sample_area"].Description);
            Assert.Equal("Desert", store.Items["desert"].Name);
        }

        [Fact]
        public void NeverOverwritesAnExistingRecord_AndRunningTwiceChangesNothing()
        {
            var store = new FakeAreaStore().With("desert", "The Great Desert", "Dunes.");
            var first = AreaBackfill.Run(store, new[] { "desert", "temple" });
            Assert.Equal((1, 1), (first.Created, first.Skipped));
            Assert.Equal(("The Great Desert", "Dunes."), (store.Items["desert"].Name, store.Items["desert"].Description));
            int inserts = store.Inserts;
            var second = AreaBackfill.Run(store, new[] { "desert", "temple" });
            Assert.Equal((0, 2), (second.Created, second.Skipped));
            Assert.Equal(2, store.Items.Count);
            Assert.Equal(inserts, store.Inserts);
        }

        [Fact]
        public void ARoomAreaThatIsNotAValidPrefix_GetsNoRecord_AndIsCounted()
        {
            var store = new FakeAreaStore();
            var result = AreaBackfill.Run(store, new[] { "Bad Area", "ok" });
            Assert.Equal((1, 1), (result.Created, result.Invalid));
            Assert.Single(store.Items);
        }

        [Fact]
        public void NoRooms_NoRecords()
        {
            Assert.Equal(0, AreaBackfill.Run(new FakeAreaStore(), null).Created);
        }
    }

    public class AreaImporterRecordTests
    {
        private const string File = @"
area:
  schema_version: 1
  prefix: t
  name: Test land
  description: A small test region.
rooms:
  - key: hall
    short: a hall
    long: |
      A plain hall.
";

        [Fact]
        public void TheParserKeepsTheDescription_AndItIsOptional()
        {
            Assert.Equal("A small test region.", AreaParser.Parse(File).Area.Description);
            Assert.Null(AreaParser.Parse(File.Replace("  description: A small test region.\n", string.Empty)).Area.Description);
        }

        [Fact]
        public void ImportingCreatesTheRecord_FromTheFileNameAndDescription_WhenMissing()
        {
            var areas = new FakeAreaStore();
            var result = new AreaImporter(new FakeStore(), areas).Import(File);
            Assert.True(result.Success);
            Assert.Equal("created", result.AreaRecord);
            Assert.Equal(("Test land", "A small test region."), (areas.Items["t"].Name, areas.Items["t"].Description));
        }

        [Fact]
        public void ImportingNeverOverwritesAnExistingRecord_WebEditsWin()
        {
            var areas = new FakeAreaStore().With("t", "Edited on the web", "Edited too.");
            var result = new AreaImporter(new FakeStore(), areas).Import(File);
            Assert.True(result.Success);
            Assert.Equal("exists", result.AreaRecord);
            Assert.Equal(("Edited on the web", "Edited too."), (areas.Items["t"].Name, areas.Items["t"].Description));
            Assert.Equal(0, areas.Inserts);
        }

        [Fact]
        public void ImportingTwice_CreatesTheRecordOnce()
        {
            var areas = new FakeAreaStore();
            var importer = new AreaImporter(new FakeStore(), areas);
            importer.Import(File);
            var again = importer.Import(File.Replace("Test land", "Renamed in the file"));
            Assert.Equal("exists", again.AreaRecord);
            Assert.Equal("Test land", areas.Items["t"].Name);
            Assert.Equal(1, areas.Inserts);
        }

        [Fact]
        public void ADryRun_WritesNoRecord()
        {
            var areas = new FakeAreaStore();
            var result = new AreaImporter(new FakeStore(), areas).Import(File, dryRun: true);
            Assert.True(result.Success);
            Assert.Null(result.AreaRecord);
            Assert.Empty(areas.Items);
        }

        [Fact]
        public void AFailedValidation_WritesNoRecord()
        {
            var areas = new FakeAreaStore();
            var result = new AreaImporter(new FakeStore(), areas).Import(File.Replace("  - key: hall", "  - key: Bad Key"));
            Assert.False(result.Success);
            Assert.Empty(areas.Items);
        }

        [Fact]
        public void WithoutAnAreaStore_TheImporterWorksAsBefore()
        {
            var result = new AreaImporter(new FakeStore()).Import(File);
            Assert.True(result.Success);
            Assert.Null(result.AreaRecord);
        }

        [Fact]
        public void AFileNameOrDescriptionThatCannotBeStored_FallsBack_AndSaysSo()
        {
            var areas = new FakeAreaStore();
            string yaml = File.Replace("Test land", new string('n', 150)).Replace("A small test region.", new string('d', 300));
            var result = new AreaImporter(new FakeStore(), areas).Import(yaml);
            Assert.True(result.Success);
            Assert.Equal(("T", string.Empty), (areas.Items["t"].Name, areas.Items["t"].Description));
            Assert.Contains("name", result.AreaRecordNote);
            Assert.Contains("description", result.AreaRecordNote);
        }

        [Fact]
        public void AnAreaStoreThatThrows_DoesNotStopTheRooms()
        {
            var rooms = new FakeStore();
            var result = new AreaImporter(rooms, new FakeAreaStore { FailReads = true }).Import(File);
            Assert.True(result.Success);
            Assert.Null(result.AreaRecord);
            Assert.Contains("could not be checked or created", result.AreaRecordNote);
            Assert.True(rooms.Docs.ContainsKey("t.hall"));
        }

        [Fact]
        public void TheFileServiceReportsACreatedRecord_AndANote()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "areaimp-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "t.yaml"), File);
                var service = new AreaFileService(new AreaImporter(new FakeStore(), new FakeAreaStore()), dir);
                Assert.Contains("  created the area record for 't'.", service.ImportAll());
                var noted = new AreaFileService(new AreaImporter(new FakeStore(), new FakeAreaStore { FailReads = true }), dir).ImportAll();
                Assert.Contains(noted, l => l.StartsWith("  note: "));
            }
            finally { System.IO.Directory.Delete(dir, true); }
        }
    }

    [Collection("World construction")]
    public class AdminAreaApiTests
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

        private class Queries : IAdminQueries
        {
            public SyncRoomRepo Rooms;
            public bool Fail;
            public List<BsonDocument> ListPlayers(string nameFilter, bool descending, int page, int pageSize, out int total) { total = 0; return new List<BsonDocument>(); }
            public BsonDocument FindPlayer(string nameKey) { return null; }
            public int CountAdmins() { return 0; }
            public List<BsonDocument> ListRooms() { if (Fail) { throw new InvalidOperationException("db down"); } return Rooms.Stored.Select(p => { var d = p.Value.DeepClone().AsBsonDocument; d["_id"] = p.Key; return d; }).ToList(); }
            public List<string> RoomIdsWithExitTo(string key) { return Rooms.Stored.Where(p => p.Value.Contains("Exits") && p.Value["Exits"].AsBsonArray.Any(e => e["DestinationId"].AsString == key)).Select(p => p.Key).ToList(); }
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
            public SyncRoomRepo Rooms = new SyncRoomRepo();
            public AdminSessions Sessions = new AdminSessions();
            public FakeAreaStore Areas = new FakeAreaStore();
            public Queries Queries;
            public List<string> Log = new List<string>();
            public AdminApi Api;

            public Fixture(bool withAreas = true)
            {
                Queries = new Queries { Rooms = Rooms };
                Api = new AdminApi(Host, Players, Rooms, Queries, Audit, Sessions, new FakeClock(), Log.Add, max => 0, null, null, null, null, withAreas ? Areas : null);
                AddRoom("t.start");
            }

            public Room AddRoom(string key, string area = null)
            {
                var room = new Room(Rooms) { Id = key, ShortDescription = "room " + key, LongDescription = "long " + key, Area = area ?? key.Substring(0, key.IndexOf('.')) };
                Rooms.Cache[key] = room;
                Rooms.Stored[key] = room.ToBsonDocument();
                return room;
            }

            public string Login(string name, bool creator = false, bool admin = false, bool player = false)
            {
                var person = new Person(name, Players);
                person.Roles.Creator = !player && (creator || admin);
                person.Roles.Admin = !player && admin;
                Players.Live.Add(person);
                Players.Stored[person.Id] = person.ToBsonDocument();
                return Api.WebAuthenticate(new WebAuthRequest { Name = name, Password = "secret1", RemoteKey = "k" }).Token;
            }

            public string Creator() { return Login("Cora", creator: true); }
        }

        private static AreaRecord Record(string id, string name = "Name", string description = "")
        {
            return new AreaRecord { Id = id, Name = name, Description = description };
        }

        private static SaveAreaRequest Save(string token, AreaRecord record) { return new SaveAreaRequest { Token = token, Area = record }; }

        // ---------------------------------------------------------------- roles and tokens

        [Fact]
        public void EveryCall_NeedsAGoodToken_AndACreatorOrAdmin()
        {
            var f = new Fixture();
            string player = f.Login("Pete", player: true);
            string admin = f.Login("Ada", admin: true);
            foreach (string token in new[] { "", "nope" })
            {
                Assert.Equal(AdminFailure.Unauthenticated, Assert.Throws<AdminCallException>(() => f.Api.ListAreas(new TokenRequest { Token = token })).Failure);
                Assert.Equal(AdminFailure.Unauthenticated, Assert.Throws<AdminCallException>(() => f.Api.GetArea(new AreaIdRequest { Token = token, Id = "t" })).Failure);
                Assert.Equal(AdminFailure.Unauthenticated, Assert.Throws<AdminCallException>(() => f.Api.CreateArea(Save(token, Record("a")))).Failure);
                Assert.Equal(AdminFailure.Unauthenticated, Assert.Throws<AdminCallException>(() => f.Api.UpdateArea(Save(token, Record("a")))).Failure);
                Assert.Equal(AdminFailure.Unauthenticated, Assert.Throws<AdminCallException>(() => f.Api.DeleteArea(new AreaIdRequest { Token = token, Id = "a" })).Failure);
            }
            Assert.Equal(AdminFailure.Forbidden, Assert.Throws<AdminCallException>(() => f.Api.ListAreas(new TokenRequest { Token = player })).Failure);
            Assert.Equal(AdminFailure.Forbidden, Assert.Throws<AdminCallException>(() => f.Api.GetArea(new AreaIdRequest { Token = player, Id = "t" })).Failure);
            Assert.Equal(AdminFailure.Forbidden, Assert.Throws<AdminCallException>(() => f.Api.CreateArea(Save(player, Record("a")))).Failure);
            Assert.Equal(AdminFailure.Forbidden, Assert.Throws<AdminCallException>(() => f.Api.UpdateArea(Save(player, Record("a")))).Failure);
            Assert.Equal(AdminFailure.Forbidden, Assert.Throws<AdminCallException>(() => f.Api.DeleteArea(new AreaIdRequest { Token = player, Id = "a" })).Failure);
            Assert.Equal(AdminResult.Ok, f.Api.CreateArea(Save(admin, Record("by_admin"))).Result);   // an admin may do everything a creator may
            Assert.Empty(f.Audit.Rows.Where(r => r.EntityId == "a"));
        }

        [Fact]
        public void AWriteWhenTheGameThreadIsBusy_IsUnavailable()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Host.Available = false;
            Assert.Equal(AdminFailure.Unavailable, Assert.Throws<AdminCallException>(() => f.Api.CreateArea(Save(token, Record("a")))).Failure);
        }

        // ---------------------------------------------------------------- reads

        [Fact]
        public void ListAreas_ReturnsRecordsSortedById_WithTheRoomCount()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Areas.With("zone", "Zone", "Last").With("t", "Test", "First").With("empty", "Empty");
            f.AddRoom("t.a"); f.AddRoom("t.b"); f.AddRoom("zone.one");
            ListAreasReply reply = f.Api.ListAreas(new TokenRequest { Token = token });
            Assert.Equal(new[] { "empty", "t", "zone" }, reply.Areas.Select(a => a.Id));
            Assert.Equal(new[] { 0, 3, 1 }, reply.Areas.Select(a => a.RoomCount));   // t.start counts for t
            Assert.Equal(("Test", "First"), (reply.Areas[1].Name, reply.Areas[1].Description));
        }

        [Fact]
        public void ListAreas_CountsTheLiveRoom_NotTheStoredCopy()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Areas.With("t").With("u");
            f.AddRoom("t.a").Area = "u";   // edited live, not yet persisted
            ListAreasReply reply = f.Api.ListAreas(new TokenRequest { Token = token });
            Assert.Equal((1, 1), (reply.Areas.Single(a => a.Id == "t").RoomCount, reply.Areas.Single(a => a.Id == "u").RoomCount));
        }

        [Fact]
        public void ListAreas_WithoutAStore_IsEmpty_AndWhenTheDatabaseThrows_IsUnavailable()
        {
            var none = new Fixture(withAreas: false);
            Assert.Empty(none.Api.ListAreas(new TokenRequest { Token = none.Creator() }).Areas);

            var f = new Fixture();
            string token = f.Creator();
            f.Areas.FailReads = true;
            Assert.Equal(AdminFailure.Unavailable, Assert.Throws<AdminCallException>(() => f.Api.ListAreas(new TokenRequest { Token = token })).Failure);
            f.Areas.FailReads = false;
            f.Queries.Fail = true;
            Assert.Equal(AdminFailure.Unavailable, Assert.Throws<AdminCallException>(() => f.Api.ListAreas(new TokenRequest { Token = token })).Failure);
            Assert.DoesNotContain(f.Log, l => l.Contains("db down"));
        }

        [Fact]
        public void GetArea_Found_NotFound_NoStore_AndUnavailable()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Areas.With("t", "Test", "Desc");
            GetAreaReply found = f.Api.GetArea(new AreaIdRequest { Token = token, Id = " t " });
            Assert.True(found.Found);
            Assert.Equal(("Test", "Desc", 1), (found.Area.Name, found.Area.Description, found.Area.RoomCount));
            Assert.False(f.Api.GetArea(new AreaIdRequest { Token = token, Id = "nope" }).Found);
            var none = new Fixture(withAreas: false);
            Assert.False(none.Api.GetArea(new AreaIdRequest { Token = none.Creator(), Id = "t" }).Found);
            f.Areas.FailReads = true;
            Assert.Equal(AdminFailure.Unavailable, Assert.Throws<AdminCallException>(() => f.Api.GetArea(new AreaIdRequest { Token = token, Id = "t" })).Failure);
        }

        // ---------------------------------------------------------------- create

        [Fact]
        public void CreateArea_Ok_StoresIt_AndAuditsFirst()
        {
            var f = new Fixture();
            string token = f.Creator();
            AreaEditReply reply = f.Api.CreateArea(Save(token, Record(" sample_area ", "  Sample area  ", "A tiny place.")));
            Assert.Equal(AdminResult.Ok, reply.Result);
            Assert.Equal(("sample_area", "Sample area", "A tiny place."), (reply.Area.Id, reply.Area.Name, reply.Area.Description));
            Assert.Equal(("Sample area", "A tiny place."), (f.Areas.Items["sample_area"].Name, f.Areas.Items["sample_area"].Description));
            AuditRecord row = Assert.Single(f.Audit.Rows);
            Assert.Equal((AdminApi.AreaEntity, "sample_area", "*create*", true, "Cora"), (row.Entity, row.EntityId, row.Field, row.Applied, row.ActorName));
        }

        [Fact]
        public void CreateArea_Invalid_ListsEveryBrokenField_AndWritesNothing()
        {
            var f = new Fixture();
            string token = f.Creator();
            AreaEditReply reply = f.Api.CreateArea(Save(token, Record("Bad Prefix", "", new string('d', 201))));
            Assert.Equal(AdminResult.Invalid, reply.Result);
            Assert.Equal(new[] { "Id", "Name", "Description" }, reply.Errors.Select(e => e.Field));
            Assert.DoesNotContain("Bad Prefix", string.Join("|", reply.Errors.Select(e => e.Message)));   // nothing echoed from the input
            Assert.Empty(f.Areas.Items);
            Assert.Empty(f.Audit.Rows);

            Assert.Equal(AdminResult.Invalid, f.Api.CreateArea(Save(token, null)).Result);
            Assert.Equal(AdminResult.Invalid, f.Api.CreateArea(Save(token, Record(""))).Result);
            Assert.Equal(AdminResult.Invalid, f.Api.CreateArea(Save(token, Record("a", "two\nlines"))).Result);
        }

        [Fact]
        public void CreateArea_AlreadyExists_AlsoWhenSomeoneElseCreatedItInBetween()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Areas.With("a", "Mine");
            Assert.Equal(AdminResult.AlreadyExists, f.Api.CreateArea(Save(token, Record("a", "Other"))).Result);
            Assert.Equal("Mine", f.Areas.Items["a"].Name);
            Assert.Empty(f.Audit.Rows);

            f.Areas.RaceOnWrite = true;
            Assert.Equal(AdminResult.AlreadyExists, f.Api.CreateArea(Save(token, Record("b"))).Result);
            Assert.False(Assert.Single(f.Audit.Rows).Applied);   // the audit row is marked rejected
        }

        [Fact]
        public void CreateArea_AuditFailed_Refused_Failed()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Audit.FailInsert = true;
            Assert.Equal(AdminResult.AuditFailed, f.Api.CreateArea(Save(token, Record("a"))).Result);
            Assert.Empty(f.Areas.Items);   // no row, no write
            f.Audit.FailInsert = false;

            f.Areas.FailWrites = true;
            AreaEditReply failed = f.Api.CreateArea(Save(token, Record("a")));
            Assert.Equal(AdminResult.Failed, failed.Result);
            Assert.Equal(AdminApi.AreaWriteFailedMessage, failed.Message);
            Assert.True(f.Audit.Rows.Single().Rejected);
            Assert.DoesNotContain(f.Log, l => l.Contains("db down"));
            f.Areas.FailWrites = false;
            f.Areas.FailReads = true;
            Assert.Equal(AdminResult.Failed, f.Api.CreateArea(Save(token, Record("c"))).Result);

            var none = new Fixture(withAreas: false);
            AreaEditReply refused = none.Api.CreateArea(Save(none.Creator(), Record("a")));
            Assert.Equal((AdminResult.Refused, AdminApi.AreaUnavailableMessage), (refused.Result, refused.Message));
        }

        // ---------------------------------------------------------------- update

        [Fact]
        public void UpdateArea_ChangesNameAndDescriptionOnly_AuditsEachField()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Areas.With("a", "Old", "Old text");
            AreaEditReply reply = f.Api.UpdateArea(Save(token, Record("a", "New", "New text")));
            Assert.Equal(AdminResult.Ok, reply.Result);
            Assert.Equal(("New", "New text"), (f.Areas.Items["a"].Name, f.Areas.Items["a"].Description));
            Assert.Equal(new[] { "name", "description" }, f.Audit.Rows.Select(r => r.Field));
            Assert.All(f.Audit.Rows, r => Assert.True(r.Applied));
            Assert.Equal(("Old", "New"), (f.Audit.Rows[0].Old.AsString, f.Audit.Rows[0].New.AsString));

            f.Audit.Rows.Clear();
            Assert.Equal(AdminResult.Ok, f.Api.UpdateArea(Save(token, Record("a", "New", ""))).Result);   // clearing the description is a change
            Assert.Equal("description", Assert.Single(f.Audit.Rows).Field);
        }

        [Fact]
        public void UpdateArea_TheIdIsTheKey_ItCanNeverBeRenamed()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Areas.With("a", "A");
            AreaEditReply reply = f.Api.UpdateArea(Save(token, Record("b", "B")));   // a different id is another (unknown) area, never a rename
            Assert.Equal(AdminResult.NotFound, reply.Result);
            Assert.Equal(new[] { "a" }, f.Areas.Items.Keys);
            Assert.Equal("A", f.Areas.Items["a"].Name);
        }

        [Fact]
        public void UpdateArea_NoChange_Invalid_NotFound_AuditFailed_Failed_Refused()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Areas.With("a", "A", "d");
            AreaEditReply same = f.Api.UpdateArea(Save(token, Record("a", " A ", "d")));
            Assert.Equal((AdminResult.NoChange, AdminApi.AreaNothingChangedMessage), (same.Result, same.Message));
            Assert.Equal("A", same.Area.Name);
            Assert.Empty(f.Audit.Rows);

            Assert.Equal(AdminResult.Invalid, f.Api.UpdateArea(Save(token, Record("a", ""))).Result);
            Assert.Equal("A", f.Areas.Items["a"].Name);
            Assert.Equal(AdminResult.NotFound, f.Api.UpdateArea(Save(token, Record("zzz"))).Result);

            f.Audit.FailInsert = true;
            Assert.Equal(AdminResult.AuditFailed, f.Api.UpdateArea(Save(token, Record("a", "B"))).Result);
            Assert.Equal("A", f.Areas.Items["a"].Name);
            f.Audit.FailInsert = false;

            f.Areas.RaceOnWrite = true;   // removed by someone else between the read and the write
            Assert.Equal(AdminResult.NotFound, f.Api.UpdateArea(Save(token, Record("a", "B"))).Result);
            f.Areas.RaceOnWrite = false;

            f.Areas.FailWrites = true;
            Assert.Equal(AdminResult.Failed, f.Api.UpdateArea(Save(token, Record("a", "B"))).Result);

            var none = new Fixture(withAreas: false);
            Assert.Equal(AdminResult.Refused, none.Api.UpdateArea(Save(none.Creator(), Record("a"))).Result);
        }

        // ---------------------------------------------------------------- delete

        [Fact]
        public void DeleteArea_Ok_WhenNoRoomUsesIt_AuditsTheWholeRecord()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Areas.With("unused", "Unused", "Nobody lives here.");
            AreaEditReply reply = f.Api.DeleteArea(new AreaIdRequest { Token = token, Id = "unused" });
            Assert.Equal(AdminResult.Ok, reply.Result);
            Assert.Empty(f.Areas.Items);
            AuditRecord row = Assert.Single(f.Audit.Rows);
            Assert.Equal((AdminApi.AreaEntity, "unused", "*delete*", true), (row.Entity, row.EntityId, row.Field, row.Applied));
            Assert.Equal("Unused", row.Old.AsBsonDocument["name"].AsString);
        }

        [Fact]
        public void DeleteArea_IsRefused_WhileRoomsUseIt_AndTheMessageNamesTheCount()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Areas.With("t", "Test");
            AreaEditReply one = f.Api.DeleteArea(new AreaIdRequest { Token = token, Id = "t" });   // t.start only
            Assert.Equal(AdminResult.Refused, one.Result);
            Assert.Contains("1 room still uses", one.Message);
            f.AddRoom("t.a"); f.AddRoom("t.b");
            AreaEditReply three = f.Api.DeleteArea(new AreaIdRequest { Token = token, Id = "t" });
            Assert.Equal(AdminResult.Refused, three.Result);
            Assert.Contains("3 rooms still use", three.Message);
            Assert.True(f.Areas.Items.ContainsKey("t"));
            Assert.Empty(f.Audit.Rows);   // a refusal is not audited
        }

        [Fact]
        public void DeleteArea_NotFound_AuditFailed_Failed_Refused_AndTheRaces()
        {
            var f = new Fixture();
            string token = f.Creator();
            Assert.Equal(AdminResult.NotFound, f.Api.DeleteArea(new AreaIdRequest { Token = token, Id = "nope" }).Result);
            Assert.Equal(AdminResult.NotFound, f.Api.DeleteArea(new AreaIdRequest { Token = token, Id = "" }).Result);

            f.Areas.With("u");
            f.Audit.FailInsert = true;
            Assert.Equal(AdminResult.AuditFailed, f.Api.DeleteArea(new AreaIdRequest { Token = token, Id = "u" }).Result);
            Assert.True(f.Areas.Items.ContainsKey("u"));
            f.Audit.FailInsert = false;

            f.Areas.RaceOnWrite = true;
            Assert.Equal(AdminResult.NotFound, f.Api.DeleteArea(new AreaIdRequest { Token = token, Id = "u" }).Result);
            f.Areas.RaceOnWrite = false;

            f.Areas.FailWrites = true;
            Assert.Equal(AdminResult.Failed, f.Api.DeleteArea(new AreaIdRequest { Token = token, Id = "u" }).Result);
            f.Areas.FailWrites = false;
            f.Areas.FailReads = true;
            Assert.Equal(AdminResult.Failed, f.Api.DeleteArea(new AreaIdRequest { Token = token, Id = "u" }).Result);

            var none = new Fixture(withAreas: false);
            Assert.Equal(AdminResult.Refused, none.Api.DeleteArea(new AreaIdRequest { Token = none.Creator(), Id = "u" }).Result);
        }

        // ---------------------------------------------------------------- rooms need an area record

        [Fact]
        public void CreateRoom_IsRejected_ForAnAreaWithNoRecord_AndAcceptedOnceItHasOne()
        {
            var f = new Fixture();
            string token = f.Creator();
            var request = new RoomEditRequest { Token = token, Key = "newarea.hall", ShortDescription = "a hall", LongDescription = "A hall.", Area = "newarea" };
            RoomEditReply rejected = f.Api.CreateRoom(request);
            Assert.Equal(AdminResult.Invalid, rejected.Result);
            FieldError error = Assert.Single(rejected.Errors);
            Assert.Equal("Area", error.Field);
            Assert.Contains("no area with that prefix", error.Message);
            Assert.False(f.Rooms.Stored.ContainsKey("newarea.hall"));
            Assert.Empty(f.Audit.Rows);

            f.Areas.With("newarea");
            Assert.Equal(AdminResult.Ok, f.Api.CreateRoom(request).Result);
            Assert.True(f.Rooms.Stored.ContainsKey("newarea.hall"));
        }

        [Fact]
        public void CreateRoom_WithoutAnAreaStore_IsNotChecked()
        {
            var f = new Fixture(withAreas: false);
            var request = new RoomEditRequest { Token = f.Creator(), Key = "x.hall", ShortDescription = "a hall", LongDescription = "A hall.", Area = "x" };
            Assert.Equal(AdminResult.Ok, f.Api.CreateRoom(request).Result);
        }

        [Fact]
        public void MoveRoom_IsRejected_ForAnAreaWithNoRecord_AndAcceptedOnceItHasOne()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Areas.With("t");
            f.AddRoom("t.hall");
            RoomEditReply rejected = f.Api.MoveRoom(new MoveRoomRequest { Token = token, Key = "t.hall", NewArea = "elsewhere", NewLocalKey = "hall" });
            Assert.Equal(AdminResult.Invalid, rejected.Result);
            Assert.Equal("NewArea", Assert.Single(rejected.Errors).Field);
            Assert.True(f.Rooms.Stored.ContainsKey("t.hall"));
            Assert.False(f.Rooms.Stored.ContainsKey("elsewhere.hall"));

            f.Areas.With("elsewhere");
            Assert.Equal(AdminResult.Ok, f.Api.MoveRoom(new MoveRoomRequest { Token = token, Key = "t.hall", NewArea = "elsewhere", NewLocalKey = "hall" }).Result);
            Assert.True(f.Rooms.Stored.ContainsKey("elsewhere.hall"));
        }

        [Fact]
        public void AMoveWithAnInvalidAreaShape_StillReportsTheShapeNotTheMissingRecord()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.AddRoom("t.hall");
            RoomEditReply reply = f.Api.MoveRoom(new MoveRoomRequest { Token = token, Key = "t.hall", NewArea = "Bad Area", NewLocalKey = "hall" });
            Assert.Equal(AdminResult.Invalid, reply.Result);
            Assert.DoesNotContain(reply.Errors, e => e.Message.Contains("no area with that prefix"));
        }
    }
}
