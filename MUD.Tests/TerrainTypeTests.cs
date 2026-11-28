using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MongoDB.Bson;
using MUD.Contracts;
using MUD.Core.Administration;
using MUD.Core.Areas;
using MUD.Core.GameObjects;
using MUD.Core.Generation.Terrains;
using MUD.Core.Repositories.Interfaces;
using Xunit;

namespace MUD.Tests
{
    // Terrain types owned by areas: the startup migration of legacy library documents and the Admin terrain type calls.
    // No database: in-memory stores and fakes (MongoLibraryStore is not run here).
    public class LibraryMigrationTests
    {
        private static readonly MapAreaInfo SampleLand = new MapAreaInfo
        {
            Prefix = "sample_land", Name = "Sample Land", Description = "A small stretch of coast.",
            Terrains = new List<string> { "ocean", "grass", "forest", "river" },
        };

        private static readonly MapAreaInfo SampleCity = new MapAreaInfo
        {
            Prefix = "sample_city", Name = "Sample City", Description = "", Terrains = new List<string> { "wall", "street", "plaza", "gate" },
        };

        private static List<string> Terrains(params string[] t) { return t.ToList(); }

        // The live database (read-only look, 2026-11-27): 13 fragments, 3 features, 6 phrases, all with a legacy `terrains` list, version 1.
        private static FakeLibraryStore LiveShaped()
        {
            var s = new FakeLibraryStore { Version = 1 };
            void F(string id, string slot, params string[] t) { s.FragmentTable.Items[id] = new DescriptionFragment { Id = id, LegacyTerrains = Terrains(t), Slot = slot, Text = id, Weight = 1 }; }
            F("grass-short-1", "short", "grass"); F("grass-short-2", "short", "grass"); F("grass-ground-1", "ground", "grass"); F("grass-ground-2", "ground", "grass");
            F("forest-short-1", "short", "forest"); F("forest-short-2", "short", "forest"); F("forest-ground-1", "ground", "forest"); F("forest-ground-2", "ground", "forest");
            F("river-short-1", "short", "river"); F("river-ground-1", "ground", "river");
            F("ocean-short-1", "short", "ocean"); F("ocean-ground-1", "ground", "ocean"); F("ocean-ground-2", "ground", "ocean");
            void T(string id, params string[] t) { s.FeatureTable.Items[id] = new FeatureTemplate { Id = id, LegacyTerrains = Terrains(t), Short = id, Long = id, Version = 1 }; }
            T("boulder-grey", "grass", "forest"); T("fallen-log", "forest"); T("driftwood", "ocean");
            void P(string id, string nbr, params string[] t) { s.PhraseTable.Items[id] = new NeighborPhrase { Id = id, LegacyTerrains = Terrains(t), NeighborTerrain = nbr, Text = "{dir}" }; }
            P("river-near", "river", "grass", "forest"); P("river-near-alt", "river", "grass", "forest"); P("forest-near", "forest", "grass");
            P("ocean-near", "ocean", "grass", "forest", "river"); P("ocean-near-alt", "ocean", "grass", "forest", "river"); P("river-meets-ocean", "river", "ocean");
            return s;
        }

        private static LibraryMigrationResult Run(FakeLibraryStore store, FakeAreaStore areas, List<string> log, params MapAreaInfo[] maps)
        {
            return LibraryMigration.Run(store, areas, maps, log.Add);
        }

        [Fact]
        public void TheLiveShapedLibraryMigratesToSampleLandWith4TerrainTypes_13Fragments_4Features_12Phrases()
        {
            var store = LiveShaped();
            var areas = new FakeAreaStore().With("sample_area").With("planar_centrum");   // as in the live database: no sample_land record
            var log = new List<string>();

            LibraryMigrationResult r = Run(store, areas, log, SampleLand, SampleCity);

            Assert.True(areas.Items.ContainsKey("sample_land"));
            Assert.Equal(("Sample Land", "A small stretch of coast."), (areas.Items["sample_land"].Name, areas.Items["sample_land"].Description));
            Assert.False(areas.Items.ContainsKey("sample_city"));   // no library content names a sample_city terrain
            Assert.Equal(1, r.AreasCreated);
            Assert.Equal(new[] { "sample_land.forest", "sample_land.grass", "sample_land.ocean", "sample_land.river" }, store.TerrainTypeTable.Items.Keys.OrderBy(x => x));
            Assert.Equal((13, 4, 12), (store.FragmentTable.Items.Count, store.FeatureTable.Items.Count, store.PhraseTable.Items.Count));
            Assert.Equal((13, 4, 12), (r.Fragments, r.Features, r.Phrases));   // documents written, copies included
            Assert.Equal((22, 7), (r.Migrated, r.Copies));                   // 13+3+6 originals moved, 7 split copies added
            Assert.Empty(r.Unmigrated);
            Assert.Equal(2, store.Version);   // bumped once
            Assert.True(r.VersionBumped);
            Assert.Contains(log, l => l.Contains("Terrain library migration:"));
        }

        [Fact]
        public void EveryMigratedEntryHasExactlyOneTerrainInTheSameAreaAndTheLegacyArrayIsKept()
        {
            var store = LiveShaped();
            Run(store, new FakeAreaStore(), new List<string>(), SampleLand);

            Assert.All(store.FragmentTable.Items.Values, f => Assert.StartsWith("sample_land.", f.Terrain));
            Assert.All(store.FeatureTable.Items.Values, f => Assert.StartsWith("sample_land.", f.Terrain));
            Assert.All(store.PhraseTable.Items.Values, p => { Assert.StartsWith("sample_land.", p.Terrain); Assert.StartsWith("sample_land.", p.NeighborTerrain); });
            Assert.Equal(new[] { "grass", "forest" }, store.FeatureTable.Items["boulder-grey"].LegacyTerrains);
            Assert.Equal(new[] { "grass" }, store.FragmentTable.Items["grass-short-1"].LegacyTerrains);
        }

        [Fact]
        public void SharedEntriesAreSplit_TheFirstTerrainKeepsTheOriginalId_EveryOtherGetsACopyNamedIdDashLocal()
        {
            var store = LiveShaped();
            Run(store, new FakeAreaStore(), new List<string>(), SampleLand);

            Assert.Equal("sample_land.grass", store.FeatureTable.Items["boulder-grey"].Terrain);
            Assert.Equal("sample_land.forest", store.FeatureTable.Items["boulder-grey-forest"].Terrain);
            Assert.Equal(store.FeatureTable.Items["boulder-grey"].Short, store.FeatureTable.Items["boulder-grey-forest"].Short);

            Assert.Equal(("sample_land.grass", "sample_land.river"), (store.PhraseTable.Items["river-near"].Terrain, store.PhraseTable.Items["river-near"].NeighborTerrain));
            Assert.Equal(("sample_land.forest", "sample_land.river"), (store.PhraseTable.Items["river-near-forest"].Terrain, store.PhraseTable.Items["river-near-forest"].NeighborTerrain));
            Assert.Equal("sample_land.river", store.PhraseTable.Items["ocean-near-river"].Terrain);
            Assert.Equal("sample_land.ocean", store.PhraseTable.Items["ocean-near-river"].NeighborTerrain);
            Assert.Equal("sample_land.forest", store.PhraseTable.Items["ocean-near-alt-forest"].Terrain);
            // The split ids of the shipped seed and of the migration agree.
            var seed = LibrarySeed.Parse(LibrarySeed.ReadEmbedded(), new List<string>(), new List<string>());
            Assert.Equal(seed.Phrases.Select(p => p.Id).OrderBy(x => x), store.PhraseTable.Items.Keys.OrderBy(x => x));
            Assert.Equal(seed.Features.Select(p => p.Id).OrderBy(x => x), store.FeatureTable.Items.Keys.OrderBy(x => x));
            Assert.Equal(seed.Fragments.Select(p => p.Id).OrderBy(x => x), store.FragmentTable.Items.Keys.OrderBy(x => x));
        }

        [Fact]
        public void ANeighbourIsResolvedInTheSameAreaAsTheRoomTerrain()
        {
            var store = new FakeLibraryStore { Version = 1 };
            store.PhraseTable.Items["p"] = new NeighborPhrase { Id = "p", LegacyTerrains = Terrains("grass"), NeighborTerrain = "river", Text = "{dir}" };
            // grass and river both occur in sample_land; the city has a grass-less legend, so the phrase belongs to sample_land only.
            Run(store, new FakeAreaStore(), new List<string>(), SampleCity, SampleLand);
            Assert.Equal(("sample_land.grass", "sample_land.river"), (store.PhraseTable.Items["p"].Terrain, store.PhraseTable.Items["p"].NeighborTerrain));
        }

        [Fact]
        public void APhraseWhoseNeighbourIsNotUsedByTheMapIsNotWrittenForThatMapAndIsReported()
        {
            var store = new FakeLibraryStore { Version = 1 };
            store.PhraseTable.Items["p"] = new NeighborPhrase { Id = "p", LegacyTerrains = Terrains("grass"), NeighborTerrain = "swamp", Text = "{dir}" };
            var log = new List<string>();
            LibraryMigrationResult r = Run(store, new FakeAreaStore(), log, SampleLand);
            Assert.Null(store.PhraseTable.Items["p"].Terrain);
            Assert.Equal(new[] { "p" }, r.Unmigrated);
            Assert.Contains(log, l => l.Contains("neighbour terrain 'swamp' is not used by that map"));
        }

        [Fact]
        public void ANameUsedByNoMapFileIsNotMigrated_ItsIdIsLogged_AndTheDocumentIsLeftUntouched()
        {
            var store = new FakeLibraryStore { Version = 1 };
            store.FragmentTable.Items["lava"] = new DescriptionFragment { Id = "lava", LegacyTerrains = Terrains("lava"), Slot = "ground", Text = "Hot." };
            store.FragmentTable.Items["g"] = new DescriptionFragment { Id = "g", LegacyTerrains = Terrains("grass"), Slot = "ground", Text = "Green." };
            var log = new List<string>();

            LibraryMigrationResult r = Run(store, new FakeAreaStore(), log, SampleLand);

            Assert.Equal(new[] { "lava" }, r.Unmigrated);
            Assert.Null(store.FragmentTable.Items["lava"].Terrain);
            Assert.Equal(new[] { "lava" }, store.FragmentTable.Items["lava"].LegacyTerrains);
            Assert.Contains(log, l => l.Contains("'lava'") && l.Contains("used by no map file"));
            Assert.Equal("sample_land.grass", store.FragmentTable.Items["g"].Terrain);
            Assert.False(store.TerrainTypeTable.Items.ContainsKey("sample_land.lava"));
        }

        [Fact]
        public void AnEntryWithAnEmptyLegacyListIsNotMigrated_ItsIdIsLogged_AndTheDocumentIsLeftUntouched()
        {
            var store = new FakeLibraryStore { Version = 1 };
            store.FeatureTable.Items["off"] = new FeatureTemplate { Id = "off", LegacyTerrains = new List<string>(), Short = "s", Long = "l" };
            var log = new List<string>();

            LibraryMigrationResult r = Run(store, new FakeAreaStore(), log, SampleLand);

            Assert.Equal(new[] { "off" }, r.Unmigrated);
            Assert.Null(store.FeatureTable.Items["off"].Terrain);
            Assert.Contains(log, l => l.Contains("'off'") && l.Contains("lists no terrain"));
            Assert.False(r.VersionBumped);       // nothing migrated: no version bump
            Assert.Equal(1, store.Version);
            Assert.Empty(store.TerrainTypeTable.Items);
        }

        [Fact]
        public void AnExistingAreaAndTerrainTypeAreNeverOverwritten()
        {
            var store = LiveShaped();
            var areas = new FakeAreaStore().With("sample_land", "Renamed by hand", "mine");
            store.TerrainTypeTable.Items["sample_land.grass"] = new TerrainType { Id = "sample_land.grass", Name = "Meadow", Description = "edited" };

            LibraryMigrationResult r = Run(store, areas, new List<string>(), SampleLand);

            Assert.Equal(0, r.AreasCreated);
            Assert.Equal("Renamed by hand", areas.Items["sample_land"].Name);
            Assert.Equal("Meadow", store.TerrainTypeTable.Items["sample_land.grass"].Name);
            Assert.Equal(3, r.TerrainTypesCreated);
        }

        [Fact]
        public void ASecondRunChangesNothing()
        {
            var store = LiveShaped();
            var areas = new FakeAreaStore();
            Run(store, areas, new List<string>(), SampleLand);
            string snapshot = Snapshot(store, areas);
            int version = store.Version.Value;

            var log = new List<string>();
            LibraryMigrationResult second = Run(store, areas, log, SampleLand);

            Assert.Equal(snapshot, Snapshot(store, areas));
            Assert.Equal(version, store.Version);
            Assert.False(second.Changed);
            Assert.Empty(log);   // nothing to do: no log line
        }

        [Fact]
        public void ARunThatDiedHalfWayIsFinishedByTheNextRun_WithoutDuplicates()
        {
            var store = LiveShaped();
            var areas = new FakeAreaStore();
            // A previous run wrote a copy and created the terrain types, then died before replacing the original.
            store.TerrainTypeTable.Items["sample_land.forest"] = new TerrainType { Id = "sample_land.forest", Name = "Forest" };
            store.FeatureTable.Items["boulder-grey-forest"] = new FeatureTemplate { Id = "boulder-grey-forest", Terrain = "sample_land.forest", LegacyTerrains = Terrains("grass", "forest"), Short = "a grey boulder", Long = "l" };

            Run(store, areas, new List<string>(), SampleLand);

            Assert.Equal(4, store.FeatureTable.Items.Count);
            Assert.Equal("sample_land.grass", store.FeatureTable.Items["boulder-grey"].Terrain);
            Assert.Equal("sample_land.forest", store.FeatureTable.Items["boulder-grey-forest"].Terrain);
        }

        [Fact]
        public void ACopyIdAlreadyTakenByAnotherEntryGetsTheAreaInItsName()
        {
            var store = new FakeLibraryStore { Version = 1 };
            store.FeatureTable.Items["rock"] = new FeatureTemplate { Id = "rock", LegacyTerrains = Terrains("grass", "forest"), Short = "a rock", Long = "l" };
            store.FeatureTable.Items["rock-forest"] = new FeatureTemplate { Id = "rock-forest", Terrain = "elsewhere.sand", Short = "other", Long = "l" };
            Run(store, new FakeAreaStore(), new List<string>(), SampleLand);
            Assert.Equal("sample_land.forest", store.FeatureTable.Items["rock-sample_land-forest"].Terrain);
            Assert.Equal("other", store.FeatureTable.Items["rock-forest"].Short);
        }

        [Fact]
        public void ANameUsedByTwoMapsIsWrittenOncePerArea_TheFirstInPrefixOrderKeepsTheId()
        {
            var store = new FakeLibraryStore { Version = 1 };
            store.FragmentTable.Items["g"] = new DescriptionFragment { Id = "g", LegacyTerrains = Terrains("grass"), Slot = "ground", Text = "Green." };
            var farm = new MapAreaInfo { Prefix = "farm", Name = "Farm", Terrains = Terrains("grass") };
            Run(store, new FakeAreaStore(), new List<string>(), SampleLand, farm);
            Assert.Equal("farm.grass", store.FragmentTable.Items["g"].Terrain);
            Assert.Equal("sample_land.grass", store.FragmentTable.Items["g-grass"].Terrain);
        }

        [Fact]
        public void WithoutAnyMapFilesNothingIsMigratedAndEverythingIsReported()
        {
            var store = LiveShaped();
            LibraryMigrationResult r = Run(store, new FakeAreaStore(), new List<string>());
            Assert.Equal(13 + 3 + 6, r.Unmigrated.Count);
            Assert.False(r.VersionBumped);
            Assert.Empty(store.TerrainTypeTable.Items);
        }

        [Fact]
        public void AFreshInstallWithNoLibraryYetIsNotTouched()
        {
            var store = new FakeLibraryStore();
            var log = new List<string>();
            LibraryMigration.Run(store, new FakeAreaStore(), new[] { SampleLand }, log.Add);
            Assert.Empty(log);
            Assert.Null(store.Version);
        }

        [Fact]
        public void TheMigrationWorksWithoutAnAreaStore_AndTheTerrainTypesStillGetCreated()
        {
            var store = LiveShaped();
            LibraryMigration.Run(store, null, new[] { SampleLand }, _ => { });
            Assert.Equal(4, store.TerrainTypeTable.Items.Count);
        }

        [Fact]
        public void AMigratedLibraryGivesTheGeneratorTheSameContentAsTheShippedSeed()
        {
            var migrated = LiveShaped();
            Run(migrated, new FakeAreaStore(), new List<string>(), SampleLand);
            var seeded = new FakeLibraryStore();
            LibrarySeed.SeedIfFresh(seeded, new FakeAreaStore(), LibrarySeed.ReadEmbedded());
            var a = new StoreLibrary(migrated);
            var b = new StoreLibrary(seeded);
            foreach (string t in new[] { "grass", "forest", "river", "ocean" })
            {
                string id = "sample_land." + t;
                Assert.Equal(b.Features(id).Select(f => f.Id), a.Features(id).Select(f => f.Id));
                Assert.Equal(b.Fragments(id, "short").Select(f => f.Id), a.Fragments(id, "short").Select(f => f.Id));
                foreach (string n in new[] { "grass", "forest", "river", "ocean" })
                {
                    Assert.Equal(b.Phrases(id, "sample_land." + n).Select(f => f.Id), a.Phrases(id, "sample_land." + n).Select(f => f.Id));
                }
            }
        }

        private static string Snapshot(FakeLibraryStore s, FakeAreaStore a)
        {
            var doc = new BsonDocument
            {
                { "frag", new BsonArray(s.FragmentTable.Items.Values.OrderBy(x => x.Id).Select(x => LibraryDocuments.ToDocument(x))) },
                { "feat", new BsonArray(s.FeatureTable.Items.Values.OrderBy(x => x.Id).Select(x => LibraryDocuments.ToDocument(x))) },
                { "phr", new BsonArray(s.PhraseTable.Items.Values.OrderBy(x => x.Id).Select(x => LibraryDocuments.ToDocument(x))) },
                { "tt", new BsonArray(s.TerrainTypeTable.Items.Values.OrderBy(x => x.Id).Select(x => TerrainTypeDocuments.ToDocument(x))) },
                { "area", new BsonArray(a.Items.Values.OrderBy(x => x.Id).Select(x => AreaDocuments.ToDocument(x))) },
            };
            return doc.ToJson();
        }
    }

    public class MapAreaReaderTests
    {
        private const string Land = "generator:\n  prefix: sample_land\n  name: \"Sample Land\"\n  description: \"A coast.\"\n  seed: s\ngrid:\n  - \"~.\"\nlegend:\n  \"~\": { template: ocean, color: \"#2f5fa8\" }\n  \".\": grass\ntemplates:\n  ocean: {}\n  grass: {}\n";

        [Fact]
        public void ReadsTheGeneratorBlockAndTheLegendTemplateNames()
        {
            var info = MapAreaReader.Read(Land);
            Assert.Equal(("sample_land", "Sample Land", "A coast."), (info.Prefix, info.Name, info.Description));
            Assert.Equal(new[] { "ocean", "grass" }, info.Terrains);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not: a map")]
        [InlineData("generator:\n  prefix: Bad Prefix\n")]
        [InlineData("generator: [oops")]
        public void ANonMapOrAnInvalidPrefixIsNull(string yaml)
        {
            Assert.Null(MapAreaReader.Read(yaml));
        }

        [Fact]
        public void TheShippedMapFilesGiveSampleLandAndSampleCity()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "MUD.sln"))) { dir = Path.GetDirectoryName(dir); }
            var maps = MapAreaReader.ReadDirectory(Path.Combine(dir, "MUD.Main", "Content"));
            Assert.Equal(new[] { "sample_city", "sample_land" }, maps.Select(m => m.Prefix).OrderBy(x => x));
            Assert.Equal(new[] { "forest", "grass", "ocean", "river" }, maps.Single(m => m.Prefix == "sample_land").Terrains.OrderBy(x => x));
            Assert.Contains("wall", maps.Single(m => m.Prefix == "sample_city").Terrains);
        }

        [Fact]
        public void AMissingDirectoryGivesNoMaps()
        {
            Assert.Empty(MapAreaReader.ReadDirectory(null));
            Assert.Empty(MapAreaReader.ReadDirectory(Path.Combine(Path.GetTempPath(), "no-such-dir-" + Guid.NewGuid().ToString("N"))));
        }
    }

    [Collection("World construction")]
    public class AdminTerrainTypeApiTests
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

            public Fixture(bool withLibrary = true, bool withAreas = true)
            {
                Cache = new StoreLibrary(Store);
                Api = new AdminApi(Host, Players, new SyncRoomRepo(), new NoQueries(), Audit, Sessions, new FakeClock(), Log.Add, max => 0, null,
                    withLibrary ? Store : null, withLibrary ? Cache : null, null, withAreas ? Areas : null);
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

        private static TerrainTypeRecord Type(string id = "demo.swamp", string name = "Swamp", string description = "Wet.")
        {
            return new TerrainTypeRecord { Id = id, Name = name, Description = description };
        }

        private static AdminCallException Throws(Action call) { return Assert.Throws<AdminCallException>(call); }

        // ---------------------------------------------------------------- Ok

        [Fact]
        public void Create_Ok_StoresIt_FillsTheArea_AndAuditsFirstWithEntityTerrainType()
        {
            var f = new Fixture();
            string token = f.Creator();

            TerrainTypeEditReply reply = f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type() });

            Assert.Equal(AdminResult.Ok, reply.Result);
            Assert.Equal(("demo.swamp", "demo", "Swamp", "Wet."), (reply.TerrainType.Id, reply.TerrainType.Area, reply.TerrainType.Name, reply.TerrainType.Description));
            Assert.Equal("Swamp", f.Store.TerrainTypeTable.Items["demo.swamp"].Name);
            AuditRecord row = Assert.Single(f.Audit.Rows);
            Assert.Equal(("terrain_type", "demo.swamp", "*create*", true), (row.Entity, row.EntityId, row.Field, row.Applied));
            Assert.Equal("Cora", row.ActorName);
        }

        [Fact]
        public void Create_ByAnAdmin_Works_AndTheAreaIdMayContainDots()
        {
            var f = new Fixture();
            f.Areas.With("a.b");
            string token = f.Login("Ada", admin: true);
            Assert.Equal(AdminResult.Ok, f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type("a.b.grass", "Grass") }).Result);
            Assert.Equal("a.b", f.Store.TerrainTypeTable.Items["a.b.grass"].Area);
            Assert.Equal("Admin", f.Audit.Rows.Single().ActorRole);
        }

        [Fact]
        public void Update_ChangesNameAndDescriptionOnly_AuditsOneRowPerChangedField()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type() });
            f.Audit.Rows.Clear();

            var edit = Type(name: "Marsh", description: "Wet.");
            edit.Area = "other";   // read-only: ignored
            TerrainTypeEditReply reply = f.Api.UpdateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = edit });

            Assert.Equal(AdminResult.Ok, reply.Result);
            Assert.Equal(("Marsh", "demo"), (reply.TerrainType.Name, reply.TerrainType.Area));
            AuditRecord row = Assert.Single(f.Audit.Rows);
            Assert.Equal(("name", "Swamp", "Marsh"), (row.Field, row.Old.AsString, row.New.AsString));
        }

        [Fact]
        public void Get_AndList_ReturnTheStoredTypes_ListFiltersByArea_AndGetOfAnUnknownIdIsNotFound()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type("demo.b") });
            f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type("other.a") });
            f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type("demo.a") });

            Assert.Equal(new[] { "demo.a", "demo.b", "other.a" }, f.Api.ListTerrainTypes(new ListTerrainTypesRequest { Token = token }).TerrainTypes.Select(t => t.Id));
            Assert.Equal(new[] { "demo.a", "demo.b" }, f.Api.ListTerrainTypes(new ListTerrainTypesRequest { Token = token, Area = "demo" }).TerrainTypes.Select(t => t.Id));
            Assert.Empty(f.Api.ListTerrainTypes(new ListTerrainTypesRequest { Token = token, Area = "nope" }).TerrainTypes);
            Assert.True(f.Api.GetTerrainType(new TerrainTypeIdRequest { Token = token, Id = "demo.a" }).Found);
            Assert.False(f.Api.GetTerrainType(new TerrainTypeIdRequest { Token = token, Id = "demo.zzz" }).Found);
        }

        [Fact]
        public void Delete_Ok_WhenNothingUsesIt_AuditsTheOldDocument()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type() });
            f.Audit.Rows.Clear();

            Assert.Equal(AdminResult.Ok, f.Api.DeleteTerrainType(new TerrainTypeIdRequest { Token = token, Id = "demo.swamp" }).Result);

            Assert.Empty(f.Store.TerrainTypeTable.Items);
            AuditRecord row = Assert.Single(f.Audit.Rows);
            Assert.Equal("*delete*", row.Field);
            Assert.Equal("Swamp", row.Old.AsBsonDocument["name"].AsString);
        }

        // ---------------------------------------------------------------- Invalid

        [Theory]
        [InlineData("swamp", "Id")]                // no area part
        [InlineData("demo.Swamp", "Id")]           // uppercase local
        [InlineData("demo.sw amp", "Id")]
        [InlineData("demo.", "Id")]
        [InlineData("Demo.swamp", "Id")]           // not a valid area prefix
        [InlineData("", "Id")]
        public void Create_WithABadId_IsInvalid_AndNothingIsWrittenOrAudited(string id, string field)
        {
            var f = new Fixture();
            string token = f.Creator();
            TerrainTypeEditReply reply = f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type(id) });
            Assert.Equal(AdminResult.Invalid, reply.Result);
            Assert.Contains(reply.Errors, e => e.Field == field);
            Assert.Empty(f.Store.TerrainTypeTable.Items);
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void Create_InAnAreaThatHasNoRecord_IsInvalid()
        {
            var f = new Fixture();
            string token = f.Creator();
            TerrainTypeEditReply reply = f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type("ghost.swamp") });
            Assert.Equal(AdminResult.Invalid, reply.Result);
            Assert.Equal("Id", reply.Errors[0].Field);
            Assert.Contains("no area", reply.Errors[0].Message);
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void NameIsRequired_AndTheDescriptionIsOptionalUpTo200Characters()
        {
            var f = new Fixture();
            string token = f.Creator();
            Assert.Contains(f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type(name: "") }).Errors, e => e.Field == "Name");
            Assert.Contains(f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type(description: new string('x', 201)) }).Errors, e => e.Field == "Description");
            Assert.Equal(AdminResult.Ok, f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type("demo.a", "A", new string('x', 200)) }).Result);
            Assert.Equal(AdminResult.Ok, f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type("demo.b", "B", "") }).Result);
            Assert.Equal(AdminResult.Invalid, f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token }).Result);
        }

        // ---------------------------------------------------------------- NoChange / AlreadyExists / NotFound

        [Fact]
        public void Update_WithNothingChanged_IsNoChange_AndNothingIsAudited()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type() });
            int audits = f.Audit.Rows.Count;
            TerrainTypeEditReply reply = f.Api.UpdateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type() });
            Assert.Equal(AdminResult.NoChange, reply.Result);
            Assert.Equal("Swamp", reply.TerrainType.Name);
            Assert.Equal(audits, f.Audit.Rows.Count);
        }

        [Fact]
        public void Create_OfATakenId_IsAlreadyExists_AndChangesNothing()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type() });
            int audits = f.Audit.Rows.Count;
            TerrainTypeEditReply reply = f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type(name: "Other") });
            Assert.Equal((AdminResult.AlreadyExists, AdminApi.TerrainTypeAlreadyExistsMessage), (reply.Result, reply.Message));
            Assert.Equal("Swamp", f.Store.TerrainTypeTable.Items["demo.swamp"].Name);
            Assert.Equal(audits, f.Audit.Rows.Count);
        }

        [Fact]
        public void Create_LosingARaceAfterTheAudit_IsAlreadyExists_AndTheAuditRowIsRejected()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Store.TerrainTypeTable.RaceOnWrite = true;
            TerrainTypeEditReply reply = f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type() });
            Assert.Equal(AdminResult.AlreadyExists, reply.Result);
            Assert.True(Assert.Single(f.Audit.Rows).Rejected);
        }

        [Fact]
        public void UpdateAndDelete_OfAnUnknownId_AreNotFound_AndAnUpdateNeverCreates()
        {
            var f = new Fixture();
            string token = f.Creator();
            Assert.Equal(AdminResult.NotFound, f.Api.UpdateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type("demo.zz") }).Result);
            Assert.Equal(AdminResult.NotFound, f.Api.DeleteTerrainType(new TerrainTypeIdRequest { Token = token, Id = "demo.zz" }).Result);
            Assert.Equal(AdminResult.NotFound, f.Api.DeleteTerrainType(new TerrainTypeIdRequest { Token = token, Id = "" }).Result);
            Assert.False(f.Store.TerrainTypeTable.Items.ContainsKey("demo.zz"));
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void Delete_LosingARaceAfterTheAudit_IsNotFound_AndTheAuditRowIsRejected()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Store.TerrainTypeTable.Items["demo.swamp"] = new TerrainType { Id = "demo.swamp", Name = "Swamp" };
            f.Store.TerrainTypeTable.RaceOnWrite = true;
            Assert.Equal(AdminResult.NotFound, f.Api.DeleteTerrainType(new TerrainTypeIdRequest { Token = token, Id = "demo.swamp" }).Result);
            Assert.True(Assert.Single(f.Audit.Rows).Rejected);
        }

        // ---------------------------------------------------------------- Refused

        [Fact]
        public void Delete_IsRefusedWhileAFragmentAFeatureOrAPhraseIsScopedToIt_AndTheMessageNamesTheCounts()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type("demo.swamp") });
            f.Store.FragmentTable.Items["f1"] = new DescriptionFragment { Id = "f1", Terrain = "demo.swamp", Slot = "s", Text = "t" };
            f.Store.FragmentTable.Items["f2"] = new DescriptionFragment { Id = "f2", Terrain = "demo.swamp", Slot = "s", Text = "t" };
            f.Store.FeatureTable.Items["t1"] = new FeatureTemplate { Id = "t1", Terrain = "demo.swamp" };
            f.Store.PhraseTable.Items["p1"] = new NeighborPhrase { Id = "p1", Terrain = "demo.swamp", NeighborTerrain = "demo.grass", Text = "{dir}" };
            f.Audit.Rows.Clear();

            TerrainTypeEditReply reply = f.Api.DeleteTerrainType(new TerrainTypeIdRequest { Token = token, Id = "demo.swamp" });

            Assert.Equal(AdminResult.Refused, reply.Result);
            Assert.Contains("2 description fragments", reply.Message);
            Assert.Contains("1 feature template", reply.Message);
            Assert.Contains("1 neighbour phrase", reply.Message);
            Assert.True(f.Store.TerrainTypeTable.Items.ContainsKey("demo.swamp"));
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void Delete_IsRefusedWhileAPhraseNamesItAsNeighbourTerrain()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Store.TerrainTypeTable.Items["demo.river"] = new TerrainType { Id = "demo.river", Name = "River" };
            f.Store.PhraseTable.Items["p1"] = new NeighborPhrase { Id = "p1", Terrain = "demo.grass", NeighborTerrain = "demo.river", Text = "{dir}" };
            TerrainTypeEditReply reply = f.Api.DeleteTerrainType(new TerrainTypeIdRequest { Token = token, Id = "demo.river" });
            Assert.Equal(AdminResult.Refused, reply.Result);
            Assert.Contains("1 neighbour phrase (as neighbour terrain)", reply.Message);
            Assert.True(f.Store.TerrainTypeTable.Items.ContainsKey("demo.river"));
        }

        [Fact]
        public void TheInUseMessageHandlesEveryCombination()
        {
            Assert.Equal("This terrain type is still in use by 1 description fragment. Move or delete them first. Nothing was changed.", AdminApi.TerrainTypeInUseMessage(1, 0, 0, 0));
            Assert.Equal("This terrain type is still in use by 3 description fragments, 1 feature template, 2 neighbour phrases and 4 neighbour phrases (as neighbour terrain). Move or delete them first. Nothing was changed.", AdminApi.TerrainTypeInUseMessage(3, 1, 2, 4));
        }

        [Fact]
        public void WithoutALibraryStore_EveryWriteIsRefused_AndReadsAreEmpty()
        {
            var f = new Fixture(withLibrary: false);
            string token = f.Creator();
            Assert.Equal(AdminResult.Refused, f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type() }).Result);
            Assert.Equal(AdminResult.Refused, f.Api.UpdateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type() }).Result);
            TerrainTypeEditReply refused = f.Api.DeleteTerrainType(new TerrainTypeIdRequest { Token = token, Id = "demo.swamp" });
            Assert.Equal((AdminResult.Refused, AdminApi.TerrainTypeUnavailableMessage), (refused.Result, refused.Message));
            Assert.Empty(f.Api.ListTerrainTypes(new ListTerrainTypesRequest { Token = token }).TerrainTypes);
            Assert.False(f.Api.GetTerrainType(new TerrainTypeIdRequest { Token = token, Id = "demo.grass" }).Found);
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void WithoutAnAreaStore_ACreateIsRefused_BecauseTheAreaCannotBeChecked()
        {
            var f = new Fixture(withAreas: false);
            string token = f.Creator();
            Assert.Equal(AdminResult.Refused, f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type() }).Result);
            Assert.Empty(f.Audit.Rows);
        }

        // ---------------------------------------------------------------- roles, audit, failures

        [Fact]
        public void APlayerWhoIsNotACreator_IsForbidden_ForEveryCall_AndAnUnknownTokenIsUnauthenticated()
        {
            var f = new Fixture();
            string token = f.Login("Pat");
            Assert.Equal(AdminFailure.Forbidden, Throws(() => f.Api.ListTerrainTypes(new ListTerrainTypesRequest { Token = token })).Failure);
            Assert.Equal(AdminFailure.Forbidden, Throws(() => f.Api.GetTerrainType(new TerrainTypeIdRequest { Token = token, Id = "demo.grass" })).Failure);
            Assert.Equal(AdminFailure.Forbidden, Throws(() => f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type() })).Failure);
            Assert.Equal(AdminFailure.Forbidden, Throws(() => f.Api.UpdateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type() })).Failure);
            Assert.Equal(AdminFailure.Forbidden, Throws(() => f.Api.DeleteTerrainType(new TerrainTypeIdRequest { Token = token, Id = "demo.grass" })).Failure);
            Assert.Equal(AdminFailure.Unauthenticated, Throws(() => f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = "nope", TerrainType = Type() })).Failure);
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void WhenTheAuditCannotBeWritten_TheEditIsRefusedAsAuditFailed_AndNothingChanges()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Audit.FailInsert = true;
            Assert.Equal(AdminResult.AuditFailed, f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type() }).Result);
            Assert.DoesNotContain("demo.swamp", f.Store.TerrainTypeTable.Items.Keys);
            f.Store.TerrainTypeTable.Items["demo.old"] = new TerrainType { Id = "demo.old", Name = "Old" };
            Assert.Equal(AdminResult.AuditFailed, f.Api.DeleteTerrainType(new TerrainTypeIdRequest { Token = token, Id = "demo.old" }).Result);
            Assert.True(f.Store.TerrainTypeTable.Items.ContainsKey("demo.old"));
        }

        [Fact]
        public void WhenTheDatabaseWriteFails_TheResultIsFailed_TheAuditRowIsRejected_AndNoValuesAreLogged()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Store.TerrainTypeTable.FailWrites = true;
            TerrainTypeEditReply reply = f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type("demo.secret") });
            Assert.Equal((AdminResult.Failed, AdminApi.TerrainTypeWriteFailedMessage), (reply.Result, reply.Message));
            Assert.True(Assert.Single(f.Audit.Rows).Rejected);
            Assert.DoesNotContain(f.Log, l => l.Contains("db down"));
        }

        [Fact]
        public void WhenTheDatabaseCannotBeRead_AListIsUnavailable_AndAWriteFails()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Store.TerrainTypeTable.FailReads = true;
            Assert.Equal(AdminFailure.Unavailable, Throws(() => f.Api.ListTerrainTypes(new ListTerrainTypesRequest { Token = token })).Failure);
            Assert.Equal(AdminFailure.Unavailable, Throws(() => f.Api.GetTerrainType(new TerrainTypeIdRequest { Token = token, Id = "demo.grass" })).Failure);
            Assert.Equal(AdminResult.Failed, f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type() }).Result);
            Assert.Equal(AdminResult.Failed, f.Api.DeleteTerrainType(new TerrainTypeIdRequest { Token = token, Id = "demo.grass" }).Result);
        }

        [Fact]
        public void WhenTheGameThreadIsNotAvailable_WritesAreUnavailable_AndNothingIsAudited()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Host.Available = false;
            Assert.Equal(AdminFailure.Unavailable, Throws(() => f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type() })).Failure);
            Assert.Equal(AdminFailure.Unavailable, Throws(() => f.Api.DeleteTerrainType(new TerrainTypeIdRequest { Token = token, Id = "demo.grass" })).Failure);
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void TheTerrainTypeCallsAreMappedToGrpcStatusesByTheService()
        {
            var f = new Fixture();
            var service = new AdminService(f.Api);
            var ex = Assert.ThrowsAsync<Grpc.Core.RpcException>(() => service.CreateTerrainType(new SaveTerrainTypeRequest { Token = "nope", TerrainType = Type() }, null));
            Assert.Equal(Grpc.Core.StatusCode.Unauthenticated, ex.GetAwaiter().GetResult().StatusCode);
            var list = Assert.ThrowsAsync<Grpc.Core.RpcException>(() => service.ListTerrainTypes(new ListTerrainTypesRequest { Token = "nope" }, null));
            Assert.Equal(Grpc.Core.StatusCode.Unauthenticated, list.GetAwaiter().GetResult().StatusCode);
        }

        // ---------------------------------------------------------------- area delete counts terrain types

        [Fact]
        public void DeleteArea_IsRefusedWhileTerrainTypesStillBelongToIt_AndTheMessageNamesTheCount()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type("other.a") });
            f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type("other.b") });
            f.Audit.Rows.Clear();

            AreaEditReply reply = f.Api.DeleteArea(new AreaIdRequest { Token = token, Id = "other" });

            Assert.Equal(AdminResult.Refused, reply.Result);
            Assert.Equal("2 terrain types still use this area. Move or delete them first. Nothing was changed.", reply.Message);
            Assert.True(f.Areas.Items.ContainsKey("other"));
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void DeleteArea_IsAllowedAgainOnceItsTerrainTypesAreGone()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Api.CreateTerrainType(new SaveTerrainTypeRequest { Token = token, TerrainType = Type("other.a") });
            Assert.Equal(AdminResult.Refused, f.Api.DeleteArea(new AreaIdRequest { Token = token, Id = "other" }).Result);
            f.Store.TerrainTypeTable.Items.Remove("other.a");
            f.Store.TerrainTypeTable.Items.Remove("other.grass");   // the fixture's own type of that area
            Assert.Equal(AdminResult.Ok, f.Api.DeleteArea(new AreaIdRequest { Token = token, Id = "other" }).Result);
        }

        [Theory]
        [InlineData(1, 0, "1 room still uses this area. Move or delete them first. Nothing was changed.")]
        [InlineData(3, 0, "3 rooms still use this area. Move or delete them first. Nothing was changed.")]
        [InlineData(0, 1, "1 terrain type still uses this area. Move or delete them first. Nothing was changed.")]
        [InlineData(0, 4, "4 terrain types still use this area. Move or delete them first. Nothing was changed.")]
        [InlineData(1, 4, "1 room and 4 terrain types still use this area. Move or delete them first. Nothing was changed.")]
        public void TheAreaInUseMessage(int rooms, int types, string expected)
        {
            Assert.Equal(expected, AdminApi.AreaInUseMessage(rooms, types));
        }
    }
}
