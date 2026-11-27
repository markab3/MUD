using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MUD.Contracts;
using MUD.Core.Administration;
using MUD.Core.Areas;
using MUD.Core.GameObjects;
using MUD.Core.ItemTemplates;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;
using Xunit;

namespace MUD.Tests
{
    // Item templates (collection "item_template"): rules, documents and the Admin calls. No database: an in-memory IItemTemplateStore.
    // MongoItemTemplateStore was NOT run here (no MongoDB in the sandbox); see specs/ADMIN_API.md "Not verified here".
    public class FakeItemTemplateStore : IItemTemplateStore
    {
        public Dictionary<string, ItemTemplateEntry> Items = new Dictionary<string, ItemTemplateEntry>();
        public bool FailReads;
        public bool FailWrites;
        public bool RaceOnWrite;

        public IReadOnlyList<ItemTemplateEntry> List(string area) { Check(false); return Items.Values.Where(i => string.IsNullOrEmpty(area) || i.Area == area).OrderBy(i => i.Id, StringComparer.Ordinal).ToList(); }
        public ItemTemplateEntry Get(string id) { Check(false); return Items.TryGetValue(id, out var v) ? v : null; }
        public bool Insert(ItemTemplateEntry e, string by) { Check(true); if (RaceOnWrite || Items.ContainsKey(e.Id)) { return false; } Items[e.Id] = e; return true; }
        public bool Replace(ItemTemplateEntry e, string by) { Check(true); if (RaceOnWrite || !Items.ContainsKey(e.Id)) { return false; } Items[e.Id] = e; return true; }
        public bool Delete(string id) { Check(true); return !RaceOnWrite && Items.Remove(id); }
        public int CountInArea(string area) { Check(false); return Items.Values.Count(i => i.Area == area); }
        private void Check(bool write) { if (write ? FailWrites : FailReads) { throw new InvalidOperationException("db down"); } }
    }

    public class ItemTemplateRulesTests
    {
        [Theory]
        [InlineData("t.sword", "t")]
        [InlineData("a.b.sword_2", "a.b")]
        public void CheckId_AcceptsAreaDotLocal(string id, string area) { Assert.Null(ItemTemplateRules.CheckId(id, area)); }

        [Theory]
        [InlineData("t.sword", "")]            // no area
        [InlineData("t.sword", "Bad Area")]     // bad prefix grammar
        [InlineData("", "t")]                   // no id
        [InlineData("u.sword", "t")]            // prefix differs
        [InlineData("tsword", "t")]             // no dot
        [InlineData("t.Sword", "t")]            // local key grammar
        [InlineData("t.", "t")]                 // empty local key
        public void CheckId_RefusesBadIds(string id, string area) { Assert.NotNull(ItemTemplateRules.CheckId(id, area)); }

        [Fact]
        public void CheckId_RefusesTooLongIds()
        {
            Assert.NotNull(ItemTemplateRules.CheckId("t." + new string('a', RoomRules.MaxKey), "t"));
        }

        [Fact]
        public void CheckWords_NullIsEmpty_LimitsEntriesAndRefusesBlanksAndRepeats()
        {
            Assert.Null(ItemTemplateRules.CheckWords("handles", null));
            Assert.Null(ItemTemplateRules.CheckWords("handles", new string[0]));
            Assert.Null(ItemTemplateRules.CheckWords("handles", new[] { "sword", "blade" }));
            Assert.NotNull(ItemTemplateRules.CheckWords("handles", new[] { "sword", "" }));
            Assert.NotNull(ItemTemplateRules.CheckWords("handles", new[] { "sword", "sword" }));
            Assert.NotNull(ItemTemplateRules.CheckWords("handles", new[] { new string('x', RoomRules.MaxFeatureWord + 1) }));
            Assert.NotNull(ItemTemplateRules.CheckWords("handles", Enumerable.Range(0, ItemTemplateRules.MaxWords + 1).Select(i => "w" + i).ToList()));
            Assert.Null(ItemTemplateRules.CheckWords("handles", Enumerable.Range(0, ItemTemplateRules.MaxWords).Select(i => "w" + i).ToList()));
        }

        [Fact]
        public void ParseComponents_EmptyIsNone_ObjectIsKept_JunkAndOversizeAreRefused()
        {
            Assert.Null(ItemTemplateRules.ParseComponents(null, out var none)); Assert.Null(none);
            Assert.Null(ItemTemplateRules.ParseComponents("  ", out none)); Assert.Null(none);
            Assert.Null(ItemTemplateRules.ParseComponents("{\"weight\": 3}", out var ok)); Assert.Equal(3, ok["weight"].AsInt32);
            Assert.NotNull(ItemTemplateRules.ParseComponents("not json", out var bad)); Assert.Null(bad);
            Assert.NotNull(ItemTemplateRules.ParseComponents("[1,2]", out bad));
            string big = new BsonDocument("blob", new string('x', ItemTemplateRules.MaxComponentBytes)).ToJson();
            Assert.NotNull(ItemTemplateRules.ParseComponents(big, out bad)); Assert.Null(bad);
        }

        [Fact]
        public void ParseBehavior_ValidatesLikeBehaviorRef_AndParsesTheConfig()
        {
            Assert.Null(ItemTemplateRules.ParseBehavior("item.take", "desert", "Thing", 2, "{\"a\":1}", out var parsed));
            Assert.Equal((2, 1), (parsed.Priority, parsed.Config["a"].AsInt32));
            Assert.Null(ItemTemplateRules.ParseBehavior("item.take", "desert", "Thing", 0, "", out parsed));
            Assert.Equal(0, parsed.Config.ElementCount);
            Assert.NotNull(ItemTemplateRules.ParseBehavior("item.take", "desert", "Thing", 0, "{oops", out parsed)); Assert.Null(parsed);
            string unknown = ItemTemplateRules.ParseBehavior("nope", "desert", "Thing", 0, "{}", out parsed);
            Assert.NotNull(unknown); Assert.DoesNotContain("nope", unknown);
            Assert.NotNull(ItemTemplateRules.ParseBehavior("item.take", "has space", "Thing", 0, "{}", out parsed));
            Assert.NotNull(ItemTemplateRules.ParseBehavior("item.take", "desert", "", 0, "{}", out parsed));
            string big = new BsonDocument("blob", new string('x', MUD.Core.Components.BehaviorRef.MaxConfigBytes)).ToJson();
            Assert.NotNull(ItemTemplateRules.ParseBehavior("item.take", "desert", "Thing", 0, big, out parsed));
        }

        [Fact]
        public void TheDocumentRoundTrips_AndToleratesMissingOrWrongTypedFields()
        {
            var entry = new ItemTemplateEntry
            {
                Id = "t.sword", Area = "t", Short = "a sword", Long = "Sharp.", Handles = { "sword" }, Adjectives = { "iron" }, Plurals = { "swords" },
                Components = new BsonDocument("weight", 3), Version = 4,
                Behaviors = { new MUD.Core.Components.BehaviorRef("item.take", "m", "T", 5, new BsonDocument("k", "v")) },
            };
            ItemTemplateEntry back = ItemTemplateDocuments.From(ItemTemplateDocuments.ToDocument(entry));
            Assert.Equal(("t.sword", "t", "a sword", "Sharp.", 4), (back.Id, back.Area, back.Short, back.Long, back.Version));
            Assert.Equal(new[] { "sword" }, back.Handles);
            Assert.Equal(new[] { "iron" }, back.Adjectives);
            Assert.Equal(new[] { "swords" }, back.Plurals);
            Assert.Equal(3, back.Components["weight"].AsInt32);
            var b = Assert.Single(back.Behaviors);
            Assert.Equal(("item.take", "m", "T", 5, "v"), (b.Hook, b.Module, b.Type, b.Priority, b.Config["k"].AsString));

            ItemTemplateEntry bare = ItemTemplateDocuments.From(new BsonDocument { { "_id", "x.y" }, { "handles", BsonNull.Value }, { "plurals", "oops" }, { "adjectives", new BsonArray { 1, "ok" } }, { "behaviors", new BsonArray { 3, new BsonDocument() } }, { "components", "x" }, { "version", "z" } });
            Assert.Equal((string.Empty, string.Empty, string.Empty, 1), (bare.Area, bare.Short, bare.Long, bare.Version));
            Assert.Empty(bare.Handles); Assert.Empty(bare.Plurals);
            Assert.Equal(new[] { "ok" }, bare.Adjectives);
            Assert.Null(bare.Components);
            Assert.Single(bare.Behaviors);

            BsonDocument written = ItemTemplateDocuments.ToDocument(new ItemTemplateEntry { Id = "a.b", Handles = null, Adjectives = null, Plurals = null, Behaviors = null });
            Assert.False(written.Contains("components"));
            Assert.Equal(0, written["handles"].AsBsonArray.Count);
        }
    }

    public class AdminItemTemplateApiTests
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
            // Stored room documents ("_id", "search") the template reward scan reads; tests add rooms here.
            public List<BsonDocument> Rooms = new List<BsonDocument>();
            public bool FailRewardScan;
            public List<BsonDocument> ListPlayers(string nameFilter, bool descending, int page, int pageSize, out int total) { total = 0; return new List<BsonDocument>(); }
            public BsonDocument FindPlayer(string nameKey) { return null; }
            public int CountAdmins() { return 0; }
            public List<BsonDocument> RoomsWithTemplateReward(string templateId)
            {
                if (FailRewardScan) { throw new InvalidOperationException("db down"); }
                return MUD.Core.ItemTemplates.ItemTemplateReferences.Scan(Rooms, templateId).Select(r => r.RoomKey).Distinct().Select(k => Rooms.First(d => d["_id"].AsString == k)).ToList();
            }
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
            public FakeItemTemplateStore Items = new FakeItemTemplateStore();
            public Queries Queries = new Queries();
            public List<string> Log = new List<string>();
            public AdminApi Api;

            public Fixture(bool withStore = true, bool withAreas = true)
            {
                Api = new AdminApi(Host, Players, Rooms, Queries, Audit, Sessions, new FakeClock(), Log.Add, max => 0, null, null, null, null,
                    withAreas ? Areas : null, withStore ? Items : null);
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

        private static ItemTemplateRecord Record(string id = "t.sword", string area = "t", string shortText = "a sword", string longText = "A sharp sword.")
        {
            var r = new ItemTemplateRecord { Id = id, Area = area, Short = shortText, Long = longText };
            r.Handles.Add("sword");
            return r;
        }

        private static SaveItemTemplateRequest Save(string token, ItemTemplateRecord record) { return new SaveItemTemplateRequest { Token = token, Template = record }; }

        private static ItemTemplateEntry Entry(string id, string area, string shortText = "s") { return new ItemTemplateEntry { Id = id, Area = area, Short = shortText, Long = "l" }; }

        // ---------------------------------------------------------------- roles and tokens

        [Fact]
        public void EveryCall_NeedsAGoodToken_AndACreatorOrAdmin()
        {
            var f = new Fixture();
            string player = f.Login("Pete", player: true);
            string admin = f.Login("Ada", admin: true);
            foreach (string token in new[] { "", "nope" })
            {
                Assert.Equal(AdminFailure.Unauthenticated, Assert.Throws<AdminCallException>(() => f.Api.ListItemTemplates(new ListItemTemplatesRequest { Token = token })).Failure);
                Assert.Equal(AdminFailure.Unauthenticated, Assert.Throws<AdminCallException>(() => f.Api.GetItemTemplate(new ItemTemplateIdRequest { Token = token, Id = "t.a" })).Failure);
                Assert.Equal(AdminFailure.Unauthenticated, Assert.Throws<AdminCallException>(() => f.Api.CreateItemTemplate(Save(token, Record()))).Failure);
                Assert.Equal(AdminFailure.Unauthenticated, Assert.Throws<AdminCallException>(() => f.Api.UpdateItemTemplate(Save(token, Record()))).Failure);
                Assert.Equal(AdminFailure.Unauthenticated, Assert.Throws<AdminCallException>(() => f.Api.DeleteItemTemplate(new ItemTemplateIdRequest { Token = token, Id = "t.a" })).Failure);
            }
            Assert.Equal(AdminFailure.Forbidden, Assert.Throws<AdminCallException>(() => f.Api.ListItemTemplates(new ListItemTemplatesRequest { Token = player })).Failure);
            Assert.Equal(AdminFailure.Forbidden, Assert.Throws<AdminCallException>(() => f.Api.GetItemTemplate(new ItemTemplateIdRequest { Token = player, Id = "t.a" })).Failure);
            Assert.Equal(AdminFailure.Forbidden, Assert.Throws<AdminCallException>(() => f.Api.CreateItemTemplate(Save(player, Record()))).Failure);
            Assert.Equal(AdminFailure.Forbidden, Assert.Throws<AdminCallException>(() => f.Api.UpdateItemTemplate(Save(player, Record()))).Failure);
            Assert.Equal(AdminFailure.Forbidden, Assert.Throws<AdminCallException>(() => f.Api.DeleteItemTemplate(new ItemTemplateIdRequest { Token = player, Id = "t.a" })).Failure);
            Assert.Empty(f.Items.Items);
            Assert.Empty(f.Audit.Rows);
            Assert.Equal(AdminResult.Ok, f.Api.CreateItemTemplate(Save(admin, Record())).Result);   // an admin may do everything a creator may
        }

        [Fact]
        public void AWriteWhenTheGameThreadIsBusy_IsUnavailable()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Host.Available = false;
            Assert.Equal(AdminFailure.Unavailable, Assert.Throws<AdminCallException>(() => f.Api.CreateItemTemplate(Save(token, Record()))).Failure);
        }

        // ---------------------------------------------------------------- reads

        [Fact]
        public void List_IsSortedById_AndFiltersByArea_AndTrimsTheFilter()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Items.Items["u.b"] = Entry("u.b", "u"); f.Items.Items["t.z"] = Entry("t.z", "t"); f.Items.Items["t.a"] = Entry("t.a", "t");
            Assert.Equal(new[] { "t.a", "t.z", "u.b" }, f.Api.ListItemTemplates(new ListItemTemplatesRequest { Token = token }).Templates.Select(t => t.Id));
            Assert.Equal(new[] { "t.a", "t.z" }, f.Api.ListItemTemplates(new ListItemTemplatesRequest { Token = token, Area = " t " }).Templates.Select(t => t.Id));
            Assert.Empty(f.Api.ListItemTemplates(new ListItemTemplatesRequest { Token = token, Area = "nothing" }).Templates);
        }

        [Fact]
        public void List_AndGet_WithoutAStore_AreEmpty_AndWhenTheDatabaseThrows_AreUnavailable()
        {
            var none = new Fixture(withStore: false);
            string t = none.Creator();
            Assert.Empty(none.Api.ListItemTemplates(new ListItemTemplatesRequest { Token = t }).Templates);
            Assert.False(none.Api.GetItemTemplate(new ItemTemplateIdRequest { Token = t, Id = "t.a" }).Found);

            var f = new Fixture();
            string token = f.Creator();
            f.Items.FailReads = true;
            Assert.Equal(AdminFailure.Unavailable, Assert.Throws<AdminCallException>(() => f.Api.ListItemTemplates(new ListItemTemplatesRequest { Token = token })).Failure);
            Assert.Equal(AdminFailure.Unavailable, Assert.Throws<AdminCallException>(() => f.Api.GetItemTemplate(new ItemTemplateIdRequest { Token = token, Id = "t.a" })).Failure);
            Assert.DoesNotContain(f.Log, l => l.Contains("db down"));
        }

        [Fact]
        public void Get_FoundAndNotFound_AndTheRecordCarriesEveryField()
        {
            var f = new Fixture();
            string token = f.Creator();
            var e = Entry("t.wand", "t", "a wand");
            e.Handles.Add("wand"); e.Adjectives.Add("oak"); e.Plurals.Add("wands"); e.Components = new BsonDocument("charges", 3); e.Version = 7;
            e.Behaviors.Add(new MUD.Core.Components.BehaviorRef("item.take", "m", "Wand", 1, new BsonDocument("spell", "fire")));
            f.Items.Items["t.wand"] = e;
            GetItemTemplateReply reply = f.Api.GetItemTemplate(new ItemTemplateIdRequest { Token = token, Id = " t.wand " });
            Assert.True(reply.Found);
            ItemTemplateRecord r = reply.Template;
            Assert.Equal(("t.wand", "t", "a wand", 7), (r.Id, r.Area, r.Short, r.Version));
            Assert.Equal(new[] { "wand" }, r.Handles); Assert.Equal(new[] { "oak" }, r.Adjectives); Assert.Equal(new[] { "wands" }, r.Plurals);
            Assert.Equal("{\"charges\":3}", r.ComponentsJson);
            Assert.Equal(("item.take", "m", "Wand", 1, "{\"spell\":\"fire\"}"), (r.Behaviors[0].Hook, r.Behaviors[0].Module, r.Behaviors[0].Type, r.Behaviors[0].Priority, r.Behaviors[0].ConfigJson));
            Assert.False(f.Api.GetItemTemplate(new ItemTemplateIdRequest { Token = token, Id = "t.none" }).Found);
        }

        // ---------------------------------------------------------------- create

        [Fact]
        public void Create_Ok_StoresItAtVersion1_NormalizesText_AndAuditsFirst()
        {
            var f = new Fixture();
            string token = f.Creator();
            var record = Record(" t.sword ", " t ", "  a sword  ", "Line one.   \r\nLine two.  ");
            record.Version = 99;   // ignored
            record.ComponentsJson = "{\"weight\": 3}";
            record.Behaviors.Add(new RoomBehavior { Hook = "item.take", Module = "m", Type = "T", Priority = 2, ConfigJson = "{\"a\":1}" });
            ItemTemplateEditReply reply = f.Api.CreateItemTemplate(Save(token, record));
            Assert.Equal(AdminResult.Ok, reply.Result);
            Assert.Equal(("t.sword", "t", "a sword", "Line one.\nLine two.", 1), (reply.Template.Id, reply.Template.Area, reply.Template.Short, reply.Template.Long, reply.Template.Version));
            ItemTemplateEntry stored = f.Items.Items["t.sword"];
            Assert.Equal(1, stored.Version);
            Assert.Equal(3, stored.Components["weight"].AsInt32);
            Assert.Equal(1, Assert.Single(stored.Behaviors).Config["a"].AsInt32);
            AuditRecord row = Assert.Single(f.Audit.Rows);
            Assert.Equal((AdminApi.ItemTemplateEntity, "t.sword", "*create*", true, "Cora"), (row.Entity, row.EntityId, row.Field, row.Applied, row.ActorName));
        }

        [Fact]
        public void Create_NullListsAndNoOptionalParts_Work_AndStoreEmptyLists()
        {
            var f = new Fixture();
            string token = f.Creator();
            var record = new ItemTemplateRecord { Id = "t.rock", Area = "t", Short = "a rock", Long = "Grey." };   // no handles, adjectives, plurals, components, behaviors
            Assert.Equal(AdminResult.Ok, f.Api.CreateItemTemplate(Save(token, record)).Result);
            ItemTemplateEntry stored = f.Items.Items["t.rock"];
            Assert.Empty(stored.Handles); Assert.Empty(stored.Adjectives); Assert.Empty(stored.Plurals); Assert.Empty(stored.Behaviors); Assert.Null(stored.Components);
            Assert.Equal(string.Empty, f.Api.GetItemTemplate(new ItemTemplateIdRequest { Token = token, Id = "t.rock" }).Template.ComponentsJson);
        }

        [Fact]
        public void Create_ARecordWithNoWords_NullEntriesInTheStoredLists_AreRead()
        {
            // A stored document with null lists (older or hand-edited data) is returned as empty lists, never a NullReferenceException.
            var f = new Fixture();
            string token = f.Creator();
            f.Items.Items["t.old"] = new ItemTemplateEntry { Id = "t.old", Area = "t", Short = "s", Long = "l", Handles = null, Adjectives = null, Plurals = null, Behaviors = null };
            ItemTemplateRecord r = f.Api.GetItemTemplate(new ItemTemplateIdRequest { Token = token, Id = "t.old" }).Template;
            Assert.Empty(r.Handles); Assert.Empty(r.Adjectives); Assert.Empty(r.Plurals); Assert.Empty(r.Behaviors);
            Assert.Single(f.Api.ListItemTemplates(new ListItemTemplatesRequest { Token = token }).Templates);
        }

        [Fact]
        public void Create_Invalid_ListsEveryBrokenField_WritesNothing_AndEchoesNothing()
        {
            var f = new Fixture();
            string token = f.Creator();
            var record = Record("Bad Id", "t", "", "");
            record.Handles.Add("");
            record.Adjectives.Add("a"); record.Adjectives.Add("a");
            record.Plurals.Add(new string('p', 100));
            record.ComponentsJson = "nope";
            ItemTemplateEditReply reply = f.Api.CreateItemTemplate(Save(token, record));
            Assert.Equal(AdminResult.Invalid, reply.Result);
            Assert.Equal(new[] { "Id", "Short", "Long", "Handles", "Adjectives", "Plurals", "ComponentsJson" }, reply.Errors.Select(e => e.Field));
            Assert.DoesNotContain("Bad Id", string.Join("|", reply.Errors.Select(e => e.Message)));
            Assert.Empty(f.Items.Items);
            Assert.Empty(f.Audit.Rows);
            Assert.Equal(AdminResult.Invalid, f.Api.CreateItemTemplate(Save(token, null)).Result);
        }

        [Theory]
        [InlineData("u.sword", "t")]     // the id prefix is not the area
        [InlineData("t.sword", "")]      // no area
        [InlineData("t.sword", "Bad Area")]
        [InlineData("tsword", "t")]      // no dot
        public void Create_BadKeyPrefix_IsInvalidOnId(string id, string area)
        {
            var f = new Fixture();
            ItemTemplateEditReply reply = f.Api.CreateItemTemplate(Save(f.Creator(), Record(id, area)));
            Assert.Equal(AdminResult.Invalid, reply.Result);
            Assert.Contains("Id", reply.Errors.Select(e => e.Field));
            Assert.Empty(f.Items.Items);
        }

        [Fact]
        public void Create_UnknownArea_IsInvalidOnArea_ButWithoutAnAreaStoreNothingIsChecked()
        {
            var f = new Fixture();
            ItemTemplateEditReply reply = f.Api.CreateItemTemplate(Save(f.Creator(), Record("nowhere.sword", "nowhere")));
            Assert.Equal(AdminResult.Invalid, reply.Result);
            Assert.Equal("Area", Assert.Single(reply.Errors).Field);
            Assert.Empty(f.Items.Items);

            var none = new Fixture(withAreas: false);
            Assert.Equal(AdminResult.Ok, none.Api.CreateItemTemplate(Save(none.Creator(), Record("nowhere.sword", "nowhere"))).Result);

            var down = new Fixture();
            string token = down.Creator();
            down.Areas.FailReads = true;
            Assert.Equal(AdminFailure.Unavailable, Assert.Throws<AdminCallException>(() => down.Api.CreateItemTemplate(Save(token, Record()))).Failure);
        }

        [Fact]
        public void Create_OversizeComponentsAndConfig_AreInvalid()
        {
            var f = new Fixture();
            string token = f.Creator();
            string big = new BsonDocument("blob", new string('x', ItemTemplateRules.MaxComponentBytes)).ToJson();
            var a = Record(); a.ComponentsJson = big;
            ItemTemplateEditReply reply = f.Api.CreateItemTemplate(Save(token, a));
            Assert.Equal(("ComponentsJson", AdminResult.Invalid), (Assert.Single(reply.Errors).Field, reply.Result));

            var b = Record();
            b.Behaviors.Add(new RoomBehavior { Hook = "item.take", Module = "m", Type = "T", ConfigJson = new BsonDocument("blob", new string('x', MUD.Core.Components.BehaviorRef.MaxConfigBytes)).ToJson() });
            reply = f.Api.CreateItemTemplate(Save(token, b));
            Assert.Equal("Behaviors", Assert.Single(reply.Errors).Field);
            Assert.Contains("Behavior 1", reply.Errors[0].Message);
            Assert.Empty(f.Items.Items);
        }

        [Fact]
        public void Create_BadBehaviors_AreInvalid_NumberedPerEntry_AndTooManyAreRefused()
        {
            var f = new Fixture();
            string token = f.Creator();
            var r = Record();
            r.Behaviors.Add(new RoomBehavior { Hook = "item.take", Module = "m", Type = "T" });
            r.Behaviors.Add(new RoomBehavior { Hook = "bogus", Module = "m", Type = "T" });
            r.Behaviors.Add(new RoomBehavior { Hook = "item.take", Module = "m", Type = "T", ConfigJson = "{bad" });
            ItemTemplateEditReply reply = f.Api.CreateItemTemplate(Save(token, r));
            Assert.Equal(AdminResult.Invalid, reply.Result);
            Assert.Equal(2, reply.Errors.Count);
            Assert.StartsWith("Behavior 2:", reply.Errors[0].Message);
            Assert.StartsWith("Behavior 3:", reply.Errors[1].Message);
            Assert.DoesNotContain("bogus", reply.Errors[0].Message);

            var many = Record();
            for (int i = 0; i <= ItemTemplateRules.MaxBehaviors; i++) { many.Behaviors.Add(new RoomBehavior { Hook = "item.take", Module = "m", Type = "T" }); }
            Assert.Equal("Behaviors", Assert.Single(f.Api.CreateItemTemplate(Save(token, many)).Errors).Field);
        }

        [Fact]
        public void Create_DuplicateId_AlreadyExists_AlsoWhenSomeoneElseCreatedItInBetween()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Items.Items["t.sword"] = Entry("t.sword", "t", "mine");
            ItemTemplateEditReply dup = f.Api.CreateItemTemplate(Save(token, Record()));
            Assert.Equal((AdminResult.AlreadyExists, AdminApi.ItemTemplateAlreadyExistsMessage), (dup.Result, dup.Message));
            Assert.Equal("mine", f.Items.Items["t.sword"].Short);
            Assert.Empty(f.Audit.Rows);

            var race = new Fixture();
            string t2 = race.Creator();
            race.Items.RaceOnWrite = true;
            Assert.Equal(AdminResult.AlreadyExists, race.Api.CreateItemTemplate(Save(t2, Record())).Result);
            Assert.False(Assert.Single(race.Audit.Rows).Applied);
        }

        [Fact]
        public void Create_AuditFailed_Refused_Failed_AndAReadFailure()
        {
            var f = new Fixture();
            string token = f.Creator();
            f.Audit.FailInsert = true;
            Assert.Equal(AdminResult.AuditFailed, f.Api.CreateItemTemplate(Save(token, Record())).Result);
            Assert.Empty(f.Items.Items);
            f.Audit.FailInsert = false;

            f.Items.FailWrites = true;
            ItemTemplateEditReply failed = f.Api.CreateItemTemplate(Save(token, Record()));
            Assert.Equal((AdminResult.Failed, AdminApi.ItemTemplateWriteFailedMessage), (failed.Result, failed.Message));
            Assert.False(Assert.Single(f.Audit.Rows).Applied);
            Assert.DoesNotContain(f.Log, l => l.Contains("db down"));
            f.Items.FailWrites = false;

            f.Items.FailReads = true;
            Assert.Equal(AdminResult.Failed, f.Api.CreateItemTemplate(Save(token, Record())).Result);

            var none = new Fixture(withStore: false);
            ItemTemplateEditReply refused = none.Api.CreateItemTemplate(Save(none.Creator(), Record()));
            Assert.Equal((AdminResult.Refused, AdminApi.ItemTemplateUnavailableMessage), (refused.Result, refused.Message));
        }

        // ---------------------------------------------------------------- update

        private static Fixture WithSword(out string token)
        {
            var f = new Fixture();
            token = f.Creator();
            Assert.Equal(AdminResult.Ok, f.Api.CreateItemTemplate(Save(token, Record())).Result);
            f.Audit.Rows.Clear();
            return f;
        }

        [Fact]
        public void Update_ChangedFields_BumpTheVersionByOne_AndAuditOneRowPerChangedField()
        {
            var f = WithSword(out string token);
            var edit = Record(shortText: "a fine sword");
            edit.Handles.Add("blade");
            edit.ComponentsJson = "{\"weight\":2}";
            ItemTemplateEditReply reply = f.Api.UpdateItemTemplate(Save(token, edit));
            Assert.Equal(AdminResult.Ok, reply.Result);
            Assert.Equal(2, reply.Template.Version);
            Assert.Equal(2, f.Items.Items["t.sword"].Version);
            Assert.Equal(new[] { "short", "handles", "components", "version" }, f.Audit.Rows.Select(r => r.Field));
            Assert.All(f.Audit.Rows, r => Assert.True(r.Applied));
            Assert.Equal(AdminResult.Ok, f.Api.UpdateItemTemplate(Save(token, Record(shortText: "a finer sword"))).Result);
            Assert.Equal(3, f.Items.Items["t.sword"].Version);   // the second edit also dropped the handle and the components
        }

        [Fact]
        public void Update_TheAreaAndIdCannotChange_TheStoredAreaWinsNothing_InvalidWhenTheyDisagree()
        {
            var f = WithSword(out string token);
            // Area u with id prefix t is a bad key prefix: the id is the key and never changes.
            ItemTemplateEditReply reply = f.Api.UpdateItemTemplate(Save(token, Record("t.sword", "u")));
            Assert.Equal(AdminResult.Invalid, reply.Result);
            Assert.Equal("t", f.Items.Items["t.sword"].Area);
        }

        [Fact]
        public void Update_NoChange_NotFound_Invalid_AuditFailed_Failed_Race_Refused()
        {
            var f = WithSword(out string token);
            ItemTemplateEditReply same = f.Api.UpdateItemTemplate(Save(token, Record()));
            Assert.Equal((AdminResult.NoChange, AdminApi.ItemTemplateNothingChangedMessage), (same.Result, same.Message));
            Assert.Equal(1, same.Template.Version);
            Assert.Empty(f.Audit.Rows);

            Assert.Equal(AdminResult.NotFound, f.Api.UpdateItemTemplate(Save(token, Record("t.nothing"))).Result);
            Assert.Equal(AdminResult.Invalid, f.Api.UpdateItemTemplate(Save(token, Record(shortText: ""))).Result);

            f.Audit.FailInsert = true;
            Assert.Equal(AdminResult.AuditFailed, f.Api.UpdateItemTemplate(Save(token, Record(shortText: "x"))).Result);
            f.Audit.FailInsert = false;
            Assert.Equal(1, f.Items.Items["t.sword"].Version);

            f.Items.FailWrites = true;
            Assert.Equal(AdminResult.Failed, f.Api.UpdateItemTemplate(Save(token, Record(shortText: "x"))).Result);
            f.Items.FailWrites = false;
            Assert.Equal(1, f.Items.Items["t.sword"].Version);

            f.Items.FailReads = true;
            Assert.Equal(AdminResult.Failed, f.Api.UpdateItemTemplate(Save(token, Record(shortText: "x"))).Result);
            f.Items.FailReads = false;

            f.Items.RaceOnWrite = true;
            Assert.Equal(AdminResult.NotFound, f.Api.UpdateItemTemplate(Save(token, Record(shortText: "x"))).Result);

            var none = new Fixture(withStore: false);
            Assert.Equal(AdminResult.Refused, none.Api.UpdateItemTemplate(Save(none.Creator(), Record())).Result);
        }

        // ---------------------------------------------------------------- delete

        [Fact]
        public void Delete_Ok_AuditsTheOldDocument_NotFound_AuditFailed_Failed_Race_Refused()
        {
            var f = WithSword(out string token);
            Assert.Equal(AdminResult.NotFound, f.Api.DeleteItemTemplate(new ItemTemplateIdRequest { Token = token, Id = "t.none" }).Result);
            Assert.Equal(AdminResult.NotFound, f.Api.DeleteItemTemplate(new ItemTemplateIdRequest { Token = token, Id = " " }).Result);

            f.Audit.FailInsert = true;
            Assert.Equal(AdminResult.AuditFailed, f.Api.DeleteItemTemplate(new ItemTemplateIdRequest { Token = token, Id = "t.sword" }).Result);
            f.Audit.FailInsert = false;
            f.Items.FailWrites = true;
            Assert.Equal(AdminResult.Failed, f.Api.DeleteItemTemplate(new ItemTemplateIdRequest { Token = token, Id = "t.sword" }).Result);
            f.Items.FailWrites = false;
            f.Items.FailReads = true;
            Assert.Equal(AdminResult.Failed, f.Api.DeleteItemTemplate(new ItemTemplateIdRequest { Token = token, Id = "t.sword" }).Result);
            f.Items.FailReads = false;
            f.Items.RaceOnWrite = true;
            Assert.Equal(AdminResult.NotFound, f.Api.DeleteItemTemplate(new ItemTemplateIdRequest { Token = token, Id = "t.sword" }).Result);
            f.Items.RaceOnWrite = false;
            Assert.Single(f.Items.Items);
            f.Audit.Rows.Clear();

            Assert.Equal(AdminResult.Ok, f.Api.DeleteItemTemplate(new ItemTemplateIdRequest { Token = token, Id = " t.sword " }).Result);
            Assert.Empty(f.Items.Items);
            AuditRecord row = Assert.Single(f.Audit.Rows);
            Assert.Equal((AdminApi.ItemTemplateEntity, "t.sword", "*delete*", true), (row.Entity, row.EntityId, row.Field, row.Applied));

            var none = new Fixture(withStore: false);
            Assert.Equal(AdminResult.Refused, none.Api.DeleteItemTemplate(new ItemTemplateIdRequest { Token = none.Creator(), Id = "t.sword" }).Result);
        }

        // ---------------------------------------------------------------- references: delete protection and the stale report

        private static BsonDocument RoomWithReward(string key, string entryId, string templateId, BsonValue version)
        {
            var reward = new BsonDocument { { "ShortDescription", "a sword" } };
            if (templateId != null) { reward["templateId"] = templateId; }
            if (version != null) { reward["templateVersion"] = version; }
            return new BsonDocument { { "_id", key }, { "search", new BsonDocument("entries", new BsonArray { new BsonDocument { { "id", entryId }, { "reward", reward } } }) } };
        }

        [Fact]
        public void Delete_IsRefusedWhileARewardCarriesTheTemplateId_NamingTheCount_AndChangesNothing()
        {
            var f = WithSword(out string token);
            f.Queries.Rooms.Add(RoomWithReward("t.a", "s1", "t.sword", 1));
            f.Queries.Rooms.Add(RoomWithReward("t.b", "s1", "t.sword", 1));
            f.Queries.Rooms.Add(RoomWithReward("t.c", "s1", "t.other", 1));

            var reply = f.Api.DeleteItemTemplate(new ItemTemplateIdRequest { Token = token, Id = "t.sword" });

            Assert.Equal(AdminResult.Refused, reply.Result);
            Assert.Equal(AdminApi.ItemTemplateReferencedMessage(2), reply.Message);
            Assert.Contains("2 search rewards", reply.Message);
            Assert.Single(f.Items.Items);
            Assert.Empty(f.Audit.Rows);   // refused before anything is audited or written
        }

        [Fact]
        public void Delete_SaysSingularForOne_AndProceedsOnceNoRewardCarriesTheId()
        {
            var f = WithSword(out string token);
            f.Queries.Rooms.Add(RoomWithReward("t.a", "s1", "t.sword", 1));
            Assert.Contains("1 search reward.", f.Api.DeleteItemTemplate(new ItemTemplateIdRequest { Token = token, Id = "t.sword" }).Message);

            f.Queries.Rooms.Clear();
            Assert.Equal(AdminResult.Ok, f.Api.DeleteItemTemplate(new ItemTemplateIdRequest { Token = token, Id = "t.sword" }).Result);
            Assert.Empty(f.Items.Items);
        }

        [Fact]
        public void Delete_WhenTheRoomsCannotBeRead_IsFailedAndKeepsTheTemplate()
        {
            var f = WithSword(out string token);
            f.Queries.FailRewardScan = true;
            Assert.Equal(AdminResult.Failed, f.Api.DeleteItemTemplate(new ItemTemplateIdRequest { Token = token, Id = "t.sword" }).Result);
            Assert.Single(f.Items.Items);
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void References_ListCopiesInRoomOrder_AndMarkTheOnesBehindTheCurrentVersionStale()
        {
            var f = WithSword(out string token);
            var edit = Record(shortText: "a fine sword");
            Assert.Equal(AdminResult.Ok, f.Api.UpdateItemTemplate(Save(token, edit)).Result);          // version 2
            Assert.Equal(2, f.Items.Items["t.sword"].Version);
            f.Queries.Rooms.Add(RoomWithReward("t.b", "s2", "t.sword", 2));
            f.Queries.Rooms.Add(RoomWithReward("t.a", "s1", "t.sword", 1));
            f.Queries.Rooms.Add(RoomWithReward("t.c", "s1", "t.sword", null));                          // no version stored: counts as 0, stale
            f.Queries.Rooms.Add(RoomWithReward("t.d", "s1", null, 1));                                   // a plain reward: not a copy
            f.Queries.Rooms.Add(RoomWithReward("t.e", "s1", "t.other", 1));

            var reply = f.Api.ListItemTemplateReferences(new ItemTemplateIdRequest { Token = token, Id = " t.sword " });

            Assert.Equal(2, reply.CurrentVersion);
            Assert.Equal(new[] { "t.a", "t.b", "t.c" }, reply.References.Select(r => r.RoomKey));
            Assert.Equal(new[] { true, false, true }, reply.References.Select(r => r.Stale));
            Assert.Equal(new[] { 1, 2, 0 }, reply.References.Select(r => r.TemplateVersion));
            Assert.Equal(new[] { "s1", "s2", "s1" }, reply.References.Select(r => r.EntryId));
        }

        [Fact]
        public void References_AreEmptyForAnUnknownTemplate_BlankId_OrNoStore_AndNeedACreator()
        {
            var f = WithSword(out string token);
            f.Queries.Rooms.Add(RoomWithReward("t.a", "s1", "t.gone", 1));
            Assert.Empty(f.Api.ListItemTemplateReferences(new ItemTemplateIdRequest { Token = token, Id = "t.gone" }).References);
            Assert.Equal(0, f.Api.ListItemTemplateReferences(new ItemTemplateIdRequest { Token = token, Id = " " }).CurrentVersion);
            var none = new Fixture(withStore: false);
            Assert.Empty(none.Api.ListItemTemplateReferences(new ItemTemplateIdRequest { Token = none.Creator(), Id = "t.sword" }).References);

            Assert.Equal(AdminFailure.Unauthenticated, Assert.Throws<AdminCallException>(() => f.Api.ListItemTemplateReferences(new ItemTemplateIdRequest { Token = "nope", Id = "t.sword" })).Failure);
            string player = f.Login("Pete", player: true);
            Assert.Equal(AdminFailure.Forbidden, Assert.Throws<AdminCallException>(() => f.Api.ListItemTemplateReferences(new ItemTemplateIdRequest { Token = player, Id = "t.sword" })).Failure);
        }

        [Fact]
        public void References_WhenTheRoomsOrTheLibraryCannotBeRead_IsUnavailable()
        {
            var f = WithSword(out string token);
            f.Queries.FailRewardScan = true;
            Assert.Equal(AdminFailure.Unavailable, Assert.Throws<AdminCallException>(() => f.Api.ListItemTemplateReferences(new ItemTemplateIdRequest { Token = token, Id = "t.sword" })).Failure);
            f.Queries.FailRewardScan = false;
            f.Items.FailReads = true;
            Assert.Equal(AdminFailure.Unavailable, Assert.Throws<AdminCallException>(() => f.Api.ListItemTemplateReferences(new ItemTemplateIdRequest { Token = token, Id = "t.sword" })).Failure);
        }

        [Fact]
        public void TheScan_ToleratesMissingAndWronglyTypedFields_AndCanListEveryTemplate()
        {
            var rooms = new List<BsonDocument>
            {
                new BsonDocument("_id", "t.none"),
                new BsonDocument { { "_id", "t.s1" }, { "search", "x" } },
                new BsonDocument { { "_id", "t.s2" }, { "search", new BsonDocument("entries", "x") } },
                new BsonDocument { { "_id", "t.s3" }, { "search", new BsonDocument("entries", new BsonArray { 1, new BsonDocument("id", "a"), new BsonDocument { { "id", "b" }, { "reward", "x" } }, new BsonDocument { { "id", "c" }, { "reward", new BsonDocument("templateId", 5) } } }) } },
                RoomWithReward("t.ok", "k", "t.sword", 3),
                RoomWithReward("t.odd", "k", "t.axe", "three"),
            };
            Assert.Empty(ItemTemplateReferences.Scan(null, "t.sword"));
            var one = Assert.Single(ItemTemplateReferences.Scan(rooms, "t.sword"));
            Assert.Equal(("t.ok", "k", 3), (one.RoomKey, one.EntryId, one.TemplateVersion));
            var all = ItemTemplateReferences.Scan(rooms, null);
            Assert.Equal(new[] { "t.odd", "t.ok" }, all.Select(r => r.RoomKey));
            Assert.Equal(0, all[0].TemplateVersion);   // a non-numeric version counts as 0
        }
    }

    // The copy of a template into an item (clone command and importer share it).
    public class ItemTemplateCopyTests
    {
        [Fact]
        public void ToItem_CopiesEveryFieldIntoANewIndependentItem()
        {
            var template = new ItemTemplateEntry
            {
                Id = "t.lamp", Area = "t", Short = "a lamp", Long = "A brass lamp.", Handles = { "lamp" }, Adjectives = { "brass" }, Plurals = { "lamps" }, Version = 3,
                Components = new BsonDocument("desert", new BsonDocument("fuel", new BsonDocument("amount", 5))),
                Behaviors = { new MUD.Core.Components.BehaviorRef("item.take", "desert", "Lamp", 2, new BsonDocument("k", "v")) },
            };

            var a = ItemTemplateCopy.ToItem(template);
            var b = ItemTemplateCopy.ToItem(template);

            Assert.Equal(("a lamp", "A brass lamp."), (a.ShortDescription, a.LongDescription));
            Assert.Equal(new[] { "lamp" }, a.Handles);
            Assert.Equal(new[] { "brass" }, a.Adjectives);
            Assert.Equal(new[] { "lamps" }, a.Plurals);
            Assert.Equal(5, a.Components["desert"]["fuel"]["amount"].AsInt32);
            var behavior = Assert.Single(a.Behaviors);
            Assert.Equal(("item.take", "desert", "Lamp", 2, "v"), (behavior.Hook, behavior.Module, behavior.Type, behavior.Priority, behavior.Config["k"].AsString));
            Assert.NotEqual(a.Id, b.Id);
            Assert.Null(a.TemplateId);

            // Changing the copy leaves the template (and the other copy) alone.
            a.Handles.Add("torch"); a.Components["desert"]["fuel"]["amount"] = 1; a.Behaviors[0].Config["k"] = "changed";
            Assert.Equal(new[] { "lamp" }, template.Handles);
            Assert.Equal(5, template.Components["desert"]["fuel"]["amount"].AsInt32);
            Assert.Equal("v", template.Behaviors[0].Config["k"].AsString);
            Assert.Equal(5, b.Components["desert"]["fuel"]["amount"].AsInt32);
        }

        [Fact]
        public void ToItem_WithNoOptionalPartsGivesEmptyListsAndNoComponents()
        {
            var item = ItemTemplateCopy.ToItem(new ItemTemplateEntry { Id = "t.x", Short = "x", Long = "y", Handles = null, Adjectives = null, Plurals = null, Behaviors = null });
            Assert.Empty(item.Handles); Assert.Empty(item.Adjectives); Assert.Empty(item.Plurals); Assert.Empty(item.Behaviors); Assert.Empty(item.Components);
        }

        [Fact]
        public void Components_SkipWhatDoesNotFitTheModuleNameShape()
        {
            var template = new ItemTemplateEntry
            {
                Id = "t.x",
                Components = new BsonDocument
                {
                    { "weight", 3 },                                                   // not an object
                    { "bad.module", new BsonDocument("n", new BsonDocument("a", 1)) },  // module name with a dot
                    { "m", new BsonDocument { { "plain", 1 }, { "bad.name", new BsonDocument("a", 1) }, { "ok", new BsonDocument("a", 2) } } },
                    { "empty", new BsonDocument("plain", 1) },                          // nothing usable inside: no entry
                },
            };
            var parts = ItemTemplateCopy.ComponentsOf(template);
            var module = Assert.Single(parts);
            Assert.Equal("m", module.Key);
            Assert.Equal(new[] { "ok" }, module.Value.Keys);
            Assert.Empty(ItemTemplateCopy.ComponentsOf(null));
            Assert.Empty(ItemTemplateCopy.BehaviorsOf(null));
        }
    }
}
