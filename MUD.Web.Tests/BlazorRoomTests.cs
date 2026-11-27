using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using MUD.Contracts;
using MUD.Web.Pages;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

// The room pages of the Blazor UI (WEB_BLAZOR.md 4.1): RoomEditPageTests, CreatorPageTests, CreatorMapPageTests and CreatorMapColourTests
// ported from the Razor page models to the presenters. Same arrangement over FakeAdminClient, same expected sentences and calls.
// HTTP status codes become RoomEditMissing values (WEB_BLAZOR.md 7.3).
public class RoomEditPresenterTests
{
    private const string Token = "token-1";

    private static (RoomEditPresenter P, FakeAdminClient Core) Make(string key, FakeAdminClient? core = null)
    {
        core ??= new FakeAdminClient().AddRoom("t.start").AddRoom("t.hall", "t", 1, 1, 0, ("north", "t.start"));
        return (new RoomEditPresenter(core, Token, key), core);
    }

    [Fact]
    public async Task Load_UnknownRoom_IsMissingNotFound()
    {
        var (p, _) = Make("t.nope");
        Assert.Equal(RoomEditOutcome.Missing, await p.LoadAsync());
        Assert.Equal(RoomEditMissing.NotFound, p.Missing);
        Assert.Equal(Messages.RoomNotFound, p.Error);
    }

    [Fact]
    public async Task Load_LoadsValues_AndSendsTheCircuitToken()
    {
        var (p, core) = Make("t.hall");
        await p.LoadAsync();
        Assert.Equal("short t.hall", p.Short);
        Assert.Equal("long t.hall", p.Long);
        Assert.Equal(1, p.X);
        Assert.Single(p.Exits);
        Assert.Equal(new[] { Token }, core.RoomTokens);
    }

    [Fact]
    public async Task Load_UnplacedRoom_LoadsBlankCoordinates()
    {
        var (p, _) = Make("t.hall", new FakeAdminClient().AddRoom("t.hall", "t", null, null, null));
        await p.LoadAsync();
        Assert.Null(p.X); Assert.Null(p.Y); Assert.Null(p.Z);
    }

    [Fact]
    public async Task Load_WhenCoreIsUnavailable_IsMissingWithAFixedMessage()
    {
        var (p, _) = Make("t.hall", new FakeAdminClient { Unavailable = true });
        await p.LoadAsync();
        Assert.Equal(RoomEditMissing.Unavailable, p.Missing);
        Assert.Equal(Messages.Unavailable, p.Error);
    }

    [Fact]
    public async Task Load_WhenCoreRefusesTheSession_IsMissingForbidden()
    {
        var (p, _) = Make("t.hall", new FakeAdminClient { RefuseRoomCalls = true });
        await p.LoadAsync();
        Assert.Equal(RoomEditMissing.Forbidden, p.Missing);
        Assert.Equal(Messages.RoomForbidden, p.Error);
    }

    [Fact]
    public async Task Save_Success_SendsTheWholeForm_ReloadsFromCoreReply_AndShowsNotice()
    {
        var (p, core) = Make("t.hall");
        await p.LoadAsync();
        p.Short = "edited";
        Assert.Equal(RoomEditOutcome.Ok, await p.SaveAsync());
        var sent = Assert.Single(core.RoomUpdates);
        Assert.Equal("t.hall", sent.Key);
        Assert.Equal("edited", sent.ShortDescription);
        Assert.Equal("long t.hall", sent.LongDescription);
        Assert.Equal(1, sent.X);
        Assert.Equal(new[] { Token }, core.RoomTokens.Distinct());
        Assert.Equal("edited", p.Short);
        Assert.Equal("Saved. The running game has the change now.", p.Notice);
        Assert.Null(p.Error);
    }

    [Fact]
    public async Task Save_BlankCoordinates_AreSentAsAbsent()
    {
        var (p, core) = Make("t.hall", new FakeAdminClient().AddRoom("t.hall", "t", 1, 1, 0));
        await p.LoadAsync();
        p.X = null; p.Y = null; p.Z = null;
        await p.SaveAsync();
        var sent = Assert.Single(core.RoomUpdates);
        Assert.False(sent.HasX); Assert.False(sent.HasY); Assert.False(sent.HasZ);
        Assert.Null(p.X);
    }

    [Fact]
    public async Task Save_TwoEditsInARow_BothApply_LastWriteWins()
    {
        var (p, core) = Make("t.hall");
        await p.LoadAsync();
        p.Short = "first";
        await p.SaveAsync();
        p.Short = "second";
        await p.SaveAsync();
        Assert.Equal(2, core.RoomUpdates.Count);
        Assert.Equal("second", core.Rooms["t.hall"].ShortDescription);
        Assert.Null(p.Error);
    }

    [Fact]
    public async Task Save_NoChange_ShowsNotice()
    {
        var (p, _) = Make("t.hall", new FakeAdminClient { RoomEditResult = AdminResult.NoChange }.AddRoom("t.hall"));
        await p.LoadAsync();
        await p.SaveAsync();
        Assert.Equal("Nothing was changed.", p.Notice);
    }

    [Fact]
    public async Task Save_Invalid_ShowsCoreFieldErrors_AndKeepsTheUsersInput()
    {
        var core = new FakeAdminClient { RoomEditResult = AdminResult.Invalid }.AddRoom("t.hall");
        core.RoomEditErrors.Add(new FieldError { Field = "Short", Message = "Short is too long." });
        var (p, _) = Make("t.hall", core);
        p.Short = "my input";
        Assert.Equal(RoomEditOutcome.Problem, await p.SaveAsync());
        Assert.Contains("Short is too long.", p.Problems);
        Assert.Equal("my input", p.Short);
    }

    [Theory]
    [InlineData(AdminResult.AuditFailed, Messages.RoomAuditFailed)]
    [InlineData(AdminResult.Forbidden, Messages.RoomForbidden)]
    [InlineData(AdminResult.Failed, Messages.RoomSaveFailed)]
    public async Task Save_OtherResults_ShowFixedMessages(AdminResult result, string expected)
    {
        var (p, _) = Make("t.hall", new FakeAdminClient { RoomEditResult = result }.AddRoom("t.hall"));
        await p.SaveAsync();
        Assert.Equal(expected, p.Error);
    }

    [Fact]
    public async Task Save_NotFound_IsMissing()
    {
        var (p, _) = Make("t.gone", new FakeAdminClient { RoomEditResult = AdminResult.NotFound });
        await p.SaveAsync();
        Assert.Equal(RoomEditMissing.NotFound, p.Missing);
    }

    [Fact]
    public async Task Save_WhenCoreRefusesTheRole_ShowsForbidden_AndNothingIsWritten()
    {
        var (p, core) = Make("t.hall", new FakeAdminClient { RefuseRoomCalls = true }.AddRoom("t.hall"));
        p.Short = "x";
        await p.SaveAsync();
        Assert.Equal(Messages.RoomForbidden, p.Error);
        Assert.Empty(core.RoomUpdates);
    }

    [Fact]
    public async Task Save_WhenCoreIsUnavailable_ShowsAFixedMessage_AndKeepsTheInput()
    {
        var (p, _) = Make("t.hall", new FakeAdminClient { Unavailable = true });
        p.Short = "typed";
        await p.SaveAsync();
        Assert.Equal(Messages.Unavailable, p.Error);
        Assert.Equal("typed", p.Short);
    }

    [Fact]
    public async Task Save_AlwaysTargetsTheRouteKey()
    {
        var (p, core) = Make("t.hall", new FakeAdminClient().AddRoom("t.hall").AddRoom("t.other", "t", 2, 2, 0));
        p.Short = "x"; p.Long = "y"; p.Area = "t";
        await p.SaveAsync();
        Assert.All(core.RoomUpdates, u => Assert.Equal("t.hall", u.Key));
        Assert.Equal("short t.other", core.Rooms["t.other"].ShortDescription);
    }

    [Fact]
    public void ThePresenter_HasNoSetterForTheKey_AndNoRevisionOrPasswordState()
    {
        var props = typeof(RoomEditPresenter).GetProperties();
        Assert.Null(props.Single(x => x.Name == "Key").SetMethod);
        Assert.False(props.Single(x => x.Name == "GenMap").SetMethod?.IsPublic ?? false);
        Assert.False(props.Single(x => x.Name == "GenTerrain").SetMethod?.IsPublic ?? false);
        Assert.DoesNotContain(props, x => x.Name is "Rev" or "Password" or "Items");
        Assert.Equal(new[] { "Area", "ConfirmDangling", "Exits", "Features", "Long", "NewArea", "NewLocalKey", "Notice", "Short", "X", "Y", "Z" },
            props.Where(x => x.SetMethod?.IsPublic == true).Select(x => x.Name).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Save_MoreExitRowsThanTheLimit_AreCut()
    {
        var (p, core) = Make("t.hall", new FakeAdminClient().AddRoom("t.hall"));
        p.Exits = Enumerable.Range(0, RoomEditPresenter.MaxFormExits + 5).Select(i => new ExitInput { Dir = "d" + i, To = "t.x" }).ToList();
        await p.SaveAsync();
        Assert.Equal(RoomEditPresenter.MaxFormExits, Assert.Single(core.RoomUpdates).Exits.Count);
    }

    [Fact]
    public void ErrorMessages_AreFixedStrings_WithNoPlaceholdersForInput()
    {
        foreach (var s in new[] { Messages.RoomNotFound, Messages.RoomSaveFailed, Messages.RoomAuditFailed, Messages.RoomForbidden, Messages.Unavailable })
        {
            Assert.DoesNotContain("{", s);
        }
    }

    [Fact]
    public async Task AddExit_And_RemoveExit_ChangeTheFormOnly_NoWrite()
    {
        var (p, core) = Make("t.hall", new FakeAdminClient().AddRoom("t.hall", "t", 1, 1, 0, ("north", "t.start")));
        await p.LoadAsync();
        var calls = core.RoomTokens.Count;
        p.AddExit();
        Assert.Equal(2, p.Exits.Count);
        p.RemoveExit(0);
        Assert.Single(p.Exits);
        p.RemoveExit(9); // out of range: ignored
        Assert.Single(p.Exits);
        Assert.Empty(core.RoomUpdates); Assert.Empty(core.RoomCreates); Assert.Empty(core.RoomDeletes);
        Assert.Equal(calls, core.RoomTokens.Count); // the row edits do not call core at all
    }

    [Fact]
    public async Task AddExit_StopsAtTheLimit()
    {
        var (p, _) = Make("t.hall");
        for (var i = 0; i < RoomEditPresenter.MaxFormExits + 3; i++) { p.AddExit(); }
        Assert.Equal(RoomEditPresenter.MaxFormExits, p.Exits.Count);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Delete_Refused_ShowsCoresSentence_AndKeepsTheRoom()
    {
        var core = new FakeAdminClient { RoomEditResult = AdminResult.Refused, RoomEditMessage = "The default room can never be deleted." }.AddRoom("t.start");
        var (p, _) = Make("t.start", core);
        Assert.Equal(RoomEditOutcome.Problem, await p.DeleteAsync());
        Assert.Equal("The default room can never be deleted.", p.Error);
        Assert.True(core.Rooms.ContainsKey("t.start"));
        Assert.Equal("short t.start", p.Short); // the form shows the stored values again
    }

    [Fact]
    public async Task Delete_Refused_WithoutASentence_ShowsAFixedMessage()
    {
        var (p, _) = Make("t.start", new FakeAdminClient { RoomEditResult = AdminResult.Refused }.AddRoom("t.start"));
        await p.DeleteAsync();
        Assert.Equal(Messages.RoomSaveFailed, p.Error);
    }

    private static (RoomEditPresenter P, FakeAdminClient Core) Generated(string key = "t.gen")
    {
        var core = new FakeAdminClient().AddRoom("t.gen").AddRoom("t.hand");
        core.Rooms["t.gen"].Gen = new RoomGen { Map = "t", Terrain = "forest", Seed = "s", LibVersion = 1 };
        return Make(key, core);
    }

    [Fact]
    public async Task Load_ShowsGenerationInfo_OnlyForGeneratedRooms()
    {
        var (gen, _) = Generated();
        await gen.LoadAsync();
        Assert.True(gen.IsGenerated);
        Assert.Equal(("t", "forest"), (gen.GenMap, gen.GenTerrain));
        var (hand, _) = Make("t.hand", new FakeAdminClient().AddRoom("t.hand"));
        await hand.LoadAsync();
        Assert.False(hand.IsGenerated);
    }

    [Fact]
    public void RegenWarning_MentionsHandEdits()
    {
        Assert.Contains("hand edits", Messages.RegenWarning, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Regen replaces the description of this room, ALL features (including ones you added by hand) and properties with new generator output. Your hand edits to them are lost. Exits and occupants stay. Continue?", Messages.RegenWarning);
    }

    [Fact]
    public async Task Regen_CallsCoreForThatRoomOnly_AndShowsTheCounts()
    {
        var (p, core) = Generated();
        core.GenerationResult = new GenerationReply { Result = AdminResult.Ok, UpdatedCount = 1, UnchangedCount = 0 };
        await p.RegenAsync();
        Assert.Equal(new[] { ("regen", "t.gen") }, core.GenerationCalls);
        Assert.Equal("Regenerated: 1 room replaced, 0 unchanged.", p.Notice);
        Assert.Null(p.Error);
    }

    [Fact]
    public async Task Update_ShowsTheFeatureCounts()
    {
        var (p, core) = Generated();
        core.GenerationResult = new GenerationReply { Result = AdminResult.Ok, UpdatedCount = 2, UnchangedCount = 3, DeletedTemplateCount = 1 };
        await p.UpdateAsync();
        Assert.Equal(new[] { ("update", "t.gen") }, core.GenerationCalls);
        Assert.Equal("Updated: 2 features updated, 3 already current, 1 whose template was deleted (left as they are).", p.Notice);
    }

    [Fact]
    public async Task Regen_NoChange_IsANoticeNotAnError()
    {
        var (p, core) = Generated();
        core.GenerationResult = new GenerationReply { Result = AdminResult.NoChange, UnchangedCount = 1 };
        await p.RegenAsync();
        Assert.Null(p.Error);
        Assert.Equal("Regenerated: 0 room replaced, 1 unchanged.", p.Notice);
    }

    [Fact]
    public async Task Regen_OfARoomThatIsNotGenerated_ShowsAFixedError()
    {
        var (p, core) = Generated("t.hand");
        var reply = new GenerationReply { Result = AdminResult.Ok };
        reply.Rooms.Add(new RoomGenLine { Key = "t.hand", Outcome = RoomGenOutcome.RoomGenNotGenerated, Detail = "secret detail" });
        core.GenerationResult = reply;
        await p.RegenAsync();
        Assert.Equal(Messages.RoomNotGenerated, p.Error);
        Assert.Null(p.Notice);
    }

    [Theory]
    [InlineData(AdminResult.Refused, "The terrain library is not available right now. Nothing was changed.")]
    [InlineData(AdminResult.AuditFailed, Messages.RoomAuditFailed)]
    [InlineData(AdminResult.Forbidden, Messages.RoomForbidden)]
    [InlineData(AdminResult.Failed, Messages.RegenFailed)]
    public async Task Regen_Failures_ShowFixedMessages_NeverCoreText(AdminResult result, string expected)
    {
        var (p, core) = Generated();
        core.GenerationResult = new GenerationReply { Result = result, Errors = { "stored value leak" } };
        await p.RegenAsync();
        Assert.Equal(expected, p.Error);
    }

    [Fact]
    public async Task Update_Failure_ShowsTheUpdateSentence()
    {
        var (p, core) = Generated();
        core.GenerationResult = new GenerationReply { Result = AdminResult.Failed };
        await p.UpdateAsync();
        Assert.Equal(Messages.UpdateFailed, p.Error);
    }

    [Fact]
    public async Task Regen_WhenCoreIsDown_IsUnavailable_AndWhenTheSessionEnded_IsForbidden()
    {
        var (down, _) = Make("t.gen", new FakeAdminClient { Unavailable = true });
        await down.RegenAsync();
        Assert.Equal(RoomEditMissing.Unavailable, down.Missing);
        var (refused, _) = Make("t.gen", new FakeAdminClient { RefuseRoomCalls = true });
        await refused.UpdateAsync();
        Assert.Equal(RoomEditMissing.Forbidden, refused.Missing);
    }

    [Fact]
    public async Task Delete_Success_ReportsDeleted()
    {
        var (p, core) = Make("t.hall");
        Assert.Equal(RoomEditOutcome.Deleted, await p.DeleteAsync());
        Assert.Equal(new[] { "t.hall" }, core.RoomDeletes);
        Assert.False(core.Rooms.ContainsKey("t.hall"));
        Assert.Equal(Messages.RoomDeleted, p.Notice);
    }

    [Fact]
    public async Task Move_Success_ReportsTheNewKey()
    {
        var (p, core) = Make("t.hall");
        p.NewArea = " u "; p.NewLocalKey = "vault";
        Assert.Equal(RoomEditOutcome.Moved, await p.MoveAsync());
        Assert.Equal("u.vault", p.NewKey);
        Assert.Equal(WebRoutes.Prefix + "/creator/rooms/u.vault", WebRoutes.RoomEdit(p.NewKey!));
        Assert.Equal(new[] { ("t.hall", "u", "vault") }, core.RoomMoves);
        Assert.True(core.Rooms.ContainsKey("u.vault"));
    }

    [Fact]
    public async Task Move_AlreadyExists_ShowsFixedError_AndKeepsTheRoom()
    {
        var (p, core) = Make("t.hall", new FakeAdminClient { RoomEditResult = AdminResult.AlreadyExists }.AddRoom("t.start").AddRoom("t.hall"));
        p.NewArea = "t"; p.NewLocalKey = "start";
        Assert.Equal(RoomEditOutcome.Problem, await p.MoveAsync());
        Assert.Equal(Messages.RoomKeyExists, p.Error);
        Assert.True(core.Rooms.ContainsKey("t.hall"));
    }

    [Fact]
    public async Task Move_Refused_ShowsCoresSentence()
    {
        var (p, _) = Make("t.start", new FakeAdminClient { RoomEditResult = AdminResult.Refused, RoomEditMessage = "The default room can never be moved." }.AddRoom("t.start"));
        await p.MoveAsync();
        Assert.Equal("The default room can never be moved.", p.Error);
    }

    [Fact]
    public async Task Move_NotFound_IsMissing()
    {
        var (p, _) = Make("t.gone", new FakeAdminClient { RoomEditResult = AdminResult.NotFound });
        await p.MoveAsync();
        Assert.Equal(RoomEditMissing.NotFound, p.Missing);
    }

    [Fact]
    public async Task Move_CoreUnavailable_IsUnavailable()
    {
        var (p, _) = Make("t.hall", new FakeAdminClient { Unavailable = true });
        await p.MoveAsync();
        Assert.Equal(RoomEditMissing.Unavailable, p.Missing);
    }

    [Fact]
    public async Task Delete_NotFound_IsMissing()
    {
        var (p, _) = Make("t.gone", new FakeAdminClient { RoomEditResult = AdminResult.NotFound });
        await p.DeleteAsync();
        Assert.Equal(RoomEditMissing.NotFound, p.Missing);
    }

    [Fact]
    public async Task Delete_WhenCoreRefusesTheRole_IsForbidden_AndTheRoomStays()
    {
        var (p, core) = Make("t.hall", new FakeAdminClient { RefuseRoomCalls = true }.AddRoom("t.hall"));
        await p.DeleteAsync();
        Assert.Equal(RoomEditMissing.Forbidden, p.Missing);
        Assert.True(core.Rooms.ContainsKey("t.hall"));
    }

    [Fact]
    public async Task Delete_WhenCoreIsUnavailable_IsUnavailable()
    {
        var (p, _) = Make("t.hall", new FakeAdminClient { Unavailable = true });
        await p.DeleteAsync();
        Assert.Equal(RoomEditMissing.Unavailable, p.Missing);
    }

    private static FakeAdminClient WithFeatures()
    {
        var core = new FakeAdminClient().AddRoom("t.start").AddRoom("t.hall", "t", 1, 1, 0, ("north", "t.start"));
        var a = new RoomFeature { Short = "fountain", Long = "A <b>stone</b> fountain.", RoomLine = "A fountain gurgles.", Detail = "Carved." };
        a.Handles.AddRange(new[] { "fountain", "basin" });
        a.Adjectives.Add("stone");
        a.Plurals.Add("fountains");
        var b = new RoomFeature { Short = "bench", Long = "A bench." };
        core.Rooms["t.hall"].Features.AddRange(new[] { a, b });
        return core;
    }

    [Fact]
    public async Task Load_LoadsFeatures_WithWordListsJoined_AndTheTextUnencoded()
    {
        var (p, _) = Make("t.hall", WithFeatures());
        await p.LoadAsync();
        Assert.Equal(new[] { "fountain", "bench" }, p.Features.Select(f => f.Short).ToArray());
        Assert.Equal("fountain, basin", p.Features[0].Handles);
        Assert.Equal("stone", p.Features[0].Adjectives);
        Assert.Equal("fountains", p.Features[0].Plurals);
        Assert.Equal("A fountain gurgles.", p.Features[0].RoomLine);
        Assert.Equal("", p.Features[1].Handles);
        Assert.Equal("A <b>stone</b> fountain.", p.Features[0].Long); // Blazor encodes on render; the value stays raw text
    }

    [Fact]
    public async Task AddFeature_RemoveFeature_ChangeTheFormOnly_NoWrite()
    {
        var (p, core) = Make("t.hall", WithFeatures());
        await p.LoadAsync();
        p.AddFeature();
        Assert.Equal(3, p.Features.Count);
        p.RemoveFeature(0);
        Assert.Equal(new[] { "bench", null }, p.Features.Select(f => f.Short).ToArray());
        p.RemoveFeature(9); // out of range: ignored
        Assert.Equal(2, p.Features.Count);
        Assert.Empty(core.RoomUpdates);
    }

    [Fact]
    public void AddFeature_StopsAtTheLimit()
    {
        var (p, _) = Make("t.hall");
        for (var i = 0; i < RoomEditPresenter.MaxFormFeatures + 3; i++) { p.AddFeature(); }
        Assert.Equal(RoomEditPresenter.MaxFormFeatures, p.Features.Count);
    }

    [Fact]
    public async Task MoveFeature_UpAndDown_SwapNeighbours_AndIgnoreTheEnds()
    {
        var (p, core) = Make("t.hall", WithFeatures());
        await p.LoadAsync();
        p.MoveFeatureDown(0);
        Assert.Equal(new[] { "bench", "fountain" }, p.Features.Select(f => f.Short).ToArray());
        p.MoveFeatureDown(1); // last row: nothing
        p.MoveFeatureUp(0);   // first row: nothing
        Assert.Equal(new[] { "bench", "fountain" }, p.Features.Select(f => f.Short).ToArray());
        p.MoveFeatureUp(1);
        Assert.Equal(new[] { "fountain", "bench" }, p.Features.Select(f => f.Short).ToArray());
        Assert.Empty(core.RoomUpdates);
    }

    [Fact]
    public async Task Save_SendsTheFeatureList_InOrder_WithFeaturesPresent_InOneUpdate()
    {
        var (p, core) = Make("t.hall", WithFeatures());
        p.Short = "x"; p.Long = "y"; p.Area = "t";
        p.Features = new List<FeatureInput>
        {
            new() { Short = " bench ", Long = " A bench. ", Handles = "bench,  seat\tpew", Adjectives = "", Plurals = " , " },
            new() { Short = "fountain", Long = "Wet.", RoomLine = "", Detail = "d", Handles = "fountain" },
        };
        await p.SaveAsync();
        var sent = Assert.Single(core.RoomUpdates);
        Assert.True(sent.FeaturesPresent);
        Assert.Equal(new[] { "bench", "fountain" }, sent.Features.Select(f => f.Short).ToArray());
        Assert.Equal("A bench.", sent.Features[0].Long);
        Assert.Equal(new[] { "bench", "seat", "pew" }, sent.Features[0].Handles.ToArray());
        Assert.Empty(sent.Features[0].Plurals);
        Assert.Equal("d", sent.Features[1].Detail);
        Assert.Equal("x", sent.ShortDescription); // other room fields travel in the same call
    }

    [Fact]
    public async Task Save_WithNoFeatureRows_SendsAnEmptyList_ThatRemovesThem()
    {
        var (p, core) = Make("t.hall", WithFeatures());
        await p.LoadAsync();
        p.Features = new List<FeatureInput>();
        await p.SaveAsync();
        Assert.True(Assert.Single(core.RoomUpdates).FeaturesPresent);
        Assert.Empty(core.Rooms["t.hall"].Features);
    }

    [Fact]
    public async Task Save_MoreFeatureRowsThanTheLimit_AreCut()
    {
        var (p, core) = Make("t.hall", new FakeAdminClient().AddRoom("t.hall"));
        p.Features = Enumerable.Range(0, RoomEditPresenter.MaxFormFeatures + 5).Select(i => new FeatureInput { Short = "f" + i, Long = "l" }).ToList();
        await p.SaveAsync();
        Assert.Equal(RoomEditPresenter.MaxFormFeatures, Assert.Single(core.RoomUpdates).Features.Count);
    }

    [Fact]
    public async Task Save_RoundTrip_ReloadShowsTheEditedFeatures()
    {
        var (p, core) = Make("t.hall", WithFeatures());
        await p.LoadAsync();
        p.Features[1].Long = "A long oak bench.";
        p.AddFeature();
        p.Features[2].Short = "statue"; p.Features[2].Long = "Grey."; p.Features[2].Handles = "statue idol";
        p.MoveFeatureUp(2);
        await p.SaveAsync();
        Assert.Equal("Saved. The running game has the change now.", p.Notice);
        Assert.Equal(new[] { "fountain", "statue", "bench" }, core.Rooms["t.hall"].Features.Select(f => f.Short).ToArray());

        var (again, _) = Make("t.hall", core);
        await again.LoadAsync();
        Assert.Equal(new[] { "fountain", "statue", "bench" }, again.Features.Select(f => f.Short).ToArray());
        Assert.Equal("statue, idol", again.Features[1].Handles);
        Assert.Equal("A long oak bench.", again.Features[2].Long);
    }

    [Fact]
    public async Task Save_InvalidFeatures_ShowEachCoreErrorWithTheFeatureNumber_AndKeepTheInput()
    {
        var core = WithFeatures();
        core.RoomEditResult = AdminResult.Invalid;
        core.RoomEditErrors.Add(new FieldError { Field = "Features[1].Long", Message = "Long description is required." });
        core.RoomEditErrors.Add(new FieldError { Field = "Features", Message = "Two features use the same short description." });
        core.RoomEditErrors.Add(new FieldError { Field = "Features[0].Handles", Message = "Handle may not contain control characters." });
        var (p, _) = Make("t.hall", core);
        p.Short = "x"; p.Long = "y";
        p.Features = new List<FeatureInput> { new() { Short = "a", Long = "l", Handles = "bad" }, new() { Short = "a" } };
        Assert.Equal(RoomEditOutcome.Problem, await p.SaveAsync());
        Assert.Equal(new[]
        {
            "Feature 2: Long description is required.",
            "Two features use the same short description.",
            "Feature 1: Handle may not contain control characters.",
        }, p.Problems.ToArray());
        Assert.Equal(2, p.Features.Count); // the user's rows stay in the form
        Assert.Equal("a", p.Features[1].Short);
    }

    [Fact]
    public void RoomUrl_EscapesTheKey_SoItCannotBreakOutOfTheLink()
    {
        Assert.Equal("/creator/rooms/t.hall", WebRoutes.RoomEdit("t.hall"));
        Assert.DoesNotContain("\"", WebRoutes.RoomEdit("a\"b<c>"));
        Assert.DoesNotContain("<", WebRoutes.RoomEdit("a\"b<c>"));
    }
}

public class CreatorPresenterTests
{
    private static (CreatorPresenter P, FakeAdminClient Core) Make(FakeAdminClient core, string token = "token-1") =>
        (new CreatorPresenter(new RoomCreationService(core), token), core);

    private static void Fill(CreatorPresenter p)
    {
        p.Form.Key = "new"; p.Form.Short = "a room"; p.Form.Long = "Long."; p.Form.Area = "t";
    }

    [Fact]
    public async Task Created_ReportsTheKey_AndSendsTheCircuitToken()
    {
        var (p, core) = Make(new FakeAdminClient(), "abc");
        Fill(p);
        Assert.Equal(CreatorOutcome.Created, await p.CreateAsync());
        Assert.Equal("t.new", p.CreatedKey);
        Assert.Equal("/creator/rooms/t.new", WebRoutes.RoomEdit(p.CreatedKey!));
        Assert.Equal(new[] { "abc" }, core.RoomTokens.Distinct());
    }

    [Fact]
    public async Task Areas_AreDistinctAndSorted()
    {
        var core = new FakeAdminClient();
        core.Rooms["b.one"] = new RoomDetail { Key = "b.one", Area = "b" };
        core.Rooms["a.one"] = new RoomDetail { Key = "a.one", Area = "a" };
        core.Rooms["a.two"] = new RoomDetail { Key = "a.two", Area = "a" };
        var (p, _) = Make(core);
        await p.LoadAreasAsync();
        Assert.Equal(new[] { "a", "b" }, p.Areas);
    }

    [Fact]
    public async Task Areas_CoreUnavailable_StillWorksWithNoSuggestions()
    {
        var (p, _) = Make(new FakeAdminClient { Unavailable = true });
        await p.LoadAreasAsync();
        Assert.Empty(p.Areas);
    }

    [Fact]
    public async Task KeyExists_ShowsFixedError()
    {
        var (p, _) = Make(new FakeAdminClient { RoomEditResult = AdminResult.AlreadyExists });
        Fill(p);
        Assert.Equal(CreatorOutcome.Failed, await p.CreateAsync());
        Assert.Equal(Messages.RoomKeyExists, p.Error);
    }

    [Fact]
    public async Task CoreUnavailable_ShowsFixedError()
    {
        var (p, _) = Make(new FakeAdminClient { Unavailable = true });
        Fill(p);
        await p.CreateAsync();
        Assert.Equal(Messages.Unavailable, p.Error);
    }

    [Fact]
    public async Task RefusedSession_ShowsForbidden()
    {
        var (p, _) = Make(new FakeAdminClient { RefuseRoomCalls = true });
        Fill(p);
        await p.CreateAsync();
        Assert.Equal(Messages.RoomCreateForbidden, p.Error);
    }

    [Fact]
    public async Task Failed_ShowsTheFixedFailureMessage()
    {
        var (p, _) = Make(new FakeAdminClient { RoomEditResult = AdminResult.Failed });
        Fill(p);
        await p.CreateAsync();
        Assert.Equal(Messages.RoomCreateFailed, p.Error);
    }

    [Fact]
    public async Task CoreFieldErrors_AreAttachedToTheirFields_AndInputIsKept()
    {
        var core = new FakeAdminClient { RoomEditResult = AdminResult.Invalid };
        core.RoomEditErrors.Add(new FieldError { Field = "Short", Message = "Short is too long." });
        core.RoomEditErrors.Add(new FieldError { Field = "Exits[1].To", Message = "Bad target." });
        core.RoomEditErrors.Add(new FieldError { Field = "Whatever", Message = "Summary only." });
        var (p, _) = Make(core);
        Fill(p);
        await p.CreateAsync();
        Assert.Equal(new[] { "Short is too long." }, p.ErrorsFor("Short"));
        Assert.Equal(new[] { "Bad target." }, p.ErrorsFor("Exits[1].To"));
        Assert.Equal(new[] { "Summary only." }, p.ErrorsFor(string.Empty));
        Assert.Equal("a room", p.Form.Short);
    }

    [Fact]
    public async Task FormAnnotations_AreCheckedBeforeCoreIsAsked()
    {
        var (p, core) = Make(new FakeAdminClient());
        p.Form.Key = "Bad Key"; p.Form.Short = "s"; p.Form.Long = "l"; p.Form.Area = "t";
        Assert.Equal(CreatorOutcome.Failed, await p.CreateAsync());
        Assert.NotEmpty(p.ErrorsFor("Key"));
        Assert.Empty(core.RoomCreates);

        // As with MVC model validation, the cross-field rules (IValidatableObject) run once the property rules pass.
        p.Form.Key = "ok";
        p.Form.Exits[0].Dir = "north"; // target missing
        Assert.Equal(CreatorOutcome.Failed, await p.CreateAsync());
        Assert.Empty(p.ErrorsFor("Key"));
        Assert.NotEmpty(p.ErrorsFor("Exits[0].To"));
        Assert.Empty(core.RoomCreates);
    }

    [Fact]
    public void TheForm_StartsWithFourEmptyExitRows()
    {
        var (p, _) = Make(new FakeAdminClient());
        Assert.Equal(RoomForm.ExitRows, p.Form.Exits.Count);
        Assert.All(p.Form.Exits, e => Assert.True(e.IsEmpty));
        Assert.Null(p.Form.X); // blank is unplaced, never 0
    }
}

public class CreatorMapPresenterTests
{
    private static FakeAdminClient Core(params RoomRecord[] rooms)
    {
        var core = new FakeAdminClient();
        foreach (var r in rooms) { core.AddRoom(r.Key, r.Area ?? string.Empty, r.X, r.Y, r.Z); }
        return core;
    }

    private static RoomRecord R(string key, string area, int? x, int? y, int? z) =>
        new(key, area, "s " + key, x, y, z, Array.Empty<RoomExitRecord>());

    [Fact]
    public async Task DefaultsToFirstAreaAndLevelZero()
    {
        var core = Core(R("b.x", "b", 0, 0, 0), R("a.x", "a", 0, 0, 0), R("a.y", "a", 0, 0, 2));
        var p = new CreatorMapPresenter(core, "abc");
        Assert.Equal(MapOutcome.Ok, await p.LoadAsync(null, null));
        Assert.Equal(new[] { "abc" }, core.RoomTokens);
        Assert.Equal(new[] { "a", "b" }, p.Areas);
        Assert.Equal("a", p.Area);
        Assert.Equal(0, p.Z);
        Assert.Equal(new[] { 0, 2 }, p.Levels);
        Assert.Equal(new[] { "a.x" }, p.Map!.Rects.Select(r => r.Key));
    }

    [Fact]
    public async Task TheLevelSelector_ShowsTheChosenLevel()
    {
        var p = new CreatorMapPresenter(Core(R("a.x", "a", 0, 0, 0), R("a.y", "a", 0, 0, 2)), "t");
        await p.LoadAsync("a", 2);
        Assert.Equal(2, p.Z);
        Assert.Equal(new[] { "a.y" }, p.Map!.Rects.Select(r => r.Key));
    }

    [Fact]
    public async Task UnknownAreaFallsBackToFirst()
    {
        var p = new CreatorMapPresenter(Core(R("a.x", "a", 0, 0, 0)), "t");
        await p.LoadAsync("nope", 0);
        Assert.Equal("a", p.Area);
    }

    [Fact]
    public async Task NoRooms_RendersNothing()
    {
        var p = new CreatorMapPresenter(Core(), "t");
        Assert.Equal(MapOutcome.Ok, await p.LoadAsync(null, null));
        Assert.Empty(p.Areas);
        Assert.Null(p.Map);
        Assert.Null(p.Error);
    }

    [Fact]
    public async Task UnplacedRoom_IsListedAsUnplaced_NotDrawnAtZero()
    {
        var p = new CreatorMapPresenter(Core(R("a.x", "a", 0, 0, 0), R("a.u", "a", null, null, null)), "t");
        await p.LoadAsync(null, null);
        Assert.Equal(new[] { "a.x" }, p.Map!.Rects.Select(r => r.Key));
        Assert.Equal(new[] { "a.u" }, p.Map.Unplaced.Select(u => u.Key));
    }

    [Fact]
    public async Task WhenCoreIsUnavailable_ShowsAFixedMessage_AndNoMap()
    {
        var p = new CreatorMapPresenter(new FakeAdminClient { Unavailable = true }, "t");
        Assert.Equal(MapOutcome.Unavailable, await p.LoadAsync(null, null));
        Assert.Equal(Messages.Unavailable, p.Error);
        Assert.Null(p.Map);
    }

    [Fact]
    public async Task WhenCoreRefusesTheSession_IsDenied()
    {
        var p = new CreatorMapPresenter(new FakeAdminClient { RefuseRoomCalls = true }, "t");
        Assert.Equal(MapOutcome.Denied, await p.LoadAsync(null, null));
    }

    [Fact]
    public void RoomLinks_PointAtTheBlazorEditPage()
    {
        Assert.Equal("/creator/rooms/castle.hall", WebRoutes.RoomEdit("castle.hall"));
        Assert.Equal("/creator/map?area=a%20b&z=2", WebRoutes.MapFor("a b", 2));
    }

    private static FakeAdminClient Coloured()
    {
        var core = new FakeAdminClient().AddRoom("a.plain", "a", 0, 0, 0).AddRoom("a.g1", "a", 1, 0, 0).AddRoom("a.g2", "a", 2, 0, 0);
        core.Rooms["a.g1"].Gen = new RoomGen { Map = "a", Terrain = "forest" };
        core.Rooms["a.g2"].Gen = new RoomGen { Map = "a", Terrain = "swamp" };
        core.TerrainColors.Add(new TerrainColor { Map = "a", Terrain = "forest", Color = "#228B22" });
        return core;
    }

    private static string Fill(CreatorMapPresenter p, string key) => p.FillFor(p.Map!.Rects.Single(r => r.Key == key));

    [Fact]
    public async Task GeneratedRoom_TakesTheLegendColourOfItsTerrain_AndUnknownTerrainGetsTheNeutralFallback_AndPlainRoomsKeepTheirFill()
    {
        var p = new CreatorMapPresenter(Coloured(), "t");
        await p.LoadAsync("a", 0);
        Assert.Equal("#228B22", Fill(p, "a.g1"));
        Assert.Equal(CreatorMapPresenter.GeneratedFallbackFill, Fill(p, "a.g2"));
        Assert.Equal(CreatorMapPresenter.PlainFill, Fill(p, "a.plain"));
    }

    [Fact]
    public async Task ColourOfAnotherMapIsNotUsed()
    {
        var core = Coloured();
        core.TerrainColors.Clear();
        core.TerrainColors.Add(new TerrainColor { Map = "other", Terrain = "forest", Color = "#228B22" });
        var p = new CreatorMapPresenter(core, "t");
        await p.LoadAsync("a", 0);
        Assert.Equal(CreatorMapPresenter.GeneratedFallbackFill, Fill(p, "a.g1"));
    }

    [Fact]
    public async Task BadColourText_IsIgnored()
    {
        var core = Coloured();
        core.TerrainColors.Clear();
        core.TerrainColors.Add(new TerrainColor { Map = "a", Terrain = "forest", Color = "red\" onload=\"x" });
        var p = new CreatorMapPresenter(core, "t");
        await p.LoadAsync("a", 0);
        Assert.Equal(CreatorMapPresenter.GeneratedFallbackFill, Fill(p, "a.g1"));
    }

    [Fact]
    public async Task WhenColoursAreUnavailable_TheMapStillRenders_WithFallbacks()
    {
        var core = Coloured();
        core.ColorsUnavailable = true;
        var p = new CreatorMapPresenter(core, "t");
        await p.LoadAsync("a", 0);
        Assert.Null(p.Error);
        Assert.Equal(CreatorMapPresenter.GeneratedFallbackFill, Fill(p, "a.g1"));
    }

    [Fact]
    public async Task WithoutGeneratedRooms_ColoursAreNotRequested()
    {
        var core = new FakeAdminClient().AddRoom("a.plain", "a", 0, 0, 0);
        var p = new CreatorMapPresenter(core, "t");
        await p.LoadAsync("a", 0);
        Assert.Equal(0, core.ColorCalls);
        Assert.Equal(CreatorMapPresenter.PlainFill, Fill(p, "a.plain"));
    }
}

// Component-level guards (source and reflection; there is no bUnit, see the PR notes).
public class RoomComponentGuardTests
{
    private static string Read(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MUD.sln"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static Type Component(string name) =>
        typeof(IAdminClient).Assembly.GetTypes().Single(t => t.FullName == "MUD.Web.Components.Pages." + name);

    [Theory]
    [InlineData("Creator", "/creator")]
    [InlineData("CreatorMap", "/creator/map")]
    [InlineData("RoomEdit", "/creator/rooms/{Key}")]
    public void TheRoomPages_RequireTheCreatorOrAdminPolicy_OnTheirRoutes(string name, string route)
    {
        var type = Component(name);
        Assert.Equal(WebPolicies.CreatorOrAdmin, type.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal(route, type.GetCustomAttribute<RouteAttribute>()!.Template);
    }

    [Fact]
    public void RouteConstants_MatchThePageRoutes()
    {
        Assert.Equal(WebRoutes.Creator, Component("Creator").GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal(WebRoutes.Map, Component("CreatorMap").GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal(WebRoutes.RoomEdit("{Key}").Replace("%7BKey%7D", "{Key}"), Component("RoomEdit").GetCustomAttribute<RouteAttribute>()!.Template);
    }

    [Fact]
    public void TheRoomKey_ComesOnlyFromTheRoute()
    {
        var key = Component("RoomEdit").GetProperty("Key")!;
        Assert.NotNull(key.GetCustomAttribute<ParameterAttribute>());
        Assert.Null(key.GetCustomAttribute<SupplyParameterFromQueryAttribute>());
        var src = Read("MUD.Web/Components/Pages/RoomEdit.razor");
        Assert.DoesNotContain("p.Key =", src);
        Assert.DoesNotContain("Rev", src.Replace("Revalid", string.Empty));
        Assert.DoesNotContain("Conflict", src);
    }

    [Fact]
    public void RoomEdit_AsksBeforeRegen_WithTheFixedWarning_AndHasNoScriptOrCdn()
    {
        var src = Read("MUD.Web/Components/Pages/RoomEdit.razor");
        Assert.Contains("Messages.RegenWarning", src);
        Assert.Contains("AskRegen", src);
        Assert.Contains("ConfirmRegenAsync", src);
        Assert.DoesNotContain("<script", src);
        Assert.DoesNotContain("http", src);
        Assert.DoesNotContain("MarkupString", src);
        foreach (var f in new[] { "Creator", "CreatorMap" }) { Assert.DoesNotContain("MarkupString", Read("MUD.Web/Components/Pages/" + f + ".razor")); }
        Assert.DoesNotContain("MarkupString", Read("MUD.Web/Components/Shared/RoomMap.razor"));
        Assert.DoesNotContain("MarkupString", Read("MUD.Web/Components/Shared/ExitRows.razor"));
    }

    [Fact]
    public void RoomEdit_HasTheFormatNote_AndFeaturesComeAfterExits()
    {
        var src = Read("MUD.Web/Components/Pages/RoomEdit.razor");
        Assert.Contains("separate words with commas or spaces", src);
        Assert.True(src.IndexOf(">Exits</h2>", StringComparison.Ordinal) < src.IndexOf(">Features</h2>", StringComparison.Ordinal));
        Assert.Contains("<textarea id=\"re-f@(index)-long\"", src);
        Assert.Contains("<textarea id=\"re-f@(index)-detail\"", src);
    }

    [Fact]
    public void RoomEdit_DeleteMoveAndRegen_AreSeparateFromTheSaveForm()
    {
        var src = Read("MUD.Web/Components/Pages/RoomEdit.razor");
        var formEnd = src.IndexOf("</form>", StringComparison.Ordinal);
        Assert.True(src.IndexOf("DeleteAsync", formEnd, StringComparison.Ordinal) > formEnd);
        Assert.True(src.IndexOf("MoveAsync", formEnd, StringComparison.Ordinal) > formEnd);
        Assert.True(src.IndexOf("AskRegen", formEnd, StringComparison.Ordinal) > formEnd);
    }

    [Fact]
    public void Drafts_AreNeverKeptForPasswords_AndTheStoreKeyIsPerUserAndRoute()
    {
        Assert.Equal("draft:Bob:/creator/rooms/t.hall", DraftStore.KeyFor("Bob", WebRoutes.RoomEdit("t.hall")));
        foreach (var f in new[] { "Creator", "RoomEdit" }) { Assert.DoesNotContain("password", Read("MUD.Web/Components/Pages/" + f + ".razor"), StringComparison.OrdinalIgnoreCase); }
    }

    [Fact]
    public void Notices_AreShownOnce()
    {
        var n = new NoticeService();
        n.Set("Room moved.");
        Assert.Equal("Room moved.", n.Take());
        Assert.Null(n.Take());
    }

    [Fact]
    public void TheMessages_ArePinned()
    {
        Assert.Equal("A room with that key already exists.", Messages.RoomKeyExists);
        Assert.Equal("The room could not be created. Please try again later.", Messages.RoomCreateFailed);
        Assert.Equal("You are not allowed to create rooms.", Messages.RoomCreateForbidden);
        Assert.Equal("The game is not reachable right now. Please try again later.", Messages.Unavailable);
        Assert.Equal("The game is not reachable right now. Please try again later.", Messages.Unavailable);
        Assert.Equal("That room no longer exists.", Messages.RoomNotFound);
        Assert.Equal("The room could not be saved. Please try again later.", Messages.RoomSaveFailed);
        Assert.Equal("The change could not be recorded, so nothing was saved. Please try again later.", Messages.RoomAuditFailed);
        Assert.Equal("A room with that key already exists.", Messages.RoomKeyExists);
        Assert.Equal("You are not allowed to edit rooms.", Messages.RoomForbidden);
        Assert.Equal("The game is not reachable right now. Please try again later.", Messages.Unavailable);
        Assert.Equal("Regen replaces the description of this room, ALL features (including ones you added by hand) and properties with new generator output. Your hand edits to them are lost. Exits and occupants stay. Continue?", Messages.RegenWarning);
        Assert.Equal("This room was not made by a generator, so it cannot be regenerated or updated.", Messages.RoomNotGenerated);
        Assert.Equal("The terrain library is not available right now. Nothing was changed.", Messages.LibraryUnavailable);
        Assert.Equal("The room could not be regenerated. Please try again later.", Messages.RegenFailed);
        Assert.Equal("The room could not be updated. Please try again later.", Messages.UpdateFailed);
        Assert.Equal(32, RoomEditPresenter.MaxFormExits);
        Assert.Equal(64, RoomEditPresenter.MaxFormFeatures);
    }
}
