using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MUD.Contracts;
using MUD.Core.Administration;
using MUD.Core.GameObjects;
using MUD.Core.Import;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;
using Xunit;

namespace MUD.Tests
{
    // The exporter's header and scope rules, and the ExportArea rpc (specs/ADMIN_API.md "Area export"). No database: in-memory stores.
    public class AreaExportHeaderTests
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
    long: A hall.
    coords: {x: 0, y: 0, z: 0}
    exits:
      - {dir: north, to: study, reverse: south}
  - key: study
    short: a study
    long: A study.
    coords: {x: 0, y: 1, z: 0}
";

        private static FakeStore Imported()
        {
            var store = new FakeStore();
            Assert.True(new AreaImporter(store).Import(File).Success);
            return store;
        }

        [Fact]
        public void HeaderTakesNameAndDescriptionFromTheAreaRecord()
        {
            var areas = new FakeAreaStore();
            areas.Items["t"] = new MUD.Core.Areas.AreaEntry { Id = "t", Name = "The \"Test\" land", Description = "A small test region." };
            var result = new AreaExporter(Imported(), areas).ExportArea("t");
            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.Contains("  name: \"The \\\"Test\\\" land\"\n", result.Yaml);
            Assert.Contains("  description: \"A small test region.\"\n", result.Yaml);
            Assert.DoesNotContain("set the name by hand", result.Yaml);
        }

        [Fact]
        public void HeaderFallsBackToThePrefix_WithoutARecord_AndWithoutAnAreaStore()
        {
            foreach (var exporter in new[] { new AreaExporter(Imported()), new AreaExporter(Imported(), new FakeAreaStore()) })
            {
                var result = exporter.ExportArea("t");
                Assert.True(result.Success);
                Assert.Contains("  name: \"t\"\n", result.Yaml);
                Assert.DoesNotContain("description:", result.Yaml);
            }
        }

        [Fact]
        public void AnAreaStoreThatThrows_StillExports_WithThePrefixAsName()
        {
            var result = new AreaExporter(Imported(), new FakeAreaStore { FailReads = true }).ExportArea("t");
            Assert.True(result.Success);
            Assert.Contains("  name: \"t\"\n", result.Yaml);
        }

        [Fact]
        public void TheExportedFileReimportsToTheSameName_AndNeverOverwritesAnExistingRecord()
        {
            var areas = new FakeAreaStore();
            areas.Items["t"] = new MUD.Core.Areas.AreaEntry { Id = "t", Name = "Test land", Description = "A small test region." };
            var exported = new AreaExporter(Imported(), areas).ExportArea("t");

            // Fresh world: the record is created from the file's header.
            var fresh = new FakeAreaStore();
            var created = new AreaImporter(new FakeStore(), fresh).Import(exported.Yaml);
            Assert.True(created.Success, string.Join("; ", created.Errors));
            Assert.Equal("created", created.AreaRecord);
            Assert.Equal(("Test land", "A small test region."), (fresh.Items["t"].Name, fresh.Items["t"].Description));

            // Existing record (renamed on the web since): unchanged by the import.
            fresh.Items["t"].Name = "Renamed";
            var again = new AreaImporter(new FakeStore(), fresh).Import(exported.Yaml);
            Assert.Equal("exists", again.AreaRecord);
            Assert.Equal("Renamed", fresh.Items["t"].Name);
        }

        [Fact]
        public void RoundTripIsStable_ExportImportExport_AndTheSecondImportChangesNothing()
        {
            var areas = new FakeAreaStore();
            areas.Items["t"] = new MUD.Core.Areas.AreaEntry { Id = "t", Name = "Test land", Description = "A small test region." };
            var store = Imported();
            var first = new AreaExporter(store, areas).ExportArea("t");
            Assert.True(first.Success);

            var upsertsBefore = store.UpsertCount;
            var import = new AreaImporter(store, areas).Import(first.Yaml);
            Assert.True(import.Success);
            Assert.Equal(2, import.Count(ImportAction.Unchanged));
            Assert.Equal(upsertsBefore, store.UpsertCount);

            var second = new AreaExporter(store, areas).ExportArea("t");
            Assert.Equal(first.Yaml, second.Yaml);
        }

        [Fact]
        public void ExportCoversEveryLevelAndUnplacedRooms()
        {
            const string yaml = @"
area:
  schema_version: 1
  prefix: m
  name: M
rooms:
  - {key: a, short: a, long: A., coords: {x: 0, y: 0, z: 0}}
  - {key: b, short: b, long: B., coords: {x: 0, y: 0, z: 3}}
  - {key: c, short: c, long: C.}
";
            var store = new FakeStore();
            Assert.True(new AreaImporter(store).Import(yaml).Success);
            var result = new AreaExporter(store).ExportArea("m");
            Assert.True(result.Success);
            Assert.Equal(3, result.RoomCount);
            Assert.Contains("- key: a", result.Yaml);
            Assert.Contains("- key: b", result.Yaml);
            Assert.Contains("- key: c", result.Yaml);
        }

        [Fact]
        public void RoomRuntimeStateIsNeverExported_FloorItemsOccupantsCooldownsAndOpenExits()
        {
            const string yaml = @"
area:
  schema_version: 1
  prefix: s
  name: S
rooms:
  - key: hall
    short: a hall
    long: A hall.
    properties:
      - behavior: search
        id: s1
        params:
          target: hall
          refresh_ms: 5000
          reward: {short: a coin, long: A coin., handles: [coin]}
";
            var store = new FakeStore();
            Assert.True(new AreaImporter(store).Import(yaml).Success);
            var doc = store.Docs["s.hall"];
            // Runtime state as the game stores it: a floor item, occupants, a cooldown and an open temporary exit.
            doc["Items"] = new BsonArray { new BsonDocument { { "_id", "i1" }, { "ShortDescription", "FLOORROPE" } } };
            doc["Occupants"] = new BsonArray { "p.alice" };
            doc["Players"] = new BsonArray { new BsonDocument("PlayerName", "ALICEPLAYER") };
            doc["Npcs"] = new BsonArray { new BsonDocument("ShortDescription", "GOBLINNPC") };
            doc["search"]["entries"].AsBsonArray[0].AsBsonDocument["nextRewardAt"] = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            doc["OpenTemporaryExit"] = new BsonDocument { { "Name", "down" }, { "DestinationId", "s.vault" } };
            doc["TemporaryExits"] = new BsonArray { new BsonDocument { { "Name", "down" }, { "DestinationId", "s.vault" } } };

            var result = new AreaExporter(store).ExportArea("s");
            Assert.True(result.Success, string.Join("; ", result.Errors));
            foreach (var leaked in new[] { "FLOORROPE", "p.alice", "ALICEPLAYER", "GOBLINNPC", "nextRewardAt", "vault", "Items", "Occupants", "TemporaryExit" })
            {
                Assert.DoesNotContain(leaked, result.Yaml);
            }
            // The search reward is a definition, not a floor item: it stays.
            Assert.Contains("a coin", result.Yaml);
        }

        // x-gen on exported generated rooms: the exporter does not write room provenance, so the file holds hand data that re-import never wipes.
        [Fact]
        public void GeneratedRoomsExportWithoutXGen_AndReimportKeepsTheirTextAndTheirStoredProvenance()
        {
            var store = Imported();
            store.Docs["t.hall"]["gen"] = new BsonDocument { { "map", "m" }, { "terrain", "forest" }, { "seed", "1" }, { "libVersion", 1 } };
            var result = new AreaExporter(store).ExportArea("t");
            Assert.True(result.Success);
            Assert.Equal(1, result.GeneratedRoomCount);
            Assert.DoesNotContain("x-gen:", result.Yaml);

            store.Docs["t.hall"]["LongDescription"] = "Hand edited.";
            var import = new AreaImporter(store).Import(result.Yaml.Replace("A hall.", "Hand edited."), new ImportOptions { ReplaceGenerated = true });
            Assert.True(import.Success);
            Assert.Equal("Hand edited.", store.Docs["t.hall"]["LongDescription"].AsString);
            Assert.True(store.Docs["t.hall"].Contains("gen"));
        }
    }

    public class AdminExportAreaRpcTests
    {
        private class Host : IAdminHost
        {
            public bool Available = true;
            public int Runs;
            public string StartRoomKey { get { return "t.start"; } }
            public bool TryRunOnGameThread<T>(Func<T> work, out T result) { if (!Available) { result = default(T); return false; } Runs++; result = work(); return true; }
            public bool IsThrottled(string remoteKey) { return false; }
            public void RecordLoginOutcome(string remoteKey, bool success) { }
            public bool IsOnline(Player player) { return false; }
        }

        private class Audit : IAuditStore
        {
            public List<AuditRecord> Rows = new List<AuditRecord>();
            public void Insert(IReadOnlyList<AuditRecord> rows) { Rows.AddRange(rows); }
            public void SetOutcome(IReadOnlyList<string> ids, bool applied) { }
            public AuditPage Query(string entity, string entityId, int page, int pageSize) { return new AuditPage { Items = Rows, Total = Rows.Count }; }
        }

        private class Queries : IAdminQueries
        {
            public List<BsonDocument> ListPlayers(string nameFilter, bool descending, int page, int pageSize, out int total) { total = 0; return new List<BsonDocument>(); }
            public BsonDocument FindPlayer(string nameKey) { return null; }
            public int CountAdmins() { return 0; }
            public List<BsonDocument> ListRooms() { return new List<BsonDocument>(); }
            public List<BsonDocument> RoomsWithTemplateReward(string templateId) { return new List<BsonDocument>(); }
            public List<string> RoomIdsWithExitTo(string key) { return new List<string>(); }
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
            public FakeAreaStore Areas = new FakeAreaStore();
            public List<string> Log = new List<string>();
            public AdminApi Api;

            public Fixture()
            {
                Api = new AdminApi(Host, Players, Rooms, new Queries(), Audit, new AdminSessions(), new FakeClock(), Log.Add, max => 0, null, null, null, null, Areas);
            }

            public Room AddRoom(string key, string longText = null, bool cacheOnly = false)
            {
                var room = new Room(Rooms) { Id = key, ShortDescription = "room " + key, LongDescription = longText ?? "long " + key, Area = key.Substring(0, key.IndexOf('.')) };
                Rooms.Cache[key] = room;
                if (!cacheOnly) { Rooms.Stored[key] = room.ToBsonDocument(); }
                return room;
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
        }

        private static ExportAreaRequest Req(string token, string area) { return new ExportAreaRequest { Token = token, Area = area }; }

        [Fact]
        public void Ok_ReturnsTheYamlRoomCountAndNoNotes_ForCreatorAndAdmin()
        {
            var f = new Fixture();
            f.AddRoom("t.start"); f.AddRoom("t.hall");
            f.Areas.Items["t"] = new MUD.Core.Areas.AreaEntry { Id = "t", Name = "Test land", Description = "d" };
            foreach (var token in new[] { f.Login("Cora", creator: true), f.Login("Adam", admin: true) })
            {
                var reply = f.Api.ExportArea(Req(token, "t"));
                Assert.True(reply.Found);
                Assert.Empty(reply.Errors);
                Assert.Empty(reply.Notes);
                Assert.Equal(2, reply.RoomCount);
                Assert.Contains("name: \"Test land\"", reply.Yaml);
                Assert.Contains("- key: hall", reply.Yaml);
            }
            Assert.True(f.Host.Runs >= 2);
        }

        [Fact]
        public void IsReadOnly_NoAuditRow_NoRoomWrite()
        {
            var f = new Fixture();
            f.AddRoom("t.start");
            var before = f.Rooms.Stored["t.start"].ToJson();
            var upserts = f.Rooms.Upserts;
            f.Api.ExportArea(Req(f.Login("Cora", creator: true), "t"));
            Assert.Empty(f.Audit.Rows);
            Assert.Equal(upserts, f.Rooms.Upserts);
            Assert.Equal(before, f.Rooms.Stored["t.start"].ToJson());
        }

        [Fact]
        public void ALiveRoomEditNotYetInTheDatabase_IsWhatIsExported()
        {
            var f = new Fixture();
            f.AddRoom("t.start");
            f.Rooms.Cache["t.start"].LongDescription = "Edited live, still queued.";
            f.AddRoom("t.queued", cacheOnly: true);
            var reply = f.Api.ExportArea(Req(f.Login("Cora", creator: true), "t"));
            Assert.Contains("Edited live, still queued.", reply.Yaml);
            Assert.Equal(2, reply.RoomCount);   // a room created live and not yet written to the database is part of the current state
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("nowhere")]
        [InlineData("Bad Prefix")]
        [InlineData("t.\r\nX-Evil: 1")]
        public void UnknownEmptyOrInvalidArea_IsNotFound(string area)
        {
            var f = new Fixture();
            f.AddRoom("t.start");
            var reply = f.Api.ExportArea(Req(f.Login("Cora", creator: true), area));
            Assert.False(reply.Found);
            Assert.Equal(string.Empty, reply.Yaml);
        }

        [Fact]
        public void ExporterErrors_ComeBackInErrors_WithNoYaml()
        {
            var f = new Fixture();
            f.AddRoom("t.start");
            f.Rooms.Cache["t.start"].LongDescription = null;
            var reply = f.Api.ExportArea(Req(f.Login("Cora", creator: true), "t"));
            Assert.True(reply.Found);
            Assert.Equal(string.Empty, reply.Yaml);
            Assert.Contains(reply.Errors, e => e.Contains("t.start") && e.Contains("LongDescription is required"));
        }

        [Fact]
        public void ATooLargeFile_IsRefusedWithAnErrorThatPointsAtTheCommand()
        {
            var f = new Fixture();
            // Rooms are limited to 4000 characters of long text each, so a file over the limit takes many rooms.
            for (int i = 0; i < 900; i++) { f.AddRoom("t.r" + i, new string((char)(97 + i % 26), 3900)); }
            var reply = f.Api.ExportArea(Req(f.Login("Cora", creator: true), "t"));
            Assert.True(reply.Found);
            Assert.Equal(string.Empty, reply.Yaml);
            Assert.Equal(new[] { AdminApi.ExportTooLargeMessage }, reply.Errors.ToArray());
            Assert.Contains("3 MB", AdminApi.ExportTooLargeMessage);
            Assert.Contains("in-game", AdminApi.ExportTooLargeMessage);
        }

        [Fact]
        public void GeneratedRooms_AddANote_AndTheFileHasNoXGen()
        {
            var f = new Fixture();
            f.AddRoom("t.start");
            f.Rooms.Cache["t.start"].Gen = new GenProvenance { Map = "m", Terrain = "forest", Seed = "1", LibVersion = 1 };
            var reply = f.Api.ExportArea(Req(f.Login("Cora", creator: true), "t"));
            var note = Assert.Single(reply.Notes);
            Assert.StartsWith("1 room is generated.", note);
            Assert.DoesNotContain("x-gen: ", reply.Yaml.Replace("x-gen:\n", string.Empty));
        }

        [Fact]
        public void APlainPlayer_IsForbidden_AnUnknownToken_IsUnauthenticated()
        {
            var f = new Fixture();
            f.AddRoom("t.start");
            var player = f.Login("Pete");
            var forbidden = Assert.Throws<AdminCallException>(() => f.Api.ExportArea(Req(player, "t")));
            Assert.Equal(AdminFailure.Forbidden, forbidden.Failure);
            var unauthenticated = Assert.Throws<AdminCallException>(() => f.Api.ExportArea(Req("nope", "t")));
            Assert.Equal(AdminFailure.Unauthenticated, unauthenticated.Failure);
        }

        [Fact]
        public void WhenTheGameThreadDoesNotRun_TheCallIsUnavailable()
        {
            var f = new Fixture();
            f.AddRoom("t.start");
            var token = f.Login("Cora", creator: true);
            f.Host.Available = false;
            var ex = Assert.Throws<AdminCallException>(() => f.Api.ExportArea(Req(token, "t")));
            Assert.Equal(AdminFailure.Unavailable, ex.Failure);
        }

        [Fact]
        public void ADatabaseThatCannotBeRead_IsUnavailable_WithNoStoredDetailInTheMessage()
        {
            var f = new Fixture();
            var api = new AdminApi(f.Host, f.Players, new ThrowingRooms(), new Queries(), f.Audit, f.Api.Sessions, new FakeClock(), f.Log.Add, max => 0, null, null, null, null, f.Areas);
            var person = new Person("Cora", f.Players) { };
            person.Roles.Creator = true;
            f.Players.Live.Add(person);
            f.Players.Stored[person.Id] = person.ToBsonDocument();
            var token = f.Api.WebAuthenticate(new WebAuthRequest { Name = "Cora", Password = "secret1", RemoteKey = "k" }).Token;
            var ex = Assert.Throws<AdminCallException>(() => api.ExportArea(Req(token, "t")));
            Assert.Equal(AdminFailure.Unavailable, ex.Failure);
            Assert.DoesNotContain("secret", ex.Message);
            Assert.DoesNotContain(f.Log, l => l.Contains("secret"));
        }

        private class ThrowingRooms : SyncRoomRepo, IRoomRepository, ISnapshotRepository
        {
            public new List<string> GetIdsByField(string f, string v) { throw new InvalidOperationException("db down: secret detail"); }
        }
    }
}
