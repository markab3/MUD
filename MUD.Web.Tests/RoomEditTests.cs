using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MongoDB.Bson;
using MUD.Web.Pages;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

public class FakeRoomEditStore : IRoomEditStore
{
    public Dictionary<string, RoomDetail> Rooms { get; } = new();
    public List<(string Key, int ExpectedRev, BsonDocument Set)> Updates { get; } = new();
    public List<(string Key, BsonDocument Doc)> Inserts { get; } = new();
    public List<string> Deletes { get; } = new();
    // Simulates another editor winning the race between our read and our write.
    public Action<string>? BeforeUpdate { get; set; }

    public FakeRoomEditStore Add(string key, string area = "t", int? x = 0, int? y = 0, int? z = 0, int rev = 0, params (string Dir, string To)[] exits)
    {
        Rooms[key] = new RoomDetail(key, "short " + key, "long " + key, area, x, y, z, exits.Select(e => new RoomExitData(e.Dir, e.To)).ToList(), rev);
        return this;
    }

    public RoomDetail? Find(string key) => Rooms.TryGetValue(key, out var r) ? r : null;
    public bool Exists(string key) => Rooms.ContainsKey(key);

    public StoreUpdateOutcome UpdateIfRev(string key, int expectedRev, BsonDocument set)
    {
        BeforeUpdate?.Invoke(key);
        Updates.Add((key, expectedRev, set));
        if (!Rooms.TryGetValue(key, out var r)) { return StoreUpdateOutcome.Missing; }
        if (r.Rev != expectedRev) { return StoreUpdateOutcome.Conflict; }
        var exits = r.Exits;
        if (set.Contains("Exits"))
        {
            exits = set["Exits"].AsBsonArray.Select(e => new RoomExitData(e["Name"].AsString, e["DestinationId"].AsString)).ToList();
        }
        Rooms[key] = r with
        {
            Short = set.Contains("ShortDescription") ? set["ShortDescription"].AsString : r.Short,
            Long = set.Contains("LongDescription") ? set["LongDescription"].AsString : r.Long,
            Area = set.Contains("Area") ? set["Area"].AsString : r.Area,
            X = set.Contains("X") ? (set["X"].IsBsonNull ? null : set["X"].AsInt32) : r.X,
            Y = set.Contains("Y") ? (set["Y"].IsBsonNull ? null : set["Y"].AsInt32) : r.Y,
            Z = set.Contains("Z") ? (set["Z"].IsBsonNull ? null : set["Z"].AsInt32) : r.Z,
            Exits = exits,
            Rev = r.Rev + 1,
        };
        return StoreUpdateOutcome.Applied;
    }

    public bool InsertIfAbsent(string key, BsonDocument document)
    {
        if (Rooms.ContainsKey(key)) { return false; }
        Inserts.Add((key, document));
        Rooms[key] = new RoomDetail(key, document["ShortDescription"].AsString, document["LongDescription"].AsString, document["Area"].AsString, null, null, null,
            document["Exits"].AsBsonArray.Select(e => new RoomExitData(e["Name"].AsString, e["DestinationId"].AsString)).ToList(), 0);
        return true;
    }

    public StoreUpdateOutcome DeleteIfRev(string key, int expectedRev)
    {
        if (!Rooms.TryGetValue(key, out var r)) { return StoreUpdateOutcome.Missing; }
        if (r.Rev != expectedRev) { return StoreUpdateOutcome.Conflict; }
        Rooms.Remove(key);
        Deletes.Add(key);
        return StoreUpdateOutcome.Applied;
    }

    public IReadOnlyList<RoomDetail> FindReferencing(string key) =>
        Rooms.Values.Where(r => r.Exits.Any(e => e.To == key)).ToList();
}

public class FakeAuditLog : IAuditLog
{
    public List<AuditEntry> Entries { get; } = new();
    public Dictionary<string, bool> Outcomes { get; } = new();
    public bool FailInsert { get; set; }
    public List<string> Order { get; } = new();

    public void Insert(IReadOnlyList<AuditEntry> entries)
    {
        if (FailInsert) { throw new InvalidOperationException("audit store down"); }
        Order.Add("audit");
        Entries.AddRange(entries);
        foreach (var e in entries) { Outcomes[e.Id] = false; }
    }

    public void SetOutcome(IReadOnlyList<string> ids, bool applied)
    {
        foreach (var id in ids) { Outcomes[id] = applied; }
    }
}

public class FakeAccounts : IAccountService
{
    private readonly Dictionary<string, Account> _byName = new(StringComparer.OrdinalIgnoreCase);
    public FakeAccounts Add(string name, AccountRole role) { _byName[name] = new Account(name, role, "id-" + name.ToLowerInvariant()); return this; }
    public Account? Authenticate(string name, string password) => null;
    public Account? GetByName(string name) => _byName.TryGetValue(name, out var a) ? a : null;
    public PasswordChangeResult ChangePassword(string name, string currentPassword, string newPassword, string confirmPassword) => PasswordChangeResult.SaveFailed;
}

public class RoomWriterTests
{
    private static readonly RoomActor Creator = new("id-bob", "Bob", AccountRole.Creator);

    private static (RoomWriter Writer, FakeRoomEditStore Store, FakeAuditLog Audit) Make(string? start = "t.start")
    {
        var store = new FakeRoomEditStore()
            .Add("t.start")
            .Add("t.hall", "t", 1, 2, 0, 3, ("north", "t.study"))
            .Add("t.study");
        var audit = new FakeAuditLog();
        return (new RoomWriter(store, audit, start), store, audit);
    }

    private static RoomEdit Edit(RoomDetail cur, string? shortText = null, string? longText = null, string? area = null, int? x = -99, int? y = -99, int? z = -99,
        IReadOnlyList<RoomExitData>? exits = null, int? rev = null, bool confirm = false) =>
        new(cur.Key, shortText ?? cur.Short, longText ?? cur.Long, area ?? cur.Area,
            x == -99 ? cur.X : x, y == -99 ? cur.Y : y, z == -99 ? cur.Z : z, exits ?? cur.Exits, rev ?? cur.Rev, confirm);

    // ---- save ----

    [Fact]
    public void Save_SetsOnlyChangedAllowListedFields_PlusWebUpdatedAt_AndNothingElse()
    {
        var (w, store, audit) = Make();
        var cur = store.Find("t.hall")!;
        var result = w.Save(Creator, Edit(cur, shortText: "a new hall"));

        Assert.Equal(RoomSaveStatus.Saved, result.Status);
        var (_, expectedRev, set) = Assert.Single(store.Updates);
        Assert.Equal(3, expectedRev);
        Assert.Equal(new[] { "ShortDescription", RoomWriter.UpdatedAtField }, set.Names.ToArray());
        Assert.Equal(4, store.Find("t.hall")!.Rev);
        Assert.Equal("a new hall", store.Find("t.hall")!.Short);
    }

    [Fact]
    public void Save_UpdateDocument_NeverContainsStateOrBookkeepingFields()
    {
        var (w, store, _) = Make();
        var cur = store.Find("t.hall")!;
        w.Save(Creator, Edit(cur, "s2", "l2", "t", 9, 9, 9, new[] { new RoomExitData("south", "t.start") }));

        var set = Assert.Single(store.Updates).Set;
        var allowed = RoomWriter.AllowedFields.Append(RoomWriter.UpdatedAtField).ToHashSet();
        Assert.All(set.Names, n => Assert.Contains(n, allowed));
        foreach (var forbidden in new[] { "_id", "Items", "Features", "ExtendedProperties", "Handles", "Adjectives", "Plurals", "Rev", "Occupants", "Password" })
        {
            Assert.DoesNotContain(forbidden, set.Names);
        }
    }

    [Fact]
    public void Save_NothingChanged_WritesNothingAndNoAudit()
    {
        var (w, store, audit) = Make();
        var result = w.Save(Creator, Edit(store.Find("t.hall")!));
        Assert.Equal(RoomSaveStatus.NoChange, result.Status);
        Assert.Empty(store.Updates);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public void Save_BlankCoordinates_StoreNullNotZero()
    {
        var (w, store, audit) = Make();
        var cur = store.Find("t.hall")!;
        var result = w.Save(Creator, Edit(cur, x: null, y: null, z: null));

        Assert.Equal(RoomSaveStatus.Saved, result.Status);
        var set = Assert.Single(store.Updates).Set;
        Assert.True(set["X"].IsBsonNull);
        Assert.True(set["Y"].IsBsonNull);
        Assert.True(set["Z"].IsBsonNull);
        Assert.Null(store.Find("t.hall")!.X);
        var x = audit.Entries.Single(e => e.Field == "X");
        Assert.Equal(1, x.Old.AsInt32);
        Assert.True(x.New.IsBsonNull);
    }

    [Fact]
    public void Save_ZeroCoordinate_IsStoredAsZero_NotNull()
    {
        var (w, store, _) = Make();
        var cur = store.Find("t.hall")!;
        w.Save(Creator, Edit(cur, x: 0));
        Assert.Equal(0, store.Updates.Single().Set["X"].AsInt32);
    }

    [Fact]
    public void Save_PlacingAnUnplacedRoom_SetsCoordinates()
    {
        var store = new FakeRoomEditStore().Add("t.a", "t", null, null, null);
        var w = new RoomWriter(store, new FakeAuditLog(), null);
        var result = w.Save(Creator, Edit(store.Find("t.a")!, x: 0, y: 0, z: 0));
        Assert.Equal(RoomSaveStatus.Saved, result.Status);
        Assert.Equal(0, store.Find("t.a")!.X);
    }

    // ---- audit ----

    [Fact]
    public void Save_WritesOneAuditRowPerChangedField_WithOldNewRevAndActor()
    {
        var (w, store, audit) = Make();
        var cur = store.Find("t.hall")!;
        w.Save(Creator, Edit(cur, shortText: "new short", longText: "new long"));

        Assert.Equal(new[] { "LongDescription", "ShortDescription" }, audit.Entries.Select(e => e.Field).OrderBy(f => f).ToArray());
        var row = audit.Entries.Single(e => e.Field == "ShortDescription");
        Assert.Equal("short t.hall", row.Old.AsString);
        Assert.Equal("new short", row.New.AsString);
        Assert.Equal(3, row.RevBefore);
        Assert.Equal(4, row.RevAfter);
        Assert.Equal("id-bob", row.ActorId);
        Assert.Equal("Bob", row.ActorName);
        Assert.Equal("Creator", row.ActorRole);
        Assert.Equal("room", row.Entity);
        Assert.Equal("t.hall", row.EntityId);
        Assert.All(audit.Entries, e => Assert.True(audit.Outcomes[e.Id]));
    }

    [Fact]
    public void Save_AuditIsWrittenBeforeTheRoomIsChanged()
    {
        var (w, store, audit) = Make();
        store.BeforeUpdate = _ => Assert.NotEmpty(audit.Entries);
        var cur = store.Find("t.hall")!;
        Assert.Equal(RoomSaveStatus.Saved, w.Save(Creator, Edit(cur, shortText: "x")).Status);
    }

    [Fact]
    public void Save_WhenAuditWriteFails_EditIsRefusedAndNothingChanges()
    {
        var (w, store, audit) = Make();
        audit.FailInsert = true;
        var cur = store.Find("t.hall")!;
        var result = w.Save(Creator, Edit(cur, shortText: "x"));

        Assert.Equal(RoomSaveStatus.AuditFailed, result.Status);
        Assert.Empty(store.Updates);
        Assert.Equal("short t.hall", store.Find("t.hall")!.Short);
        Assert.Equal(3, store.Find("t.hall")!.Rev);
    }

    [Fact]
    public void AuditRows_CarryNoPasswordOrStateFields()
    {
        var (w, store, audit) = Make();
        w.Save(Creator, Edit(store.Find("t.hall")!, "s", "l", "t", 5, 5, 5, new[] { new RoomExitData("up", "t.start") }));
        var allowed = RoomWriter.AllowedFields.ToHashSet();
        Assert.All(audit.Entries, e => Assert.Contains(e.Field, allowed));
        Assert.DoesNotContain(audit.Entries, e => e.Field.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    // ---- conflicts ----

    [Fact]
    public void Save_StaleRev_IsAConflict_NoWrite_NoAudit_CurrentValuesReturned()
    {
        var (w, store, audit) = Make();
        var cur = store.Find("t.hall")!;
        var result = w.Save(Creator, Edit(cur, shortText: "mine", rev: 2));

        Assert.Equal(RoomSaveStatus.Conflict, result.Status);
        Assert.Equal(3, result.Current!.Rev);
        Assert.Empty(store.Updates);
        Assert.Empty(audit.Entries);
        Assert.Equal("short t.hall", store.Find("t.hall")!.Short);
    }

    [Fact]
    public void Save_RaceBetweenReadAndWrite_IsAConflict_AndAuditIsMarkedNotApplied()
    {
        var (w, store, audit) = Make();
        store.BeforeUpdate = key => store.Rooms[key] = store.Rooms[key] with { Rev = 4, Short = "someone else" };
        var cur = store.Find("t.hall")!;
        var result = w.Save(Creator, Edit(cur, shortText: "mine"));

        Assert.Equal(RoomSaveStatus.Conflict, result.Status);
        Assert.Equal("someone else", store.Find("t.hall")!.Short);
        Assert.All(audit.Entries, e => Assert.False(audit.Outcomes[e.Id]));
    }

    [Fact]
    public void Save_RoomDeletedMeanwhile_IsNotFound()
    {
        var (w, store, _) = Make();
        var cur = store.Find("t.hall")!;
        store.Rooms.Remove("t.hall");
        Assert.Equal(RoomSaveStatus.NotFound, w.Save(Creator, Edit(cur, shortText: "x")).Status);
    }

    // ---- validation ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Save_EmptyShort_IsInvalid(string text)
    {
        var (w, store, _) = Make();
        var r = w.Save(Creator, Edit(store.Find("t.hall")!, shortText: text));
        // Edit() treats "" as a real value; whitespace normalises to empty.
        Assert.Equal(RoomSaveStatus.Invalid, r.Status);
        Assert.Contains(r.FieldErrors, e => e.Field == "Short");
        Assert.Empty(store.Updates);
    }

    [Fact]
    public void Save_LengthLimits_MatchTheSharedRules()
    {
        var (w, store, _) = Make();
        var cur = store.Find("t.hall")!;
        var r = w.Save(Creator, Edit(cur, shortText: new string('x', MUD.Data.RoomRules.MaxShort + 1), longText: new string('x', MUD.Data.RoomRules.MaxLong + 1)));
        Assert.Equal(RoomSaveStatus.Invalid, r.Status);
        Assert.Contains(r.FieldErrors, e => e.Field == "Short");
        Assert.Contains(r.FieldErrors, e => e.Field == "Long");
    }

    [Fact]
    public void Save_ControlCharactersAreRejected()
    {
        var (w, store, _) = Make();
        var r = w.Save(Creator, Edit(store.Find("t.hall")!, shortText: "bad\u0007bell"));
        Assert.Equal(RoomSaveStatus.Invalid, r.Status);
    }

    [Fact]
    public void Save_BadAreaPrefix_AndAreaNotMatchingKey_AreRejected()
    {
        var (w, store, _) = Make();
        var cur = store.Find("t.hall")!;
        Assert.Contains(w.Save(Creator, Edit(cur, area: "Bad Area")).FieldErrors, e => e.Field == "Area");
        Assert.Contains(w.Save(Creator, Edit(cur, area: "other")).FieldErrors, e => e.Field == "Area");
    }

    [Theory]
    [InlineData("go-to 1", "t.study", "Exits[0].Dir")]
    [InlineData("north", "Not A Key", "Exits[0].To")]
    [InlineData("", "t.study", "Exits[0].Dir")]
    [InlineData("north", "", "Exits[0].To")]
    public void Save_BadExitRows_AreRejected(string dir, string to, string field)
    {
        var (w, store, _) = Make();
        var r = w.Save(Creator, Edit(store.Find("t.hall")!, exits: new[] { new RoomExitData(dir, to) }));
        Assert.Equal(RoomSaveStatus.Invalid, r.Status);
        Assert.Contains(r.FieldErrors, e => e.Field == field);
    }

    [Fact]
    public void Save_DuplicateExitDirection_IsRejectedCaseInsensitively()
    {
        var (w, store, _) = Make();
        var r = w.Save(Creator, Edit(store.Find("t.hall")!, exits: new[] { new RoomExitData("north", "t.study"), new RoomExitData("North", "t.start") }));
        Assert.Equal(RoomSaveStatus.Invalid, r.Status);
    }

    [Fact]
    public void Save_DanglingExit_NeedsConfirmation_ThenIsSaved()
    {
        var (w, store, audit) = Make();
        var cur = store.Find("t.hall")!;
        var exits = new[] { new RoomExitData("north", "t.study"), new RoomExitData("east", "t.nowhere") };

        var refused = w.Save(Creator, Edit(cur, exits: exits));
        Assert.Equal(RoomSaveStatus.Invalid, refused.Status);
        Assert.Contains(refused.FieldErrors, e => e.Field == "ConfirmDangling" && e.Message.Contains("t.nowhere"));
        Assert.Empty(store.Updates);

        var saved = w.Save(Creator, Edit(cur, exits: exits, confirm: true));
        Assert.Equal(RoomSaveStatus.Saved, saved.Status);
        Assert.Contains(store.Find("t.hall")!.Exits, e => e.To == "t.nowhere");
    }

    [Fact]
    public void Save_ExitBackToTheSameRoom_IsNotDangling()
    {
        var (w, store, _) = Make();
        var r = w.Save(Creator, Edit(store.Find("t.hall")!, exits: new[] { new RoomExitData("in", "t.hall") }));
        Assert.Equal(RoomSaveStatus.Saved, r.Status);
    }

    [Fact]
    public void Save_ExitRowsThatAreCompletelyBlankAreIgnored()
    {
        var (w, store, _) = Make();
        var r = w.Save(Creator, Edit(store.Find("t.hall")!, exits: new[] { new RoomExitData("north", "t.study"), new RoomExitData("", "") }));
        Assert.Equal(RoomSaveStatus.NoChange, r.Status);
    }

    // ---- authorization at the writer ----

    [Fact]
    public void Save_PlayerRole_IsForbidden_NoWrite()
    {
        var (w, store, audit) = Make();
        var r = w.Save(new RoomActor("id-p", "Pat", AccountRole.Player), Edit(store.Find("t.hall")!, shortText: "x"));
        Assert.Equal(RoomSaveStatus.Forbidden, r.Status);
        Assert.Empty(store.Updates);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public void Create_And_Delete_RequireCreatorRole()
    {
        var (w, store, _) = Make();
        var player = new RoomActor("id-p", "Pat", AccountRole.Player);
        Assert.Equal(RoomSaveStatus.Forbidden, w.Create(player, new RoomEdit("t.new", "s", "l", "t", null, null, null, Array.Empty<RoomExitData>(), 0, false)).Status);
        Assert.Equal(RoomSaveStatus.Forbidden, w.Delete(player, "t.study", 0).Status);
        Assert.False(store.Rooms.ContainsKey("t.new"));
        Assert.True(store.Rooms.ContainsKey("t.study"));
    }

    // ---- create ----

    [Fact]
    public void Create_InsertsWithSetOnInsertShape_UnplacedAndEmptyState_AndAudits()
    {
        var (w, store, audit) = Make();
        var r = w.Create(Creator, new RoomEdit("t.new", "a new room", "Long text.", "t", null, null, null, new[] { new RoomExitData("north", "t.hall") }, 0, false));

        Assert.Equal(RoomSaveStatus.Created, r.Status);
        var (_, doc) = Assert.Single(store.Inserts);
        Assert.True(doc["X"].IsBsonNull);
        Assert.True(doc["Y"].IsBsonNull);
        Assert.True(doc["Z"].IsBsonNull);
        Assert.Empty(doc["Items"].AsBsonArray);
        Assert.Empty(doc["Features"].AsBsonArray);
        Assert.Equal(0, doc["Rev"].AsInt32);
        Assert.Equal("t", doc["Area"].AsString);
        Assert.NotEmpty(audit.Entries);
        Assert.All(audit.Entries, e => Assert.Equal("t.new", e.EntityId));
        Assert.All(audit.Entries, e => Assert.True(audit.Outcomes[e.Id]));
    }

    [Fact]
    public void Create_ExistingKey_Fails_AndWritesNothing()
    {
        var (w, store, audit) = Make();
        var r = w.Create(Creator, new RoomEdit("t.hall", "s", "l", "t", null, null, null, Array.Empty<RoomExitData>(), 0, false));
        Assert.Equal(RoomSaveStatus.KeyExists, r.Status);
        Assert.Empty(store.Inserts);
        Assert.Empty(audit.Entries);
        Assert.Equal("short t.hall", store.Find("t.hall")!.Short);
    }

    [Fact]
    public void Create_RaceOnKey_IsKeyExists_AndAuditIsMarkedNotApplied()
    {
        var (w, store, audit) = Make();
        var racing = new RacingStore(store);
        var writer = new RoomWriter(racing, audit, null);
        var r = writer.Create(Creator, new RoomEdit("t.new", "s", "l", "t", null, null, null, Array.Empty<RoomExitData>(), 0, false));
        Assert.Equal(RoomSaveStatus.KeyExists, r.Status);
        Assert.All(audit.Entries, e => Assert.False(audit.Outcomes[e.Id]));
    }

    // Reports "absent" on the first Exists check, then refuses the insert as if another request won.
    private class RacingStore : IRoomEditStore
    {
        private readonly FakeRoomEditStore _inner;
        public RacingStore(FakeRoomEditStore inner) { _inner = inner; }
        public RoomDetail? Find(string key) => _inner.Find(key);
        public bool Exists(string key) => key != "t.new" && _inner.Exists(key);
        public StoreUpdateOutcome UpdateIfRev(string key, int expectedRev, BsonDocument set) => _inner.UpdateIfRev(key, expectedRev, set);
        public bool InsertIfAbsent(string key, BsonDocument document) => false;
        public StoreUpdateOutcome DeleteIfRev(string key, int expectedRev) => _inner.DeleteIfRev(key, expectedRev);
        public IReadOnlyList<RoomDetail> FindReferencing(string key) => _inner.FindReferencing(key);
    }

    [Theory]
    [InlineData("hall", "t")]            // no area prefix in the key
    [InlineData("t.Hall", "t")]          // uppercase
    [InlineData("t.hall", "u")]          // area does not match key
    public void Create_InvalidKeyOrArea_IsRejected(string key, string area)
    {
        var (w, store, _) = Make();
        var r = w.Create(Creator, new RoomEdit(key, "s", "l", area, null, null, null, Array.Empty<RoomExitData>(), 0, false));
        Assert.Equal(RoomSaveStatus.Invalid, r.Status);
        Assert.Empty(store.Inserts);
    }

    [Fact]
    public void Create_DanglingExit_NeedsConfirmation()
    {
        var (w, store, _) = Make();
        var edit = new RoomEdit("t.new", "s", "l", "t", null, null, null, new[] { new RoomExitData("north", "t.nope") }, 0, false);
        Assert.Equal(RoomSaveStatus.Invalid, w.Create(Creator, edit).Status);
        Assert.Equal(RoomSaveStatus.Created, w.Create(Creator, edit with { ConfirmDangling = true }).Status);
    }

    // ---- delete ----

    [Fact]
    public void Delete_DefaultRoom_IsRefused()
    {
        var (w, store, audit) = Make("t.start");
        var r = w.Delete(Creator, "t.start", 0);
        Assert.Equal(RoomSaveStatus.Refused, r.Status);
        Assert.True(store.Rooms.ContainsKey("t.start"));
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public void Delete_WithNoConfiguredDefaultRoom_IsRefused()
    {
        var (w, store, _) = Make(start: null);
        Assert.Equal(RoomSaveStatus.Refused, w.Delete(Creator, "t.study", 0).Status);
        Assert.True(store.Rooms.ContainsKey("t.study"));
    }

    [Fact]
    public void Delete_NoUsableExitAndDefaultRoomMissing_IsRefused_NothingDeleted()
    {
        var store = new FakeRoomEditStore().Add("t.lonely");
        var w = new RoomWriter(store, new FakeAuditLog(), "t.start-missing");
        var r = w.Delete(Creator, "t.lonely", 0);
        Assert.Equal(RoomSaveStatus.Refused, r.Status);
        Assert.True(store.Rooms.ContainsKey("t.lonely"));
        Assert.Empty(store.Deletes);
    }

    [Fact]
    public void Delete_RemovesExitsInOtherRoomsThatPointAtIt_AndAuditsThem()
    {
        var store = new FakeRoomEditStore()
            .Add("t.start")
            .Add("t.a", "t", 0, 0, 0, 0, ("east", "t.b"), ("west", "t.start"))
            .Add("t.b", "t", 1, 0, 0, 2, ("west", "t.a"), ("south", "t.start"));
        var audit = new FakeAuditLog();
        var w = new RoomWriter(store, audit, "t.start");

        var r = w.Delete(Creator, "t.b", 2);

        Assert.Equal(RoomSaveStatus.Deleted, r.Status);
        Assert.False(store.Rooms.ContainsKey("t.b"));
        Assert.Equal(new[] { "west" }, store.Find("t.a")!.Exits.Select(e => e.Name).ToArray());
        var removed = audit.Entries.Single(e => e.EntityId == "t.a" && e.Field == "Exits");
        Assert.Contains("t.b", removed.Old.ToJson());
        Assert.DoesNotContain("t.b", removed.New.ToJson());
        Assert.All(audit.Entries, e => Assert.True(audit.Outcomes[e.Id]));
        Assert.Contains(audit.Entries, e => e.EntityId == "t.b" && e.Field == "ShortDescription" && e.New.IsBsonNull);
    }

    [Fact]
    public void Delete_StaleRev_IsAConflict()
    {
        var (w, store, _) = Make();
        var r = w.Delete(Creator, "t.hall", 1);
        Assert.Equal(RoomSaveStatus.Conflict, r.Status);
        Assert.True(store.Rooms.ContainsKey("t.hall"));
    }

    [Fact]
    public void Delete_WhenAuditFails_NothingIsDeleted()
    {
        var (w, store, audit) = Make();
        audit.FailInsert = true;
        Assert.Equal(RoomSaveStatus.AuditFailed, w.Delete(Creator, "t.hall", 3).Status);
        Assert.True(store.Rooms.ContainsKey("t.hall"));
    }

    // ---- errors are generic ----

    [Fact]
    public void StoreFailure_IsAGenericResult_NotTheExceptionText()
    {
        var w = new RoomWriter(new ThrowingStore(), new FakeAuditLog(), null);
        var r = w.Save(Creator, new RoomEdit("t.a", "s", "l", "t", null, null, null, Array.Empty<RoomExitData>(), 0, false));
        Assert.Equal(RoomSaveStatus.Failed, r.Status);
        Assert.Null(r.Message);
    }

    private class ThrowingStore : IRoomEditStore
    {
        public RoomDetail? Find(string key) => throw new InvalidOperationException("mongodb://secret@host");
        public bool Exists(string key) => throw new InvalidOperationException("x");
        public StoreUpdateOutcome UpdateIfRev(string key, int expectedRev, BsonDocument set) => throw new InvalidOperationException("x");
        public bool InsertIfAbsent(string key, BsonDocument document) => throw new InvalidOperationException("x");
        public StoreUpdateOutcome DeleteIfRev(string key, int expectedRev) => throw new InvalidOperationException("x");
        public IReadOnlyList<RoomDetail> FindReferencing(string key) => throw new InvalidOperationException("x");
    }
}

public class RoomCreationServiceTests
{
    private static (RoomCreationService Service, FakeRoomEditStore Store) Make(AccountRole role = AccountRole.Creator)
    {
        var store = new FakeRoomEditStore().Add("t.hall");
        var writer = new RoomWriter(store, new FakeAuditLog(), null);
        var accounts = new FakeAccounts().Add("Bob", role);
        return (new RoomCreationService(writer, accounts), store);
    }

    private static RoomForm Form(string key = "new", string area = "t") => new()
    {
        Key = key, Short = "a new room", Long = "Long.", Area = area,
        Exits = { new ExitInput { Dir = "north", To = "t.hall" }, new ExitInput() },
    };

    [Fact]
    public void Create_StoresAreaDotKey_AndReturnsIt()
    {
        var (svc, store) = Make();
        var r = svc.Create(Form(), "Bob");
        Assert.Equal(RoomCreationStatus.Created, r.Status);
        Assert.Equal("t.new", r.CreatedKey);
        Assert.True(store.Rooms.ContainsKey("t.new"));
    }

    [Fact]
    public void Create_ExistingKey_Fails()
    {
        var (svc, store) = Make();
        Assert.Equal(RoomCreationStatus.KeyExists, svc.Create(Form("hall"), "Bob").Status);
        Assert.Empty(store.Inserts);
    }

    [Fact]
    public void Create_UsesTheCurrentStoredRole_NotAnythingTheCallerClaims()
    {
        var (svc, store) = Make(AccountRole.Player); // demoted since login
        Assert.Equal(RoomCreationStatus.Forbidden, svc.Create(Form(), "Bob").Status);
        Assert.Empty(store.Inserts);
    }

    [Fact]
    public void Create_UnknownUser_IsForbidden()
    {
        var (svc, _) = Make();
        Assert.Equal(RoomCreationStatus.Forbidden, svc.Create(Form(), "Nobody").Status);
    }

    [Fact]
    public void Create_InvalidLocalKey_IsRejected()
    {
        var (svc, store) = Make();
        var r = svc.Create(Form("Bad Key"), "Bob");
        Assert.Equal(RoomCreationStatus.Invalid, r.Status);
        Assert.Contains(r.FieldErrors, e => e.Field == "Key");
        Assert.Empty(store.Inserts);
    }
}

public class RoomValidatorParityTests
{
    // The importer and the website both call MUD.Data.RoomRules; this table pins the verdicts so a change in one place
    // shows up in both (the importer side is covered by RoomRulesCharacterizationTests in MUD.Tests).
    [Theory]
    [InlineData("hall", true)]
    [InlineData("hall_2", true)]
    [InlineData("Hall", false)]
    [InlineData("a.b", false)]
    [InlineData("", false)]
    public void LocalKey(string key, bool ok) => Assert.Equal(ok, MUD.Data.RoomRules.CheckLocalKey(key) == null);

    [Theory]
    [InlineData("t", true)]
    [InlineData("a.b.c", true)]
    [InlineData("T", false)]
    [InlineData("a..b", false)]
    public void AreaPrefix(string prefix, bool ok) => Assert.Equal(ok, MUD.Data.RoomRules.CheckAreaPrefix(prefix) == null);

    [Fact]
    public void WebPagesDoNotReferenceMudCore()
    {
        var referenced = typeof(RoomWriter).Assembly.GetReferencedAssemblies().Select(a => a.Name);
        Assert.DoesNotContain("MUD.Core", referenced);
    }
}

public class RoomEditPageTests
{
    private static RoomEditModel Page(string role, FakeRoomEditStore? store = null, FakeAuditLog? audit = null, string? name = "Bob")
    {
        store ??= new FakeRoomEditStore().Add("t.start").Add("t.hall", "t", 1, 1, 0, 2, ("north", "t.start"));
        audit ??= new FakeAuditLog();
        var accounts = new FakeAccounts().Add("Bob", Enum.Parse<AccountRole>(role));
        var model = new RoomEditModel(new RoomWriter(store, audit, "t.start"), store, accounts);
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(name == null ? Array.Empty<Claim>() : new[] { new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.Role, role) }, "test"));
        model.PageContext = new PageContext { HttpContext = http };
        return model;
    }

    [Fact]
    public void Page_RequiresCreatorOrAdminPolicy()
    {
        var attr = (AuthorizeAttribute?)Attribute.GetCustomAttribute(typeof(RoomEditModel), typeof(AuthorizeAttribute));
        Assert.NotNull(attr);
        Assert.Equal(WebPolicies.CreatorOrAdmin, attr!.Policy);
    }

    [Fact]
    public void Page_RouteIsCreatorRoomsKey()
    {
        var path = typeof(RoomEditModel).Assembly.GetCustomAttributes(false); // route lives in the .cshtml directive
        var cshtml = File.ReadAllText(FindFile("MUD.Web/Pages/RoomEdit.cshtml"));
        Assert.StartsWith("@page \"/creator/rooms/{key}\"", cshtml);
    }

    [Fact]
    public void Page_FormsHaveAntiforgery()
    {
        var cshtml = File.ReadAllText(FindFile("MUD.Web/Pages/RoomEdit.cshtml"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(cshtml, "<form method=\"post\"").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(cshtml, "Html.AntiForgeryToken\\(\\)").Count);
    }

    [Fact]
    public void Page_EditModelDoesNotBindTheKey_OrAnyStateOrPasswordField()
    {
        var bound = typeof(RoomEditModel).GetProperties()
            .Where(p => p.GetCustomAttributes(typeof(BindPropertyAttribute), true).Any()).Select(p => p.Name).ToHashSet();
        Assert.DoesNotContain("Key", bound);
        Assert.DoesNotContain("Items", bound);
        Assert.DoesNotContain("Features", bound);
        Assert.DoesNotContain("Password", bound);
        Assert.Equal(new[] { "Area", "ConfirmDangling", "Exits", "Long", "Rev", "Short", "X", "Y", "Z" }, bound.OrderBy(b => b).ToArray());
    }

    [Fact]
    public void Get_UnknownRoom_Is404()
    {
        var model = Page("Creator");
        var result = model.OnGet("t.nope");
        Assert.IsType<PageResult>(result);
        Assert.Equal(404, model.Response.StatusCode);
        Assert.True(model.Missing);
    }

    [Fact]
    public void Get_LoadsValuesAndRev()
    {
        var model = Page("Creator");
        model.OnGet("t.hall");
        Assert.Equal("short t.hall", model.Short);
        Assert.Equal(2, model.Rev);
        Assert.Equal(1, model.X);
        Assert.Single(model.Exits);
    }

    [Fact]
    public void Save_Success_ReloadsNewRev_AndShowsNotice()
    {
        var model = Page("Creator");
        model.OnGet("t.hall");
        model.Short = "edited";
        model.OnPostSave("t.hall");
        Assert.Equal("edited", model.Short);
        Assert.Equal(3, model.Rev);
        Assert.NotNull(model.Notice);
        Assert.Null(model.Error);
    }

    [Fact]
    public void Save_BlankX_BindsAsNull_AndStoresUnplaced()
    {
        var store = new FakeRoomEditStore().Add("t.start").Add("t.hall", "t", 1, 1, 0, 0);
        var model = Page("Creator", store);
        model.OnGet("t.hall");
        model.X = null; model.Y = null; model.Z = null;
        model.OnPostSave("t.hall");
        Assert.Null(store.Find("t.hall")!.X);
        Assert.Null(store.Find("t.hall")!.Z);
    }

    [Fact]
    public void Save_Conflict_ShowsFixedMessage_KeepsUserInput_AndCurrentValues()
    {
        var store = new FakeRoomEditStore().Add("t.start").Add("t.hall", "t", 1, 1, 0, 2);
        var model = Page("Creator", store);
        model.OnGet("t.hall");
        model.Short = "my edit";
        store.Rooms["t.hall"] = store.Rooms["t.hall"] with { Rev = 3, Short = "their edit" };

        model.OnPostSave("t.hall");

        Assert.True(model.Conflict);
        Assert.Equal(RoomEditModel.ConflictError, model.Error);
        Assert.Equal("my edit", model.Short);
        Assert.Equal("their edit", model.Current!.Short);
        Assert.Equal(3, model.Rev); // next save is against the current revision, after the user has reviewed
        Assert.Equal("their edit", store.Find("t.hall")!.Short);
    }

    [Fact]
    public void Save_AsPlayerRole_WritesNothing()
    {
        var store = new FakeRoomEditStore().Add("t.start").Add("t.hall", "t", 1, 1, 0, 0);
        var model = Page("Player", store);
        model.Short = "x"; model.Long = "y"; model.Area = "t";
        model.OnPostSave("t.hall");
        Assert.Empty(store.Updates);
        Assert.Equal(RoomEditModel.ForbiddenError, model.Error);
    }

    [Fact]
    public void Save_UnknownUser_WritesNothing()
    {
        var store = new FakeRoomEditStore().Add("t.start").Add("t.hall", "t", 1, 1, 0, 0);
        var model = Page("Creator", store, name: "Ghost");
        model.Short = "x";
        model.OnPostSave("t.hall");
        Assert.Empty(store.Updates);
    }

    [Fact]
    public void Save_FormCannotRedirectTheSaveToAnotherRoom()
    {
        var store = new FakeRoomEditStore().Add("t.start").Add("t.hall", "t", 1, 1, 0, 0).Add("t.other", "t", 2, 2, 0, 0);
        var model = Page("Creator", store);
        model.Short = "x"; model.Long = "y"; model.Area = "t"; model.X = 1; model.Y = 1; model.Z = 0;
        model.OnPostSave("t.hall");
        Assert.All(store.Updates, u => Assert.Equal("t.hall", u.Key));
        Assert.Equal("short t.other", store.Find("t.other")!.Short);
    }

    [Fact]
    public void ErrorMessages_AreFixedStrings_WithNoPlaceholdersForInput()
    {
        foreach (var s in new[] { RoomEditModel.ConflictError, RoomEditModel.NotFoundError, RoomEditModel.FailedError, RoomEditModel.AuditFailedError, RoomEditModel.ForbiddenError })
        {
            Assert.DoesNotContain("{", s);
        }
    }

    [Fact]
    public void AddExit_And_RemoveExit_ChangeTheFormOnly_NoWrite()
    {
        var store = new FakeRoomEditStore().Add("t.start").Add("t.hall", "t", 1, 1, 0, 0, ("north", "t.start"));
        var model = Page("Creator", store);
        model.OnGet("t.hall");
        model.OnPostAddExit("t.hall");
        Assert.Equal(2, model.Exits.Count);
        model.OnPostRemoveExit("t.hall", 0);
        Assert.Single(model.Exits);
        Assert.Empty(store.Updates);
    }

    [Fact]
    public void Delete_DefaultRoom_ShowsRefusalAndKeepsTheRoom()
    {
        var store = new FakeRoomEditStore().Add("t.start");
        var model = Page("Creator", store);
        model.Rev = 0;
        model.OnPostDelete("t.start");
        Assert.NotNull(model.Error);
        Assert.True(store.Rooms.ContainsKey("t.start"));
    }

    [Fact]
    public void Delete_Success_RedirectsToTheMap()
    {
        var store = new FakeRoomEditStore().Add("t.start").Add("t.hall", "t", 1, 1, 0, 0, ("north", "t.start"));
        var model = Page("Creator", store);
        model.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(model.HttpContext, new NoTempData());
        model.Rev = 0;
        var result = model.OnPostDelete("t.hall");
        Assert.Equal("/creator/map", Assert.IsType<RedirectResult>(result).Url);
        Assert.False(store.Rooms.ContainsKey("t.hall"));
    }

    private class NoTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    [Fact]
    public void RoomUrl_EscapesTheKey_SoItCannotBreakOutOfTheLink()
    {
        Assert.Equal("/creator/rooms/t.hall", RoomEditModel.RoomUrl("t.hall"));
        Assert.DoesNotContain("\"", RoomEditModel.RoomUrl("a\"b<c>"));
        Assert.DoesNotContain("<", RoomEditModel.RoomUrl("a\"b<c>"));
    }

    [Fact]
    public void RoomEditPage_Source_UsesNoRawHtmlOutput()
    {
        var cshtml = File.ReadAllText(FindFile("MUD.Web/Pages/RoomEdit.cshtml"));
        Assert.DoesNotContain("Html.Raw", cshtml);
        Assert.DoesNotContain("@Html.Raw", cshtml);
        var creator = File.ReadAllText(FindFile("MUD.Web/Pages/Creator.cshtml"));
        Assert.DoesNotContain("Html.Raw", creator);
        Assert.DoesNotContain("not implemented", creator, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MUD.sln"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
    }
}
