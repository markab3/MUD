using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MUD.Contracts;
using MUD.Core.Administration;
using MUD.Core.GameObjects;
using MUD.Core.NpcTemplates;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;
using Xunit;

namespace MUD.Tests
{
    // NPC templates (collection "npc_template"): rules, documents and the Admin calls. No database: an in-memory INpcTemplateStore.
    // MongoNpcTemplateStore was NOT run here (no MongoDB in the sandbox); see specs/ADMIN_API.md "Not verified here".
    public class FakeNpcTemplateStore : INpcTemplateStore
    {
        public Dictionary<string, NpcTemplateEntry> Items = new Dictionary<string, NpcTemplateEntry>();
        public bool FailReads;
        public bool FailWrites;
        public bool RaceOnWrite;

        public IReadOnlyList<NpcTemplateEntry> List(string area) { Check(false); return Items.Values.Where(i => string.IsNullOrEmpty(area) || i.Area == area).OrderBy(i => i.Id, StringComparer.Ordinal).ToList(); }
        public NpcTemplateEntry Get(string id) { Check(false); return Items.TryGetValue(id, out var v) ? v : null; }
        public bool Insert(NpcTemplateEntry e, string by) { Check(true); if (RaceOnWrite || Items.ContainsKey(e.Id)) { return false; } Items[e.Id] = e; return true; }
        public bool Replace(NpcTemplateEntry e, string by) { Check(true); if (RaceOnWrite || !Items.ContainsKey(e.Id)) { return false; } Items[e.Id] = e; return true; }
        public bool Delete(string id) { Check(true); return !RaceOnWrite && Items.Remove(id); }
        public int CountInArea(string area) { Check(false); return Items.Values.Count(i => i.Area == area); }
        private void Check(bool write) { if (write ? FailWrites : FailReads) { throw new InvalidOperationException("db down"); } }
    }

    public class NpcTemplateRulesTests
    {
        [Theory]
        [InlineData("t.guard", "t")]
        [InlineData("a.b.guard_2", "a.b")]
        public void CheckId_AcceptsAreaDotLocal(string id, string area) { Assert.Null(NpcTemplateRules.CheckId(id, area)); }

        [Theory]
        [InlineData("t.guard", "")]
        [InlineData("u.guard", "t")]
        [InlineData("tguard", "t")]
        [InlineData("t.Guard", "t")]
        [InlineData("t.", "t")]
        [InlineData("t.bad key", "t")]
        public void CheckId_RefusesBadIds(string id, string area) { Assert.NotNull(NpcTemplateRules.CheckId(id, area)); }

        [Theory]
        [InlineData(0, true)]
        [InlineData(1000, true)]
        [InlineData(-1, false)]
        [InlineData(1001, false)]
        public void Vitals_AreZeroToOneThousand(int value, bool ok) { Assert.Equal(ok, NpcTemplateRules.CheckVital("x", value) == null); }

        [Theory]
        [InlineData(0, true)]
        [InlineData(255, true)]
        [InlineData(-1, false)]
        [InlineData(256, false)]
        public void AbilityScores_AreZeroTo255(int value, bool ok) { Assert.Equal(ok, NpcTemplateRules.CheckAbility("x", value) == null); }

        [Fact]
        public void Experience_IsZeroOrMore_AndNotSetIsFine()
        {
            Assert.Null(NpcTemplateRules.CheckExperience(null));
            Assert.Null(NpcTemplateRules.CheckExperience(0));
            Assert.Null(NpcTemplateRules.CheckExperience(5_000_000_000));
            Assert.NotNull(NpcTemplateRules.CheckExperience(-1));
            Assert.Null(NpcTemplateRules.CheckVital("x", null));
            Assert.Null(NpcTemplateRules.CheckAbility("x", null));
        }

        [Fact]
        public void TheBoundsAndDefaultsMatchLiving()
        {
            Assert.Equal((int)Living.VitalScale, NpcTemplateRules.MaxVital);
            Assert.Equal((int)Living.VitalScale, NpcTemplateRules.DefaultVital);
            Assert.Equal((int)Living.DefaultAbilityScore, NpcTemplateRules.DefaultAbility);
            Assert.Equal((int)byte.MaxValue, NpcTemplateRules.MaxAbility);
        }

        [Fact]
        public void TheDocumentRoundTrips_StoresOnlyStatsThatAreSet_AndToleratesBadFields()
        {
            var entry = new NpcTemplateEntry
            {
                Id = "t.guard", Area = "t", Short = "a guard", Long = "Stern.", Handles = { "guard" }, Adjectives = { "stern" }, Plurals = { "guards" },
                MaxHealth = 500, Experience = 5_000_000_000, Strength = 18, Charisma = 0, Version = 4,
            };
            BsonDocument doc = NpcTemplateDocuments.ToDocument(entry);
            Assert.False(doc.Contains("maxStamina"));
            Assert.False(doc.Contains("wisdom"));
            Assert.True(doc.Contains("charisma"));   // 0 is a value, not "not set"
            NpcTemplateEntry back = NpcTemplateDocuments.From(doc);
            Assert.Equal(("t.guard", "t", "a guard", 4), (back.Id, back.Area, back.Short, back.Version));
            Assert.Equal((500, null, 5_000_000_000, 18, 0), (back.MaxHealth, back.MaxStamina, back.Experience, back.Strength, back.Charisma));
            Assert.Null(back.Wisdom);
            Assert.Equal(new[] { "guard" }, back.Handles);

            NpcTemplateEntry bare = NpcTemplateDocuments.From(new BsonDocument { { "_id", "x.y" }, { "handles", BsonNull.Value }, { "plurals", "oops" }, { "adjectives", new BsonArray { 1, "ok" } }, { "maxHealth", "z" }, { "version", "z" } });
            Assert.Equal((string.Empty, string.Empty, string.Empty, 1), (bare.Area, bare.Short, bare.Long, bare.Version));
            Assert.Empty(bare.Handles); Assert.Empty(bare.Plurals);
            Assert.Equal(new[] { "ok" }, bare.Adjectives);
            Assert.Null(bare.MaxHealth);

            BsonDocument written = NpcTemplateDocuments.ToDocument(new NpcTemplateEntry { Id = "a.b", Handles = null, Adjectives = null, Plurals = null });
            Assert.Equal(0, written["handles"].AsBsonArray.Count);
            Assert.Equal("npc_template", NpcTemplateDocuments.Collection);
        }
    }

    public class AdminNpcTemplateApiTests
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
            public List<BsonDocument> ListPlayers(string nameFilter, bool descending, int page, int pageSize, out int total) { total = 0; return new List<BsonDocument>(); }
            public BsonDocument FindPlayer(string nameKey) { return null; }
            public int CountAdmins() { return 0; }
            public List<BsonDocument> RoomsWithTemplateReward(string templateId) { return new List<BsonDocument>(); }
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
            public SyncRoomRepo Rooms = new SyncRoomRepo();
            public AdminSessions Sessions = new AdminSessions();
            public FakeAreaStore Areas = new FakeAreaStore().With("t", "Test land", "A test region.").With("u");
            public FakeNpcTemplateStore Npcs = new FakeNpcTemplateStore();
            public List<string> Log = new List<string>();
            public AdminApi Api;

            public Fixture(bool withStore = true, bool withAreas = true)
            {
                Api = new AdminApi(Host, Players, Rooms, new Queries(), Audit, Sessions, new FakeClock(), Log.Add, max => 0, null, null, null, null,
                    withAreas ? Areas : null, null, withStore ? Npcs : null);
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

        private static NpcTemplateRecord Record(string id = "t.guard", string area = "t", string shortText = "a guard", string longText = "A stern guard.")
        {
            var r = new NpcTemplateRecord { Id = id, Area = area, Short = shortText, Long = longText };
            r.Handles.Add("guard");
            return r;
        }

        private static SaveNpcTemplateRequest Save(string token, NpcTemplateRecord record) { return new SaveNpcTemplateRequest { Token = token, Template = record }; }

        private static NpcTemplateEntry Entry(string id, string area, string shortText = "s") { return new NpcTemplateEntry { Id = id, Area = area, Short = shortText, Long = "l" }; }

        // ---------------------------------------------------------------- roles and tokens

        [Fact]
        public void EveryCall_NeedsAGoodToken_AndACreatorOrAdmin()
        {
            var f = new Fixture();
            string player = f.Login("Pete", player: true);
            string admin = f.Login("Ada", admin: true);
            foreach (string token in new[] { "", "nope" })
            {
                Assert.Equal(AdminFailure.Unauthenticated, Assert.Throws<AdminCallException>(() => f.Api.ListNpcTemplates(new ListNpcTemplatesRequest { Token = token })).Failure);
                Assert.Equal(AdminFailure.Unauthenticated, Assert.Throws<AdminCallException>(() => f.Api.GetNpcTemplate(new NpcTemplateIdRequest { Token = token, Id = "t.a" })).Failure);
                Assert.Equal(AdminFailure.Unauthenticated, Assert.Throws<AdminCallException>(() => f.Api.CreateNpcTemplate(Save(token, Record()))).Failure);
                Assert.Equal(AdminFailure.Unauthenticated, Assert.Throws<AdminCallException>(() => f.Api.UpdateNpcTemplate(Save(token, Record()))).Failure);
                Assert.Equal(AdminFailure.Unauthenticated, Assert.Throws<AdminCallException>(() => f.Api.DeleteNpcTemplate(new NpcTemplateIdRequest { Token = token, Id = "t.a" })).Failure);
            }
            Assert.Equal(AdminFailure.Forbidden, Assert.Throws<AdminCallException>(() => f.Api.ListNpcTemplates(new ListNpcTemplatesRequest { Token = player })).Failure);
            Assert.Equal(AdminFailure.Forbidden, Assert.Throws<AdminCallException>(() => f.Api.GetNpcTemplate(new NpcTemplateIdRequest { Token = player, Id = "t.a" })).Failure);
            Assert.Equal(AdminFailure.Forbidden, Assert.Throws<AdminCallException>(() => f.Api.CreateNpcTemplate(Save(player, Record()))).Failure);
            Assert.Equal(AdminFailure.Forbidden, Assert.Throws<AdminCallException>(() => f.Api.UpdateNpcTemplate(Save(player, Record()))).Failure);
            Assert.Equal(AdminFailure.Forbidden, Assert.Throws<AdminCallException>(() => f.Api.DeleteNpcTemplate(new NpcTemplateIdRequest { Token = player, Id = "t.a" })).Failure);
            Assert.Empty(f.Npcs.Items);
            Assert.Empty(f.Audit.Rows);
            Assert.Equal(AdminResult.Ok, f.Api.CreateNpcTemplate(Save(admin, Record())).Result);   // an admin may do everything a creator may
        }

        [Fact]
        public void AWriteWhenTheGameThreadIsBusy_IsUnavailable()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Host.Available = false;
            Assert.Equal(AdminFailure.Unavailable, Assert.Throws<AdminCallException>(() => f.Api.CreateNpcTemplate(Save(token, Record()))).Failure);
        }

        // ---------------------------------------------------------------- reads

        [Fact]
        public void List_IsSortedById_AndFiltersByArea_AndTrimsTheFilter()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Npcs.Items["u.b"] = Entry("u.b", "u"); f.Npcs.Items["t.z"] = Entry("t.z", "t"); f.Npcs.Items["t.a"] = Entry("t.a", "t");
            Assert.Equal(new[] { "t.a", "t.z", "u.b" }, f.Api.ListNpcTemplates(new ListNpcTemplatesRequest { Token = token }).Templates.Select(t => t.Id));
            Assert.Equal(new[] { "t.a", "t.z" }, f.Api.ListNpcTemplates(new ListNpcTemplatesRequest { Token = token, Area = " t " }).Templates.Select(t => t.Id));
            Assert.Empty(f.Api.ListNpcTemplates(new ListNpcTemplatesRequest { Token = token, Area = "nothing" }).Templates);
        }

        [Fact]
        public void List_AndGet_WithoutAStore_AreEmpty_AndWhenTheDatabaseThrows_AreUnavailable()
        {
            var none = new Fixture(withStore: false);
            string t = none.Creator();
            Assert.Empty(none.Api.ListNpcTemplates(new ListNpcTemplatesRequest { Token = t }).Templates);
            Assert.False(none.Api.GetNpcTemplate(new NpcTemplateIdRequest { Token = t, Id = "t.a" }).Found);

            var f = new Fixture();
            string token = f.Creator();
            f.Npcs.FailReads = true;
            Assert.Equal(AdminFailure.Unavailable, Assert.Throws<AdminCallException>(() => f.Api.ListNpcTemplates(new ListNpcTemplatesRequest { Token = token })).Failure);
            Assert.Equal(AdminFailure.Unavailable, Assert.Throws<AdminCallException>(() => f.Api.GetNpcTemplate(new NpcTemplateIdRequest { Token = token, Id = "t.a" })).Failure);
            Assert.DoesNotContain(f.Log, l => l.Contains("db down"));
        }

        [Fact]
        public void Get_FoundAndNotFound_AndTheRecordCarriesEveryField_AndNullListsComeBackEmpty()
        {
            var f = new Fixture();
            string token = f.Creator();
            var e = Entry("t.wolf", "t", "a wolf");
            e.Handles.Add("wolf"); e.Adjectives.Add("grey"); e.Plurals.Add("wolves"); e.Version = 7;
            e.MaxHealth = 300; e.Experience = 5_000_000_000; e.Strength = 14;
            f.Npcs.Items["t.wolf"] = e;
            f.Npcs.Items["t.bare"] = new NpcTemplateEntry { Id = "t.bare", Area = "t", Handles = null, Adjectives = null, Plurals = null };
            GetNpcTemplateReply reply = f.Api.GetNpcTemplate(new NpcTemplateIdRequest { Token = token, Id = " t.wolf " });
            Assert.True(reply.Found);
            NpcTemplateRecord r = reply.Template;
            Assert.Equal(("t.wolf", "t", "a wolf", 7), (r.Id, r.Area, r.Short, r.Version));
            Assert.Equal(new[] { "wolf" }, r.Handles); Assert.Equal(new[] { "grey" }, r.Adjectives); Assert.Equal(new[] { "wolves" }, r.Plurals);
            Assert.True(r.HasMaxHealth); Assert.Equal(300, r.MaxHealth);
            Assert.True(r.HasExperience); Assert.Equal(5_000_000_000, r.Experience);
            Assert.True(r.HasStrength); Assert.Equal(14, r.Strength);
            Assert.False(r.HasMaxStamina); Assert.False(r.HasWisdom);
            Assert.False(f.Api.GetNpcTemplate(new NpcTemplateIdRequest { Token = token, Id = "t.none" }).Found);
            NpcTemplateRecord bare = f.Api.ListNpcTemplates(new ListNpcTemplatesRequest { Token = token }).Templates.Single(x => x.Id == "t.bare");
            Assert.Empty(bare.Handles); Assert.Empty(bare.Adjectives); Assert.Empty(bare.Plurals);
        }

        // ---------------------------------------------------------------- create

        [Fact]
        public void Create_Ok_StoresItAtVersion1_NormalizesText_StoresStats_AndAuditsFirst()
        {
            var f = new Fixture();
            string token = f.Creator();
            var record = Record(" t.guard ", " t ", "  a guard  ", "Line one.   \r\nLine two.  ");
            record.Version = 99;   // ignored
            record.MaxHealth = 800; record.Experience = 120; record.Strength = 16; record.Charisma = 0;
            NpcTemplateEditReply reply = f.Api.CreateNpcTemplate(Save(token, record));
            Assert.Equal(AdminResult.Ok, reply.Result);
            Assert.Equal(("t.guard", "t", "a guard", "Line one.\nLine two.", 1), (reply.Template.Id, reply.Template.Area, reply.Template.Short, reply.Template.Long, reply.Template.Version));
            NpcTemplateEntry stored = f.Npcs.Items["t.guard"];
            Assert.Equal(1, stored.Version);
            Assert.Equal((800, 120L, 16, 0), (stored.MaxHealth, stored.Experience, stored.Strength, stored.Charisma));
            Assert.Null(stored.MaxStamina); Assert.Null(stored.Wisdom);
            AuditRecord row = Assert.Single(f.Audit.Rows);
            Assert.Equal((AdminApi.NpcTemplateEntity, "t.guard", "*create*", true, "Cora"), (row.Entity, row.EntityId, row.Field, row.Applied, row.ActorName));
            Assert.Equal("npc_template", AdminApi.NpcTemplateEntity);
        }

        [Fact]
        public void Create_NullListsAndNoStats_Work_AndStoreEmptyLists()
        {
            var f = new Fixture();
            string token = f.Creator();
            var record = new NpcTemplateRecord { Id = "t.rat", Area = "t", Short = "a rat", Long = "Small." };   // no lists, no stats
            Assert.Equal(AdminResult.Ok, f.Api.CreateNpcTemplate(Save(token, record)).Result);
            NpcTemplateEntry stored = f.Npcs.Items["t.rat"];
            Assert.Empty(stored.Handles); Assert.Empty(stored.Adjectives); Assert.Empty(stored.Plurals);
            Assert.Null(stored.MaxHealth); Assert.Null(stored.Experience); Assert.Null(stored.Strength);
        }

        [Fact]
        public void Create_AlreadyExists_AndTheRace()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Npcs.Items["t.guard"] = Entry("t.guard", "t");
            Assert.Equal(AdminResult.AlreadyExists, f.Api.CreateNpcTemplate(Save(token, Record())).Result);
            Assert.Empty(f.Audit.Rows);

            var race = new Fixture();
            string t2 = race.Creator();
            race.Npcs.RaceOnWrite = true;
            Assert.Equal(AdminResult.AlreadyExists, race.Api.CreateNpcTemplate(Save(t2, Record())).Result);
            Assert.False(Assert.Single(race.Audit.Rows).Applied);
        }

        [Fact]
        public void Create_WithoutAStore_IsRefused_AndAMissingRecordIsInvalid()
        {
            var none = new Fixture(withStore: false);
            string t = none.Creator();
            Assert.Equal(AdminResult.Refused, none.Api.CreateNpcTemplate(Save(t, Record())).Result);
            Assert.Equal(AdminResult.Refused, none.Api.UpdateNpcTemplate(Save(t, Record())).Result);
            Assert.Equal(AdminResult.Refused, none.Api.DeleteNpcTemplate(new NpcTemplateIdRequest { Token = t, Id = "t.guard" }).Result);

            var f = new Fixture();
            Assert.Equal(AdminResult.Invalid, f.Api.CreateNpcTemplate(new SaveNpcTemplateRequest { Token = f.Creator() }).Result);
        }

        [Fact]
        public void Create_UnknownArea_IsInvalid_OnTheAreaField_AndNothingIsAudited()
        {
            var f = new Fixture();
            string token = f.Creator();
            NpcTemplateEditReply reply = f.Api.CreateNpcTemplate(Save(token, Record("zz.guard", "zz")));
            Assert.Equal(AdminResult.Invalid, reply.Result);
            Assert.Equal("Area", Assert.Single(reply.Errors).Field);
            Assert.Empty(f.Npcs.Items); Assert.Empty(f.Audit.Rows);
            // Core running without an area store cannot check, as for rooms.
            var noAreas = new Fixture(withAreas: false);
            Assert.Equal(AdminResult.Ok, noAreas.Api.CreateNpcTemplate(Save(noAreas.Creator(), Record("zz.guard", "zz"))).Result);
        }

        [Theory]
        [InlineData("u.guard", "t")]      // prefix differs from the area
        [InlineData("t.Guard", "t")]      // local key grammar
        [InlineData("tguard", "t")]       // no dot
        [InlineData("t.", "t")]
        [InlineData("", "t")]
        public void Create_BadId_IsInvalid_OnTheIdField(string id, string area)
        {
            var f = new Fixture();
            NpcTemplateEditReply reply = f.Api.CreateNpcTemplate(Save(f.Creator(), Record(id, area)));
            Assert.Equal(AdminResult.Invalid, reply.Result);
            Assert.Contains(reply.Errors, e => e.Field == "Id");
            Assert.Empty(f.Npcs.Items);
        }

        [Theory]
        [InlineData("MaxHealth", -1)] [InlineData("MaxHealth", 1001)]
        [InlineData("MaxStamina", -1)] [InlineData("MaxStamina", 1001)]
        [InlineData("MaxAlacrity", -1)] [InlineData("MaxAlacrity", 1001)]
        [InlineData("Strength", -1)] [InlineData("Strength", 256)]
        [InlineData("Dexterity", -1)] [InlineData("Dexterity", 256)]
        [InlineData("Intelligence", -1)] [InlineData("Intelligence", 256)]
        [InlineData("Wisdom", -1)] [InlineData("Wisdom", 256)]
        [InlineData("Constitution", -1)] [InlineData("Constitution", 256)]
        [InlineData("Charisma", -1)] [InlineData("Charisma", 256)]
        public void Create_StatOutOfBounds_IsInvalid_OnThatField(string field, int value)
        {
            var f = new Fixture();
            var record = Record();
            typeof(NpcTemplateRecord).GetProperty(field).SetValue(record, value);
            NpcTemplateEditReply reply = f.Api.CreateNpcTemplate(Save(f.Creator(), record));
            Assert.Equal(AdminResult.Invalid, reply.Result);
            Assert.Equal(field, Assert.Single(reply.Errors).Field);
            Assert.Empty(f.Npcs.Items);
        }

        [Fact]
        public void Create_NegativeExperience_IsInvalid_AndTheBoundsThemselvesAreAccepted()
        {
            var f = new Fixture();
            string token = f.Creator();
            var bad = Record(); bad.Experience = -1;
            NpcTemplateEditReply reply = f.Api.CreateNpcTemplate(Save(token, bad));
            Assert.Equal(AdminResult.Invalid, reply.Result);
            Assert.Equal("Experience", Assert.Single(reply.Errors).Field);

            var edge = Record();
            edge.MaxHealth = 0; edge.MaxStamina = 1000; edge.MaxAlacrity = 1000; edge.Experience = 0;
            edge.Strength = 0; edge.Dexterity = 255; edge.Intelligence = 255; edge.Wisdom = 0; edge.Constitution = 255; edge.Charisma = 255;
            Assert.Equal(AdminResult.Ok, f.Api.CreateNpcTemplate(Save(token, edge)).Result);
        }

        [Fact]
        public void Create_ListRules_BadWordsAndRepeats_AreInvalid_AndSeveralErrorsAreListedTogether()
        {
            var f = new Fixture();
            var record = Record(shortText: "");
            record.Handles.Add("guard");            // repeat
            record.Adjectives.Add("");             // blank
            record.MaxHealth = 5000;
            NpcTemplateEditReply reply = f.Api.CreateNpcTemplate(Save(f.Creator(), record));
            Assert.Equal(AdminResult.Invalid, reply.Result);
            var fields = reply.Errors.Select(e => e.Field).ToList();
            Assert.Contains("Short", fields); Assert.Contains("Handles", fields); Assert.Contains("Adjectives", fields); Assert.Contains("MaxHealth", fields);
        }

        [Fact]
        public void Create_AuditFailed_WritesNothing_AndAFailingWriteIsFailed_WithAGenericMessage()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Audit.FailInsert = true;
            Assert.Equal(AdminResult.AuditFailed, f.Api.CreateNpcTemplate(Save(token, Record())).Result);
            Assert.Empty(f.Npcs.Items);

            var g = new Fixture();
            string t2 = g.Creator();
            g.Npcs.FailWrites = true;
            NpcTemplateEditReply reply = g.Api.CreateNpcTemplate(Save(t2, Record()));
            Assert.Equal(AdminResult.Failed, reply.Result);
            Assert.Equal(AdminApi.NpcTemplateWriteFailedMessage, reply.Message);
            Assert.DoesNotContain(g.Log, l => l.Contains("db down"));
            Assert.False(Assert.Single(g.Audit.Rows).Applied);

            var h = new Fixture();
            string t3 = h.Creator();
            h.Npcs.FailReads = true;
            Assert.Equal(AdminResult.Failed, h.Api.CreateNpcTemplate(Save(t3, Record())).Result);
        }

        // ---------------------------------------------------------------- update

        [Fact]
        public void Update_Ok_BumpsTheVersionByOne_AndAuditsEachChangedFieldAndTheVersion()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Api.CreateNpcTemplate(Save(token, Record()));
            f.Audit.Rows.Clear();

            var changed = Record(shortText: "a tall guard");
            changed.MaxHealth = 900;
            NpcTemplateEditReply reply = f.Api.UpdateNpcTemplate(Save(token, changed));
            Assert.Equal(AdminResult.Ok, reply.Result);
            Assert.Equal(2, reply.Template.Version);
            Assert.Equal(2, f.Npcs.Items["t.guard"].Version);
            Assert.Equal(new[] { "maxHealth", "short", "version" }, f.Audit.Rows.Select(r => r.Field).OrderBy(x => x, StringComparer.Ordinal).ToArray());
            Assert.All(f.Audit.Rows, r => Assert.Equal((AdminApi.NpcTemplateEntity, "t.guard", true), (r.Entity, r.EntityId, r.Applied)));

            changed.MaxHealth = 901;
            Assert.Equal(3, f.Api.UpdateNpcTemplate(Save(token, changed)).Template.Version);
        }

        [Fact]
        public void Update_ClearingAStat_IsAChange_AndItBecomesNotSet()
        {
            var f = new Fixture();
            string token = f.Creator();
            var withStat = Record(); withStat.Strength = 18;
            f.Api.CreateNpcTemplate(Save(token, withStat));
            Assert.Equal(AdminResult.Ok, f.Api.UpdateNpcTemplate(Save(token, Record())).Result);
            Assert.Null(f.Npcs.Items["t.guard"].Strength);
            Assert.Equal(2, f.Npcs.Items["t.guard"].Version);
        }

        [Fact]
        public void Update_NothingChanged_IsNoChange_NoVersionBumpAndNoAudit()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Api.CreateNpcTemplate(Save(token, Record()));
            f.Audit.Rows.Clear();
            NpcTemplateEditReply reply = f.Api.UpdateNpcTemplate(Save(token, Record()));
            Assert.Equal(AdminResult.NoChange, reply.Result);
            Assert.Equal(1, f.Npcs.Items["t.guard"].Version);
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void Update_UnknownId_IsNotFound_NeverARename_AndTheValidationAppliesToUpdatesToo()
        {
            var f = new Fixture();
            string token = f.Creator();
            Assert.Equal(AdminResult.NotFound, f.Api.UpdateNpcTemplate(Save(token, Record())).Result);
            Assert.Empty(f.Npcs.Items);
            f.Api.CreateNpcTemplate(Save(token, Record()));
            var bad = Record(); bad.Dexterity = 300;
            Assert.Equal(AdminResult.Invalid, f.Api.UpdateNpcTemplate(Save(token, bad)).Result);
            Assert.Equal(AdminResult.Invalid, f.Api.UpdateNpcTemplate(Save(token, Record("t.guard", "u"))).Result);   // the area cannot change
            Assert.Equal(1, f.Npcs.Items["t.guard"].Version);
        }

        [Fact]
        public void Update_TheRace_AuditFailed_AndFailedWrite()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Api.CreateNpcTemplate(Save(token, Record()));
            var changed = Record(shortText: "other");
            f.Npcs.RaceOnWrite = true;
            Assert.Equal(AdminResult.NotFound, f.Api.UpdateNpcTemplate(Save(token, changed)).Result);
            f.Npcs.RaceOnWrite = false;
            f.Audit.FailInsert = true;
            Assert.Equal(AdminResult.AuditFailed, f.Api.UpdateNpcTemplate(Save(token, changed)).Result);
            f.Audit.FailInsert = false;
            f.Npcs.FailWrites = true;
            Assert.Equal(AdminResult.Failed, f.Api.UpdateNpcTemplate(Save(token, changed)).Result);
            Assert.Equal(1, f.Npcs.Items["t.guard"].Version);
        }

        // ---------------------------------------------------------------- delete

        [Fact]
        public void Delete_Ok_AuditsTheWholeDocument_AndNothingProtectsATemplateYet()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Api.CreateNpcTemplate(Save(token, Record()));
            f.Audit.Rows.Clear();
            NpcTemplateEditReply reply = f.Api.DeleteNpcTemplate(new NpcTemplateIdRequest { Token = token, Id = " t.guard " });
            Assert.Equal(AdminResult.Ok, reply.Result);
            Assert.Empty(f.Npcs.Items);
            AuditRecord row = Assert.Single(f.Audit.Rows);
            Assert.Equal((AdminApi.NpcTemplateEntity, "t.guard", "*delete*", true), (row.Entity, row.EntityId, row.Field, row.Applied));
        }

        [Fact]
        public void Delete_NotFound_AuditFailed_Failed_AndTheRace()
        {
            var f = new Fixture();
            string token = f.Creator();
            Assert.Equal(AdminResult.NotFound, f.Api.DeleteNpcTemplate(new NpcTemplateIdRequest { Token = token, Id = "t.none" }).Result);
            Assert.Equal(AdminResult.NotFound, f.Api.DeleteNpcTemplate(new NpcTemplateIdRequest { Token = token, Id = "" }).Result);
            f.Npcs.Items["t.guard"] = Entry("t.guard", "t");
            f.Audit.FailInsert = true;
            Assert.Equal(AdminResult.AuditFailed, f.Api.DeleteNpcTemplate(new NpcTemplateIdRequest { Token = token, Id = "t.guard" }).Result);
            f.Audit.FailInsert = false;
            f.Npcs.FailWrites = true;
            Assert.Equal(AdminResult.Failed, f.Api.DeleteNpcTemplate(new NpcTemplateIdRequest { Token = token, Id = "t.guard" }).Result);
            f.Npcs.FailWrites = false;
            f.Npcs.RaceOnWrite = true;
            Assert.Equal(AdminResult.NotFound, f.Api.DeleteNpcTemplate(new NpcTemplateIdRequest { Token = token, Id = "t.guard" }).Result);
            f.Npcs.RaceOnWrite = false;
            f.Npcs.FailReads = true;
            Assert.Equal(AdminResult.Failed, f.Api.DeleteNpcTemplate(new NpcTemplateIdRequest { Token = token, Id = "t.guard" }).Result);
        }

        // ---------------------------------------------------------------- the area delete refusal

        [Fact]
        public void DeleteArea_IsRefused_WhileNpcTemplatesUseIt_AndTheMessageNamesTheCount()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Npcs.Items["t.a"] = Entry("t.a", "t");
            AreaEditReply one = f.Api.DeleteArea(new AreaIdRequest { Token = token, Id = "t" });
            Assert.Equal(AdminResult.Refused, one.Result);
            Assert.Contains("1 NPC template still uses this area", one.Message);
            f.Npcs.Items["t.b"] = Entry("t.b", "t");
            Assert.Contains("2 NPC templates still use this area", f.Api.DeleteArea(new AreaIdRequest { Token = token, Id = "t" }).Message);
            Assert.NotNull(f.Areas.Get("t"));

            // An area that holds only another area's templates, or none, can go.
            Assert.Equal(AdminResult.Ok, f.Api.DeleteArea(new AreaIdRequest { Token = token, Id = "u" }).Result);
            f.Npcs.Items.Clear();
            Assert.Equal(AdminResult.Ok, f.Api.DeleteArea(new AreaIdRequest { Token = token, Id = "t" }).Result);
        }

        [Theory]
        [InlineData(0, 0, 1, "1 NPC template still uses this area. Move or delete them first. Nothing was changed.")]
        [InlineData(0, 0, 3, "3 NPC templates still use this area. Move or delete them first. Nothing was changed.")]
        [InlineData(1, 0, 2, "1 room and 2 NPC templates still use this area. Move or delete them first. Nothing was changed.")]
        [InlineData(2, 1, 1, "2 rooms and 1 terrain type and 1 NPC template still use this area. Move or delete them first. Nothing was changed.")]
        [InlineData(1, 0, 0, "1 room still uses this area. Move or delete them first. Nothing was changed.")]
        public void TheAreaInUseMessage_NamesNpcTemplates(int rooms, int types, int npcs, string expected)
        {
            Assert.Equal(expected, AdminApi.AreaInUseMessage(rooms, types, npcs));
        }
    }
}
