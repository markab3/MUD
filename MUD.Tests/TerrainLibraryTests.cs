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
    // Card t_e62bb6f3: terrain library store, seed loader and the Admin library calls. No database: an in-memory ILibraryStore and fakes.
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
        public int? Version;

        public ILibraryCollection<DescriptionFragment> Fragments { get { return FragmentTable; } }
        public ILibraryCollection<FeatureTemplate> Features { get { return FeatureTable; } }
        public ILibraryCollection<NeighborPhrase> Phrases { get { return PhraseTable; } }
        public int? GetVersion() { return Version; }
        public void InitMeta(int version) { if (!Version.HasValue) { Version = version; } }
        public int BumpVersion() { if (!Version.HasValue) { throw new InvalidOperationException("no meta"); } Version = Version.Value + 1; return Version.Value; }
    }

    public class TerrainLibrarySeedTests
    {
        private const string Good = @"
schema_version: 1
library_version: 4
fragments:
  - { id: g1, terrains: [grass], slot: short, text: ""a meadow"", weight: 2, tags: [dry] }
  - { id: unused, terrains: [], slot: short, text: ""nobody uses me"" }
features:
  - { id: b1, terrains: [grass], short: ""a boulder"", long: ""A boulder."", handles: [boulder], weight: 2, chance: 0.5, version: 3 }
neighborPhrases:
  - { id: p1, terrains: [grass], neighborTerrain: river, text: ""A river flows just {dir} of here."", merge: false }
";

        private static List<string> ParseErrors(string yaml)
        {
            var errors = new List<string>();
            LibrarySeed.Parse(yaml, errors, new List<string>());
            return errors;
        }

        [Fact]
        public void AValidFileParsesWithAllFieldsAndKeepsAnEmptyTerrainsListEmpty()
        {
            var errors = new List<string>();
            LibrarySeedData data = LibrarySeed.Parse(Good, errors, new List<string>());
            Assert.Empty(errors);
            Assert.Equal(4, data.LibraryVersion);
            Assert.Equal(2, data.Fragments.Count);
            Assert.Equal(2, data.Fragments[0].Weight);
            Assert.Equal(new[] { "dry" }, data.Fragments[0].Tags);
            Assert.Empty(data.Fragments[1].Terrains);   // OD5: empty stays empty, it is not "any terrain"
            Assert.Equal((3, 0.5, 2), (data.Features[0].Version, data.Features[0].Chance, data.Features[0].Weight));
            Assert.False(data.Phrases[0].Merge);
        }

        [Fact]
        public void TheEmbeddedSeedFileIsPresentAndValid()
        {
            string yaml = LibrarySeed.ReadEmbedded();
            Assert.NotNull(yaml);
            var errors = new List<string>();
            LibrarySeedData data = LibrarySeed.Parse(yaml, errors, new List<string>());
            Assert.Empty(errors);
            Assert.NotEmpty(data.Fragments);
            Assert.NotEmpty(data.Features);
            Assert.NotEmpty(data.Phrases);
            // Every seeded entity is assigned to a terrain: the shipped library has nothing disabled.
            Assert.All(data.Fragments, f => Assert.NotEmpty(f.Terrains));
            Assert.All(data.Features, f => Assert.NotEmpty(f.Terrains));
            Assert.All(data.Phrases, f => Assert.NotEmpty(f.Terrains));
        }

        [Fact]
        public void TheEmbeddedSeedGivesEveryTerrainItWorksOnAShortFragment()
        {
            var data = LibrarySeed.Parse(LibrarySeed.ReadEmbedded(), new List<string>(), new List<string>());
            foreach (string terrain in data.Fragments.SelectMany(f => f.Terrains).Distinct())
            {
                Assert.Contains(data.Fragments, f => f.Slot == "short" && f.Terrains.Contains(terrain));
            }
        }

        [Fact]
        public void AFreshStoreIsSeededAndTheMetaDocumentIsCreatedLast()
        {
            var store = new FakeLibraryStore();
            LibrarySeedResult result = LibrarySeed.SeedIfFresh(store, Good);
            Assert.True(result.Seeded);
            Assert.Empty(result.Errors);
            Assert.Equal(4, store.Version);
            Assert.Equal((2, 1, 1), (store.FragmentTable.Items.Count, store.FeatureTable.Items.Count, store.PhraseTable.Items.Count));
            Assert.Equal((2, 1, 1), (result.Fragments, result.Features, result.Phrases));
        }

        [Fact]
        public void ASecondStartDoesNotReseedAndAdminEditsSurvive()
        {
            var store = new FakeLibraryStore();
            LibrarySeed.SeedIfFresh(store, Good);
            store.FragmentTable.Items.Remove("g1");                  // an admin deleted it
            store.FragmentTable.Items["extra"] = new DescriptionFragment { Id = "extra" };

            LibrarySeedResult second = LibrarySeed.SeedIfFresh(store, Good);

            Assert.False(second.Seeded);
            Assert.True(second.AlreadySeeded);
            Assert.False(store.FragmentTable.Items.ContainsKey("g1"));
            Assert.True(store.FragmentTable.Items.ContainsKey("extra"));
        }

        [Fact]
        public void AnInvalidSeedWritesNothingAndLeavesTheStoreFresh()
        {
            var store = new FakeLibraryStore();
            LibrarySeedResult result = LibrarySeed.SeedIfFresh(store, Good.Replace("{dir}", "somewhere"));
            Assert.False(result.Seeded);
            Assert.NotEmpty(result.Errors);
            Assert.Null(store.Version);
            Assert.Empty(store.FragmentTable.Items);
            Assert.Empty(store.FeatureTable.Items);
        }

        [Fact]
        public void AMissingSeedIsReportedAndNothingIsWritten()
        {
            var store = new FakeLibraryStore();
            LibrarySeedResult result = LibrarySeed.SeedIfFresh(store, null);
            Assert.False(result.Seeded);
            Assert.Single(result.Errors);
            Assert.Null(store.Version);
        }

        [Theory]
        [InlineData("schema_version: 2\n", "schema_version")]
        [InlineData("schema_version: 1\nbogus: 1\n", "Unknown top-level key")]
        [InlineData("schema_version: 1\nfragments:\n  - { id: a, terrains: [t], slot: s, text: x, colour: red }\n", "Unknown fragment key")]
        [InlineData("schema_version: 1\nfragments:\n  - { id: a, terrains: [t], slot: s, text: x }\n  - { id: a, terrains: [t], slot: s, text: y }\n", "Duplicate fragment id")]
        [InlineData("schema_version: 1\nfragments:\n  - { id: a, terrains: [t], slot: s, text: x, weight: -1 }\n", "weight")]
        [InlineData("schema_version: 1\nfragments:\n  - { id: a, terrains: [t], slot: s, text: x, weight: many }\n", "weight must be an integer")]
        [InlineData("schema_version: 1\nfragments:\n  - { id: a, terrains: [\"\"], slot: s, text: x }\n", "non-empty")]
        [InlineData("schema_version: 1\nfeatures:\n  - { id: a, terrains: [t], short: s, long: l, chance: 1.5 }\n", "chance")]
        [InlineData("schema_version: 1\nfeatures:\n  - { id: a, terrains: [t], short: s, long: l, version: 0 }\n", "version")]
        [InlineData("schema_version: 1\nfeatures:\n  - { id: a, terrains: [t], long: l }\n", "short")]
        [InlineData("schema_version: 1\nneighborPhrases:\n  - { id: a, terrains: [t], neighborTerrain: river, text: \"no placeholder\" }\n", "{dir}")]
        [InlineData("schema_version: 1\nneighborPhrases:\n  - { id: a, terrains: [t], neighborTerrain: river, text: \"{dir} and {dir}\" }\n", "{dir}")]
        [InlineData("schema_version: 1\nneighborPhrases:\n  - { id: a, terrains: [t], text: \"x {dir}\" }\n", "neighbour terrain")]
        [InlineData("schema_version: 1\nfragments: [oops\n", "not valid YAML")]
        public void EachInvalidCaseIsRejected(string yaml, string expected)
        {
            List<string> errors = ParseErrors(yaml);
            Assert.Contains(errors, e => e.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void XKeysAreAllowedAndUnknownTerrainsAreNotRejected()
        {
            Assert.Empty(ParseErrors("schema_version: 1\nx-note: hi\nfragments:\n  - { id: a, terrains: [not-registered-yet], slot: s, text: x, x-why: y }\n"));
        }

        [Fact]
        public void AStoreFailureWhileSeedingPropagatesAndLeavesNoMetaDocument()
        {
            var store = new FakeLibraryStore();
            store.FragmentTable.FailWrites = true;
            Assert.Throws<InvalidOperationException>(() => LibrarySeed.SeedIfFresh(store, Good));
            Assert.Null(store.Version);   // so the next start tries again
        }

        [Fact]
        public void ARetryAfterAHalfFinishedSeedDoesNotDuplicateOrFail()
        {
            var store = new FakeLibraryStore();
            store.FragmentTable.Items["g1"] = new DescriptionFragment { Id = "g1", Text = "left over" };   // inserted before a crash, no meta yet
            LibrarySeedResult result = LibrarySeed.SeedIfFresh(store, Good);
            Assert.True(result.Seeded);
            Assert.Equal(2, store.FragmentTable.Items.Count);
        }
    }

    public class TerrainStoreLibraryTests
    {
        [Fact]
        public void ReturnsEntitiesOfThatTerrainOnlyAndNeverAnEmptyTerrainsEntity()
        {
            var store = new FakeLibraryStore { Version = 5 };
            store.FragmentTable.Items["a"] = new DescriptionFragment { Id = "a", Terrains = new List<string> { "grass" }, Slot = "short" };
            store.FragmentTable.Items["b"] = new DescriptionFragment { Id = "b", Terrains = new List<string>(), Slot = "short" };
            store.FeatureTable.Items["f"] = new FeatureTemplate { Id = "f", Terrains = new List<string>() };
            store.PhraseTable.Items["p"] = new NeighborPhrase { Id = "p", Terrains = new List<string>(), NeighborTerrain = "river", Text = "{dir}" };
            var library = new StoreLibrary(store);

            Assert.Equal(5, library.Version);
            Assert.Equal(new[] { "a" }, library.Fragments("grass", "short").Select(f => f.Id));
            Assert.Empty(library.Fragments("forest", "short"));
            Assert.Empty(library.Features("grass"));
            Assert.Empty(library.Phrases("grass", "river"));
            Assert.Null(library.FeatureById("nope"));
            Assert.NotNull(library.FeatureById("f"));
        }

        [Fact]
        public void ReadsOneSnapshotUntilRefreshed()
        {
            var store = new FakeLibraryStore { Version = 1 };
            store.FragmentTable.Items["a"] = new DescriptionFragment { Id = "a", Terrains = new List<string> { "grass" }, Slot = "short" };
            var library = new StoreLibrary(store);
            Assert.Single(library.Fragments("grass", "short"));

            store.FragmentTable.Items["b"] = new DescriptionFragment { Id = "b", Terrains = new List<string> { "grass" }, Slot = "short" };
            store.Version = 2;
            Assert.Single(library.Fragments("grass", "short"));   // same run, same snapshot
            Assert.Equal(1, library.Version);

            library.Refresh();
            Assert.Equal(2, library.Fragments("grass", "short").Count);
            Assert.Equal(2, library.Version);
        }

        [Fact]
        public void ARealMapRunUsesTheLibraryFromTheStore()
        {
            var store = new FakeLibraryStore();
            var seeded = LibrarySeed.SeedIfFresh(store, LibrarySeed.ReadEmbedded());
            Assert.True(seeded.Seeded, string.Join("; ", seeded.Errors));
            Assert.Equal(3, store.PhraseTable.Items.Count);
            var library = new StoreLibrary(store);
            string map = "generator:\n  prefix: demo\n  name: Demo\n  seed: s1\ngrid:\n  - \"~~\"\n  - \"..\"\nlegend:\n  \"~\": {template: river}\n  \".\": grass\ntemplates:\n  river: {walkable: false}\n  grass: {short: a meadow, long: [[Grass grows here.]]}\n";
            var generated = MUD.Core.Generation.MapGenerator.Generate(map, "demo", library);
            Assert.True(generated.Success, string.Join("; ", generated.Errors));
            Assert.Contains("libVersion: 1", generated.Yaml);   // library_version of the seed file
            string flat = generated.Yaml.Replace("\r", "").Replace("\n    ", " ");
            Assert.True(flat.Contains("A river flows just north and northeast of here.") || flat.Contains("You hear running water to the north and northeast."), flat);   // a seeded river phrase
        }
    }

    public class TerrainDocumentTests
    {
        [Fact]
        public void EntitiesRoundTripThroughTheirDocuments()
        {
            var feature = new FeatureTemplate
            {
                Id = "b", Terrains = new List<string> { "grass", "forest" }, Short = "s", Long = "l", Handles = new List<string> { "h" }, Adjectives = new List<string> { "a" },
                RoomLine = "r", Detail = "d", Weight = 3, Chance = 0.25, Version = 4, Tags = new List<string> { "t" },
            };
            BsonDocument document = LibraryDocuments.ToDocument(feature);
            Assert.Equal("b", document["_id"].AsString);
            FeatureTemplate back = LibraryDocuments.FeatureFrom(document);
            Assert.Equal(feature.Terrains, back.Terrains);
            Assert.Equal((feature.Short, feature.Long, feature.RoomLine, feature.Detail, feature.Weight, feature.Chance, feature.Version),
                (back.Short, back.Long, back.RoomLine, back.Detail, back.Weight, back.Chance, back.Version));

            var fragment = new DescriptionFragment { Id = "f", Terrains = new List<string>(), Slot = "ground", Text = "x", Weight = 0, Tags = new List<string> { "wet" } };
            DescriptionFragment fragmentBack = LibraryDocuments.FragmentFrom(LibraryDocuments.ToDocument(fragment));
            Assert.Empty(fragmentBack.Terrains);
            Assert.Equal(0, fragmentBack.Weight);

            var phrase = new NeighborPhrase { Id = "p", Terrains = new List<string> { "grass" }, NeighborTerrain = "river", Text = "{dir}", Weight = 2, Merge = false };
            NeighborPhrase phraseBack = LibraryDocuments.PhraseFrom(LibraryDocuments.ToDocument(phrase));
            Assert.False(phraseBack.Merge);
            Assert.Equal("river", phraseBack.NeighborTerrain);
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
            public StoreLibrary Cache;
            public List<string> Log = new List<string>();
            public AdminApi Api;

            public Fixture(bool withLibrary = true)
            {
                Cache = new StoreLibrary(Store);
                Api = new AdminApi(Host, Players, new SyncRoomRepo(), new NoQueries(), Audit, Sessions, new FakeClock(), Log.Add, max => 0, null,
                    withLibrary ? Store : null, withLibrary ? Cache : null);
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

        private static FragmentRecord Fragment(string id = "f1", params string[] terrains)
        {
            var r = new FragmentRecord { Id = id, Slot = "ground", Text = "Grass.", Weight = 1 };
            r.Terrains.AddRange(terrains);
            return r;
        }

        private static FeatureTemplateRecord Template(string id = "t1", params string[] terrains)
        {
            var r = new FeatureTemplateRecord { Id = id, Short = "a rock", Long = "A rock.", Weight = 1, Chance = 0.5, Version = 99 };
            r.Terrains.AddRange(terrains);
            r.Handles.Add("rock");
            return r;
        }

        private static NeighborPhraseRecord Phrase(string id = "p1", params string[] terrains)
        {
            var r = new NeighborPhraseRecord { Id = id, NeighborTerrain = "river", Text = "A river flows just {dir} of here.", Weight = 1, Merge = true };
            r.Terrains.AddRange(terrains);
            return r;
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
            Assert.Equal(new[] { "grass" }, f.Store.FragmentTable.Items["f1"].Terrains);
            AuditRecord row = Assert.Single(f.Audit.Rows);
            Assert.Equal((AdminApi.FragmentEntity, "f1", "*create*", true), (row.Entity, row.EntityId, row.Field, row.Applied));
            Assert.Equal("Cora", row.ActorName);
            Assert.Single(f.Cache.Fragments("grass", "ground"));   // generators see it without a restart
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

            var edit = Fragment("f1", "grass", "forest");
            edit.Text = "New text.";
            LibraryEditReply reply = f.Api.UpdateFragment(new SaveFragmentRequest { Token = token, Fragment = edit });

            Assert.Equal(AdminResult.Ok, reply.Result);
            Assert.Equal(new[] { "terrains", "text" }, f.Audit.Rows.Select(r => r.Field).OrderBy(x => x));
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

            ListFragmentsReply list = f.Api.ListFragments(new TokenRequest { Token = token });
            Assert.Equal(new[] { "a", "b" }, list.Fragments.Select(x => x.Id));
            Assert.Equal(12, list.LibraryVersion);
            Assert.True(f.Api.GetFragment(new LibraryIdRequest { Token = token, Id = "a" }).Found);
            Assert.False(f.Api.GetFragment(new LibraryIdRequest { Token = token, Id = "zzz" }).Found);
            Assert.Empty(f.Api.ListFeatureTemplates(new TokenRequest { Token = token }).Templates);
            Assert.False(f.Api.GetNeighborPhrase(new LibraryIdRequest { Token = token, Id = "x" }).Found);
        }

        // ---------------------------------------------------------------- empty terrains (OD5)

        [Fact]
        public void AnEntityWithAnEmptyTerrainsListIsAcceptedStoredAndNeverSelectedByAGenerator()
        {
            var f = new Fixture();
            string token = f.Creator();
            Assert.Equal(AdminResult.Ok, f.Api.CreateFragment(new SaveFragmentRequest { Token = token, Fragment = Fragment("off") }).Result);
            Assert.Equal(AdminResult.Ok, f.Api.CreateFeatureTemplate(new SaveFeatureTemplateRequest { Token = token, Template = Template("off") }).Result);
            Assert.Equal(AdminResult.Ok, f.Api.CreateNeighborPhrase(new SaveNeighborPhraseRequest { Token = token, Phrase = Phrase("off") }).Result);

            Assert.Empty(f.Api.GetFragment(new LibraryIdRequest { Token = token, Id = "off" }).Fragment.Terrains);
            foreach (string terrain in new[] { "grass", "forest", "river", "" })
            {
                Assert.Empty(f.Cache.Fragments(terrain, "ground"));
                Assert.Empty(f.Cache.Features(terrain));
                Assert.Empty(f.Cache.Phrases(terrain, "river"));
            }
        }

        // ---------------------------------------------------------------- feature template version

        [Fact]
        public void TemplateEdits_BumpTheVersion_OnlyWhenTheTextOrHandlesChange_AndTheClientVersionIsIgnored()
        {
            var f = new Fixture();
            string token = f.Creator();
            LibraryEditReply created = f.Api.CreateFeatureTemplate(new SaveFeatureTemplateRequest { Token = token, Template = Template("t1", "grass") });
            Assert.Equal(1, created.Template.Version);   // the client sent 99

            var weightOnly = Template("t1", "grass", "forest");
            weightOnly.Weight = 7; weightOnly.Chance = 0.9; weightOnly.Tags.Add("x"); weightOnly.Version = 50;
            Assert.Equal(1, f.Api.UpdateFeatureTemplate(new SaveFeatureTemplateRequest { Token = token, Template = weightOnly }).Template.Version);

            var textEdit = Template("t1", "grass", "forest");
            textEdit.Long = "A different rock."; textEdit.Weight = 7; textEdit.Chance = 0.9; textEdit.Tags.Add("x");
            Assert.Equal(2, f.Api.UpdateFeatureTemplate(new SaveFeatureTemplateRequest { Token = token, Template = textEdit }).Template.Version);

            var handlesEdit = Template("t1", "grass", "forest");
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
            Assert.Equal(new[] { "grass" }, f.Store.FragmentTable.Items["f1"].Terrains);
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
            f.Store.PhraseTable.Items["p1"] = new NeighborPhrase { Id = "p1", Terrains = new List<string> { "grass" } };
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
            Assert.Empty(f.Api.ListFragments(new TokenRequest { Token = token }).Fragments);
            Assert.False(f.Api.GetFeatureTemplate(new LibraryIdRequest { Token = token, Id = "x" }).Found);
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void APlayerWhoIsNotACreator_IsForbidden_ForEveryCall_AndNothingIsAudited()
        {
            var f = new Fixture();
            string token = f.Login("Pat");
            Assert.Equal(AdminFailure.Forbidden, Throws(() => f.Api.ListFragments(new TokenRequest { Token = token })).Failure);
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
            Assert.Equal(AdminFailure.Unauthenticated, Throws(() => f.Api.ListNeighborPhrases(new TokenRequest { Token = "" })).Failure);
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

            Assert.Equal(AdminFailure.Unavailable, Throws(() => f.Api.ListFragments(new TokenRequest { Token = token })).Failure);
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
