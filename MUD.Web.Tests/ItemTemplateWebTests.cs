using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MUD.Contracts;
using MUD.Web.Components.Pages;
using MUD.Web.Pages;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

// Item templates: the gRPC client calls, the fake client, both presenters and the two pages (specs/WEB_BLAZOR.md "Item templates").
// Pages are rendered with HtmlRenderer over FakeAdminClient.
public class ItemTemplateGrpcClientTests : IDisposable
{
    private sealed class StandIn : Admin.AdminBase
    {
        public static string? LastArea;

        private static void RequireToken(string token)
        {
            if (token == "stale") { throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unauthenticated, "no")); }
            if (token == "low") { throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.PermissionDenied, "no")); }
            if (token == "busy") { throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "no")); }
        }

        private static ItemTemplateEditReply Answer(string id) => id switch
        {
            "t.taken" => new ItemTemplateEditReply { Result = AdminResult.AlreadyExists, Message = "exists" },
            "t.used" => new ItemTemplateEditReply { Result = AdminResult.Refused, Message = "in use" },
            "t.bad" => new ItemTemplateEditReply { Result = AdminResult.Invalid, Errors = { new FieldError { Field = "Short", Message = "required" } } },
            _ => new ItemTemplateEditReply { Result = AdminResult.Ok, Template = new ItemTemplateRecord { Id = id, Version = 1 } },
        };

        public override Task<ListItemTemplatesReply> ListItemTemplates(ListItemTemplatesRequest request, Grpc.Core.ServerCallContext context)
        {
            RequireToken(request.Token);
            LastArea = request.Area;
            var reply = new ListItemTemplatesReply();
            reply.Templates.Add(new ItemTemplateRecord { Id = "t.a", Area = "t", Short = "a thing", Version = 3 });
            return Task.FromResult(reply);
        }

        public override Task<GetItemTemplateReply> GetItemTemplate(ItemTemplateIdRequest request, Grpc.Core.ServerCallContext context)
        {
            RequireToken(request.Token);
            return Task.FromResult(request.Id == "t.a" ? new GetItemTemplateReply { Found = true, Template = new ItemTemplateRecord { Id = "t.a" } } : new GetItemTemplateReply());
        }

        public override Task<ItemTemplateEditReply> CreateItemTemplate(SaveItemTemplateRequest request, Grpc.Core.ServerCallContext context) { RequireToken(request.Token); return Task.FromResult(Answer(request.Template.Id)); }
        public override Task<ItemTemplateEditReply> UpdateItemTemplate(SaveItemTemplateRequest request, Grpc.Core.ServerCallContext context) { RequireToken(request.Token); return Task.FromResult(Answer(request.Template.Id)); }
        public override Task<ItemTemplateEditReply> DeleteItemTemplate(ItemTemplateIdRequest request, Grpc.Core.ServerCallContext context) { RequireToken(request.Token); return Task.FromResult(Answer(request.Id)); }
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wi" + Guid.NewGuid().ToString("N").Substring(0, 8));
    private WebApplication? _app;

    private async Task<GrpcAdminClient> Start()
    {
        Directory.CreateDirectory(_directory);
        var socket = Path.Combine(_directory, "f.sock");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.ListenUnixSocket(socket, o => o.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2));
        builder.Services.AddGrpc();
        _app = builder.Build();
        _app.MapGrpcService<StandIn>();
        await _app.StartAsync();
        return new GrpcAdminClient(new Admin.AdminClient(CoreSocket.OpenChannel(socket)));
    }

    public void Dispose()
    {
        _app?.StopAsync().GetAwaiter().GetResult();
        try { Directory.Delete(_directory, true); } catch (Exception) { }
    }

    [Fact]
    public async Task ListGet_PassTheAreaFilter_AndGetOfAnUnknownIdIsNull()
    {
        var client = await Start();
        var listed = Assert.Single(await client.ListItemTemplatesAsync("tok"));
        Assert.Equal(("t.a", "t", "a thing", 3), (listed.Id, listed.Area, listed.Short, listed.Version));
        Assert.Equal(string.Empty, StandIn.LastArea);
        await client.ListItemTemplatesAsync("tok", "t");
        Assert.Equal("t", StandIn.LastArea);
        Assert.Equal("t.a", (await client.GetItemTemplateAsync("tok", "t.a"))!.Id);
        Assert.Null(await client.GetItemTemplateAsync("tok", "t.zz"));
    }

    [Theory]
    [InlineData("t.ok", AdminResult.Ok)]
    [InlineData("t.taken", AdminResult.AlreadyExists)]
    [InlineData("t.used", AdminResult.Refused)]
    [InlineData("t.bad", AdminResult.Invalid)]
    public async Task WriteCalls_ReturnTheReplyResult(string id, AdminResult expected)
    {
        var client = await Start();
        Assert.Equal(expected, (await client.CreateItemTemplateAsync("tok", new ItemTemplateRecord { Id = id })).Result);
        Assert.Equal(expected, (await client.UpdateItemTemplateAsync("tok", new ItemTemplateRecord { Id = id })).Result);
        Assert.Equal(expected, (await client.DeleteItemTemplateAsync("tok", id)).Result);
        var invalid = await client.CreateItemTemplateAsync("tok", new ItemTemplateRecord { Id = "t.bad" });
        Assert.Equal("Short", Assert.Single(invalid.Errors).Field);
    }

    [Fact]
    public async Task RefusalsAreMapped_AndAnUnreachableCoreIsUnavailable()
    {
        var client = await Start();
        Assert.True((await Assert.ThrowsAsync<CoreRefusedException>(() => client.ListItemTemplatesAsync("stale"))).SessionEnded);
        Assert.False((await Assert.ThrowsAsync<CoreRefusedException>(() => client.GetItemTemplateAsync("low", "t.a"))).SessionEnded);
        await Assert.ThrowsAsync<CoreUnavailableException>(() => client.DeleteItemTemplateAsync("busy", "x"));
        var dead = new GrpcAdminClient(new Admin.AdminClient(CoreSocket.OpenChannel(Path.Combine(_directory, "nothing.sock"))));
        await Assert.ThrowsAsync<CoreUnavailableException>(() => dead.ListItemTemplatesAsync("tok"));
        await Assert.ThrowsAsync<CoreUnavailableException>(() => dead.CreateItemTemplateAsync("tok", new ItemTemplateRecord { Id = "x" }));
    }
}

public class FakeItemTemplateClientTests
{
    [Fact]
    public async Task TheFakeStoresListsGetsAndRemoves_AndRecordsTheToken()
    {
        var core = new FakeAdminClient().AddItemTemplate("a.one").AddItemTemplate("b.two");
        Assert.Equal(new[] { "a.one", "b.two" }, (await core.ListItemTemplatesAsync("tok")).Select(i => i.Id));
        Assert.Equal(new[] { "b.two" }, (await core.ListItemTemplatesAsync("tok", "b")).Select(i => i.Id));
        Assert.Equal("a.one", (await core.GetItemTemplateAsync("tok", "a.one"))!.Id);
        Assert.Null(await core.GetItemTemplateAsync("tok", "zz"));
        Assert.Equal(AdminResult.Ok, (await core.CreateItemTemplateAsync("tok", new ItemTemplateRecord { Id = "c.new" })).Result);
        var update = await core.UpdateItemTemplateAsync("tok", new ItemTemplateRecord { Id = "c.new", Short = "x" });
        Assert.Equal(2, update.Template.Version);
        Assert.Equal(AdminResult.Ok, (await core.UpdateItemTemplateAsync("tok", new ItemTemplateRecord { Id = "d.fresh" })).Result);
        Assert.Equal(AdminResult.Ok, (await core.DeleteItemTemplateAsync("tok", "a.one")).Result);
        Assert.DoesNotContain("a.one", core.ItemTemplates.Keys);
        Assert.All(core.ItemTemplateTokens, t => Assert.Equal("tok", t));
    }

    [Theory]
    [InlineData(AdminResult.Invalid)]
    [InlineData(AdminResult.Refused)]
    public async Task ANonOkResult_ChangesNothing(AdminResult result)
    {
        var core = new FakeAdminClient { ItemTemplateResult = result, ItemTemplateMessage = "fixed" }.AddItemTemplate("a.keep");
        Assert.Equal("fixed", (await core.DeleteItemTemplateAsync("tok", "a.keep")).Message);
        Assert.Single(core.ItemTemplates);
        Assert.Equal(result, (await core.CreateItemTemplateAsync("tok", new ItemTemplateRecord { Id = "a.n" })).Result);
        Assert.False(core.ItemTemplates.ContainsKey("a.n"));
        Assert.Equal(result, (await core.UpdateItemTemplateAsync("tok", new ItemTemplateRecord { Id = "a.keep" })).Result);
    }

    [Fact]
    public async Task UnavailableAndRefusedAreRaisedByEveryCall()
    {
        var down = new FakeAdminClient { Unavailable = true };
        await Assert.ThrowsAsync<CoreUnavailableException>(() => down.ListItemTemplatesAsync("tok"));
        var refusing = new FakeAdminClient { RefuseItemTemplateCalls = true };
        await Assert.ThrowsAsync<CoreRefusedException>(() => refusing.GetItemTemplateAsync("tok", "a"));
    }
}

public class ItemTemplateListPresenterTests
{
    private const string Token = "token-1";

    private static ItemTemplateListPresenter Make(FakeAdminClient core) => new(core, Token);

    private static FakeAdminClient Seeded()
    {
        var core = new FakeAdminClient().AddArea("b", "Bee land", "Buzzing.").AddArea("a", "Ay land").AddArea("empty", "Nothing yet")
            .AddItemTemplate("b.honey", "a pot of honey").AddItemTemplate("a.sword", "a rusty sword").AddItemTemplate("a.shield", "a round shield");
        core.ItemTemplates["a.sword"].Handles.Add("blade");
        core.ItemTemplates["a.sword"].Adjectives.Add("rusty");
        return core;
    }

    [Fact]
    public async Task Load_GroupsByArea_InIdOrder_ListsEmptyAreas_AndSendsTheToken()
    {
        var core = Seeded();
        var p = Make(core);
        Assert.Equal(ItemTemplateOutcome.Ok, await p.LoadAsync());
        Assert.Equal(new[] { "a", "b", "empty" }, p.Groups.Select(g => g.AreaId));
        Assert.Equal(new[] { "a.shield", "a.sword" }, p.Groups[0].Items.Select(i => i.Id));
        Assert.Equal(("Bee land", "Buzzing."), (p.Groups[1].Name, p.Groups[1].Description));
        Assert.Empty(p.Groups[2].Items);
        Assert.Equal(3, p.Total);
        Assert.Equal(new[] { Token }, core.ItemTemplateTokens.Distinct());
    }

    [Fact]
    public async Task ATemplateWhoseAreaHasNoRecord_GetsAGroupNamedByItsId_AndAnAreaWithoutANameUsesItsPrefix()
    {
        var core = new FakeAdminClient().AddItemTemplate("lost.thing");
        core.Areas["noname"] = new AreaRecord { Id = "noname", Name = " " };
        var p = Make(core);
        await p.LoadAsync();
        Assert.Equal(new[] { "lost", "noname" }, p.Groups.Select(g => g.AreaId));
        Assert.Equal(("lost", "lost"), (p.Groups[0].AreaId, p.Groups[0].Name));
        Assert.Equal("noname", p.Groups[1].Name);
    }

    [Theory]
    [InlineData("SWORD", new[] { "a" })]               // id, case-insensitive
    [InlineData("round", new[] { "a" })]               // short
    [InlineData("blade", new[] { "a" })]               // handle
    [InlineData("rusty", new[] { "a" })]               // adjective
    [InlineData("honey", new[] { "b" })]
    [InlineData("  honey ", new[] { "b" })]
    [InlineData("zzz", new string[0])]
    public async Task Filter_MatchesIdShortHandlesAndAdjectives_AndHidesAreasWithNoMatch(string text, string[] areas)
    {
        var p = Make(Seeded());
        await p.LoadAsync();
        p.Filter = text;
        Assert.True(p.Filtering);
        Assert.Equal(areas, p.Groups.Select(g => g.AreaId));
        p.Filter = "   ";
        Assert.False(p.Filtering);
        Assert.Equal(3, p.Groups.Count);
    }

    [Theory]
    [InlineData(0, ItemTemplateListPresenter.UnavailableError)]
    [InlineData(1, ItemTemplateListPresenter.ForbiddenError)]
    public async Task Load_WhenCoreIsUnavailableOrRefuses_SaysSo_AndListsNothing(int how, string error)
    {
        var p = Make(how == 0 ? new FakeAdminClient { Unavailable = true } : new FakeAdminClient { RefuseItemTemplateCalls = true }.AddArea("a"));
        Assert.Equal(ItemTemplateOutcome.Failed, await p.LoadAsync());
        Assert.Equal(error, p.Error);
        Assert.Empty(p.Groups);
    }

    [Fact]
    public async Task Load_AnUnexpectedFailure_IsAGenericError()
    {
        var p = Make(new FakeAdminClient { Crash = true });
        Assert.Equal(ItemTemplateOutcome.Failed, await p.LoadAsync());
        Assert.Equal(ItemTemplateListPresenter.FailedError, p.Error);
    }

    [Fact]
    public async Task Delete_Ok_NotFound_AreDone_OthersAreErrors()
    {
        var core = Seeded();
        var p = Make(core);
        Assert.Equal(ItemTemplateOutcome.Ok, await p.DeleteAsync("a.sword"));
        Assert.Equal("Item template deleted.", p.Notice);
        Assert.Contains("DeleteItemTemplate:a.sword", core.ItemTemplateCalls);
        Assert.DoesNotContain("a.sword", core.ItemTemplates.Keys);

        core.ItemTemplateResult = AdminResult.NotFound;
        Assert.Equal(ItemTemplateOutcome.Ok, await p.DeleteAsync("a.sword"));
        Assert.Equal(ItemTemplateListPresenter.GoneError, p.Notice);

        core.ItemTemplateResult = AdminResult.Refused; core.ItemTemplateMessage = "2 things use it.";
        Assert.Equal(ItemTemplateOutcome.Failed, await p.DeleteAsync("a.shield"));
        Assert.Equal("2 things use it.", p.Error);
        Assert.Null(p.Notice);
        core.ItemTemplateMessage = string.Empty;
        await p.DeleteAsync("a.shield"); Assert.Equal(ItemTemplateListPresenter.RefusedError, p.Error);
        core.ItemTemplateResult = AdminResult.AuditFailed; await p.DeleteAsync("a.shield"); Assert.Equal(ItemTemplateListPresenter.AuditFailedError, p.Error);
        core.ItemTemplateResult = AdminResult.Forbidden; await p.DeleteAsync("a.shield"); Assert.Equal(ItemTemplateListPresenter.ForbiddenError, p.Error);
        core.ItemTemplateResult = AdminResult.Failed; await p.DeleteAsync(null); Assert.Equal(ItemTemplateListPresenter.FailedError, p.Error);
        Assert.Contains("DeleteItemTemplate:", core.ItemTemplateCalls);
        Assert.Contains("a.shield", core.ItemTemplates.Keys);
    }

    [Fact]
    public async Task Delete_CoreDown_Refused_OrBroken_IsAnError()
    {
        var down = Make(new FakeAdminClient { Unavailable = true });
        Assert.Equal(ItemTemplateOutcome.Failed, await down.DeleteAsync("a.b")); Assert.Equal(ItemTemplateListPresenter.UnavailableError, down.Error);
        var refused = Make(new FakeAdminClient { RefuseItemTemplateCalls = true });
        Assert.Equal(ItemTemplateOutcome.Failed, await refused.DeleteAsync("a.b")); Assert.Equal(ItemTemplateListPresenter.ForbiddenError, refused.Error);
        var broken = Make(new FakeAdminClient { Crash = true });
        Assert.Equal(ItemTemplateOutcome.Failed, await broken.DeleteAsync("a.b")); Assert.Equal(ItemTemplateListPresenter.FailedError, broken.Error);
    }
}

public class ItemTemplateReferencesPresenterTests
{
    private static ItemTemplateReferenceRecord Ref(string room, int version, bool stale) => new() { RoomKey = room, EntryId = "s", TemplateVersion = version, Stale = stale };

    [Fact]
    public async Task Load_ReadsTheReferences_SplitsTheStaleOnes_AndSummarises()
    {
        var core = new FakeAdminClient().AddItemTemplate("a.x");
        core.ItemTemplateReferences.References.Add(Ref("a.one", 2, false));
        var p = new ItemTemplateEditPresenter(core, "tok", "a.x");
        Assert.Equal(ItemTemplateEditOutcome.Ok, await p.LoadAsync());
        Assert.Single(p.References);
        Assert.Empty(p.StaleReferences);
        Assert.Equal("1 search reward uses this template; all hold the current version.", p.ReferencesSummary);

        core.ItemTemplateReferences.References.Add(Ref("a.two", 1, true));
        await p.LoadAsync();
        Assert.Equal(new[] { "a.two" }, p.StaleReferences.Select(r => r.RoomKey));
        Assert.Equal("2 search rewards use this template; 1 hold an older version.", p.ReferencesSummary);
        Assert.Contains("ListItemTemplateReferences:a.x", core.ItemTemplateCalls);
        Assert.All(core.ItemTemplateTokens, t => Assert.Equal("tok", t));
    }

    [Fact]
    public async Task Load_WithNoCopies_SaysNoneUseIt()
    {
        var p = new ItemTemplateEditPresenter(new FakeAdminClient().AddItemTemplate("a.x"), "tok", "a.x");
        await p.LoadAsync();
        Assert.Empty(p.References);
        Assert.Null(p.ReferencesError);
        Assert.Equal("No search reward uses this template yet.", p.ReferencesSummary);
    }

    [Fact]
    public async Task Load_ANewTemplateAsksForNoReferences()
    {
        var core = new FakeAdminClient();
        await new ItemTemplateEditPresenter(core, "tok", null).LoadAsync();
        Assert.DoesNotContain(core.ItemTemplateCalls, c => c.StartsWith("ListItemTemplateReferences"));
    }

    [Fact]
    public async Task Load_WhenOnlyTheReferencesFail_TheTemplateStillLoads_AndAnErrorIsKept_ThenClearedByAGoodReload()
    {
        var core = new FakeAdminClient().AddItemTemplate("a.x");
        core.CrashOnReferences = true;
        var p = new ItemTemplateEditPresenter(core, "tok", "a.x");
        Assert.Equal(ItemTemplateEditOutcome.Ok, await p.LoadAsync());
        Assert.Equal(ItemTemplateEditPresenter.ReferencesUnavailableText, p.ReferencesError);
        Assert.Empty(p.References);
        Assert.False(p.IsMissing);

        core.CrashOnReferences = false;
        await p.LoadAsync();
        Assert.Null(p.ReferencesError);
    }

    [Fact]
    public async Task Save_ThenReload_RefreshesTheStaleReport_AgainstTheNewVersion()
    {
        var core = new FakeAdminClient().AddItemTemplate("a.x");
        var p = new ItemTemplateEditPresenter(core, "tok", "a.x");
        await p.LoadAsync();
        core.ItemTemplateReferences.References.Add(Ref("a.one", 1, true));
        p.Short = "changed";
        Assert.Equal(ItemTemplateEditOutcome.Ok, await p.SaveAsync());
        await p.LoadAsync();
        Assert.Single(p.StaleReferences);
    }
}

public class ItemTemplateEditPresenterTests
{
    private const string Token = "token-1";

    private static ItemTemplateEditPresenter Make(FakeAdminClient core, string? id = null) => new(core, Token, id);

    private static async Task<ItemTemplateEditPresenter> NewForm(FakeAdminClient core)
    {
        var p = Make(core);
        await p.LoadAsync();
        p.NewArea = "t"; p.NewLocal = " sword "; p.Short = "a sword"; p.Long = "Sharp.";
        return p;
    }

    [Fact]
    public async Task New_LoadsTheAreas_Sorted_AndComposesTheKeyFromTheDropdownAndTheLocalPart()
    {
        var core = new FakeAdminClient().AddArea("u").AddArea("t", "Test land");
        var p = Make(core);
        Assert.True(p.IsNew);
        Assert.Equal(ItemTemplateEditOutcome.Ok, await p.LoadAsync());
        Assert.Equal(new[] { "t", "u" }, p.Areas.Select(a => a.Id));
        Assert.Equal(string.Empty, p.KeyPrefix);
        Assert.Equal(string.Empty, p.FullId);
        p.NewArea = "t"; p.NewLocal = " sword ";
        Assert.Equal(("t.", "t.sword", "t"), (p.KeyPrefix, p.FullId, p.Area));
        Assert.Contains("version 1", p.VersionHint);
    }

    [Fact]
    public async Task Save_New_SendsTheFullRecord_WithListsSplitOnCommasAndSpaces_AndCreates()
    {
        var core = new FakeAdminClient().AddArea("t");
        var p = await NewForm(core);
        p.Handles = "sword, blade  sword"; p.Adjectives = "iron"; p.Plurals = "swords";
        p.Components = " {\"weight\": 3} ";
        p.AddBehavior();
        p.Behaviors[0].Hook = " item.take "; p.Behaviors[0].Module = "m"; p.Behaviors[0].Type = "T"; p.Behaviors[0].Priority = 4; p.Behaviors[0].Config = "{\"a\":1}";
        Assert.Equal(ItemTemplateEditOutcome.Ok, await p.SaveAsync());
        Assert.Equal("Item template created.", p.Notice);
        var sent = Assert.Single(core.ItemTemplateSaves);
        Assert.Equal(("t.sword", "t", "a sword", "Sharp."), (sent.Id, sent.Area, sent.Short, sent.Long));
        Assert.Equal(new[] { "sword", "blade" }, sent.Handles);
        Assert.Equal(new[] { "iron" }, sent.Adjectives);
        Assert.Equal(new[] { "swords" }, sent.Plurals);
        Assert.Equal("{\"weight\": 3}", sent.ComponentsJson);
        var b = Assert.Single(sent.Behaviors);
        Assert.Equal(("item.take", "m", "T", 4, "{\"a\":1}"), (b.Hook, b.Module, b.Type, b.Priority, b.ConfigJson));
        Assert.Contains("CreateItemTemplate", core.ItemTemplateCalls);
        Assert.Equal(new[] { Token }, core.ItemTemplateTokens.Distinct());
    }

    [Fact]
    public async Task Save_WithNoOptionalParts_SendsEmptyListsAndNoComponents()
    {
        var p = await NewForm(new FakeAdminClient().AddArea("t"));
        Assert.Equal(ItemTemplateEditOutcome.Ok, await p.SaveAsync());
    }

    [Fact]
    public async Task Edit_Loads_FillsEveryField_PrettyPrintsJson_AndWritesTheOriginalId()
    {
        var core = new FakeAdminClient().AddArea("t").AddItemTemplate("t.wand", "a wand");
        var stored = core.ItemTemplates["t.wand"];
        stored.Handles.Add("wand"); stored.Adjectives.Add("oak"); stored.Plurals.Add("wands"); stored.ComponentsJson = "{\"charges\":3}"; stored.Version = 4;
        stored.Behaviors.Add(new RoomBehavior { Hook = "item.take", Module = "m", Type = "T", Priority = 1, ConfigJson = "{\"spell\":\"fire\"}" });
        stored.Behaviors.Add(new RoomBehavior { Hook = "heartbeat", Module = "m", Type = "U", ConfigJson = "{}" });
        var p = Make(core, "t.wand");
        Assert.False(p.IsNew);
        Assert.Equal(ItemTemplateEditOutcome.Ok, await p.LoadAsync());
        Assert.Equal(("t", "a wand", "wand", "oak", "wands", 4), (p.StoredArea, p.Short, p.Handles, p.Adjectives, p.Plurals, p.Version));
        Assert.Contains("\"charges\": 3", p.Components);
        Assert.Equal(2, p.Behaviors.Count);
        Assert.Contains("\"spell\": \"fire\"", p.Behaviors[0].Config);
        Assert.Equal(string.Empty, p.Behaviors[1].Config);
        Assert.Contains("from 4 to 5", p.VersionHint);
        p.NewArea = "other"; p.NewLocal = "other";   // ignored when editing
        p.Short = "a better wand";
        Assert.Equal(ItemTemplateEditOutcome.Ok, await p.SaveAsync());
        var sent = Assert.Single(core.ItemTemplateSaves);
        Assert.Equal(("t.wand", "t"), (sent.Id, sent.Area));
        Assert.Contains("UpdateItemTemplate", core.ItemTemplateCalls);
        Assert.Equal("Item template saved.", p.Notice);
        Assert.Equal(5, p.Version);   // the reply's template refills the form
    }

    [Fact]
    public async Task Edit_OfAnUnknownId_IsMissing_AndCoreProblemsAreMapped()
    {
        var p = Make(new FakeAdminClient(), "t.nope");
        Assert.Equal(ItemTemplateEditOutcome.Missing, await p.LoadAsync());
        Assert.Equal((ItemTemplateMissing.NotFound, ItemTemplateEditPresenter.GoneText), (p.Missing, p.Error));
        Assert.True(p.IsMissing);

        var down = Make(new FakeAdminClient { Unavailable = true }, "t.a");
        await down.LoadAsync(); Assert.Equal((ItemTemplateMissing.Unavailable, ItemTemplateListPresenter.UnavailableError), (down.Missing, down.Error));
        var refused = Make(new FakeAdminClient { RefuseItemTemplateCalls = true }.AddArea("t"), "t.a");
        await refused.LoadAsync(); Assert.Equal((ItemTemplateMissing.Forbidden, ItemTemplateListPresenter.ForbiddenError), (refused.Missing, refused.Error));
        var broken = Make(new FakeAdminClient { Crash = true }, "t.a");
        await broken.LoadAsync(); Assert.Equal((ItemTemplateMissing.Unavailable, ItemTemplateListPresenter.FailedError), (broken.Missing, broken.Error));
    }

    [Theory]
    [InlineData("not json", "is not valid JSON")]
    [InlineData("[1]", "must be a JSON object")]
    [InlineData("\"text\"", "must be a JSON object")]
    public async Task Save_JsonThatIsNotAnObject_IsReportedByThePage_AndNothingIsSent(string json, string expected)
    {
        var core = new FakeAdminClient().AddArea("t");
        var p = await NewForm(core);
        p.Components = json;
        p.AddBehavior(); p.AddBehavior();
        p.Behaviors[0].Config = "{oops";
        p.Behaviors[1].Config = "{\"fine\": true}";
        Assert.Equal(ItemTemplateEditOutcome.Invalid, await p.SaveAsync());
        Assert.Equal(2, p.Problems.Count);
        Assert.Contains(expected, p.Problems[0]); Assert.StartsWith("Component data", p.Problems[0]);
        Assert.StartsWith("Behavior 1 config", p.Problems[1]);
        Assert.Empty(core.ItemTemplateSaves);
        Assert.Null(p.Notice);
    }

    [Fact]
    public async Task Save_CoreInvalid_ShowsEveryFieldMessage_AndKeepsTheInput()
    {
        var core = new FakeAdminClient { ItemTemplateResult = AdminResult.Invalid }.AddArea("t");
        core.ItemTemplateErrors.Add(new FieldError { Field = "Short", Message = "Required." });
        core.ItemTemplateErrors.Add(new FieldError { Field = "", Message = "General." });
        var p = await NewForm(core);
        Assert.Equal(ItemTemplateEditOutcome.Invalid, await p.SaveAsync());
        Assert.Equal(new[] { "Short: Required.", "General." }, p.Problems);
        Assert.Equal("a sword", p.Short);

        core.ItemTemplateErrors.Clear();
        Assert.Equal(ItemTemplateEditOutcome.Invalid, await p.SaveAsync());
        Assert.Equal(ItemTemplateListPresenter.FailedError, p.Error);
    }

    [Theory]
    [InlineData(AdminResult.AlreadyExists, ItemTemplateEditPresenter.ExistsError)]
    [InlineData(AdminResult.Refused, ItemTemplateListPresenter.RefusedError)]
    [InlineData(AdminResult.AuditFailed, ItemTemplateListPresenter.AuditFailedError)]
    [InlineData(AdminResult.Forbidden, ItemTemplateListPresenter.ForbiddenError)]
    [InlineData(AdminResult.Failed, ItemTemplateListPresenter.FailedError)]
    public async Task Save_OtherResults_AreErrors_AndKeepTheForm(AdminResult result, string error)
    {
        var p = await NewForm(new FakeAdminClient { ItemTemplateResult = result }.AddArea("t"));
        Assert.Equal(ItemTemplateEditOutcome.Failed, await p.SaveAsync());
        Assert.Equal(error, p.Error);
        Assert.False(p.IsMissing);
        Assert.Equal("a sword", p.Short);
    }

    [Fact]
    public async Task Save_Refused_UsesCoresFixedMessageWhenThereIsOne()
    {
        var p = await NewForm(new FakeAdminClient { ItemTemplateResult = AdminResult.Refused, ItemTemplateMessage = "The library is down." }.AddArea("t"));
        await p.SaveAsync();
        Assert.Equal("The library is down.", p.Error);
    }

    [Fact]
    public async Task Save_NoChange_SaysSo_AndNotFoundMakesThePageMissing()
    {
        var core = new FakeAdminClient { ItemTemplateResult = AdminResult.NoChange }.AddArea("t");
        var p = await NewForm(core);
        Assert.Equal(ItemTemplateEditOutcome.NoChange, await p.SaveAsync());
        Assert.Equal("Nothing was changed.", p.Notice);
        core.ItemTemplateResult = AdminResult.NotFound;
        Assert.Equal(ItemTemplateEditOutcome.Missing, await p.SaveAsync());
        Assert.Equal(ItemTemplateMissing.NotFound, p.Missing);
    }

    [Fact]
    public async Task Save_CoreDownRefusedOrBroken_IsAnError()
    {
        var down = await NewForm(new FakeAdminClient().AddArea("t"));
        var core = new FakeAdminClient { Unavailable = false }.AddArea("t");
        var p = Make(core); await p.LoadAsync(); p.NewArea = "t"; p.NewLocal = "x"; p.Short = "s"; p.Long = "l";
        core.Unavailable = true;
        Assert.Equal(ItemTemplateEditOutcome.Failed, await p.SaveAsync()); Assert.Equal(ItemTemplateListPresenter.UnavailableError, p.Error);
        core.Unavailable = false; core.RefuseItemTemplateCalls = true;
        Assert.Equal(ItemTemplateEditOutcome.Failed, await p.SaveAsync()); Assert.Equal(ItemTemplateListPresenter.ForbiddenError, p.Error);

        var broken = Make(new FakeAdminClient { Crash = true }); broken.NewArea = "t"; broken.NewLocal = "x";
        Assert.Equal(ItemTemplateEditOutcome.Failed, await broken.SaveAsync()); Assert.Equal(ItemTemplateListPresenter.FailedError, broken.Error);
        Assert.Equal(ItemTemplateEditOutcome.Ok, await down.SaveAsync());
    }

    [Fact]
    public void Behaviors_CanBeAddedAndRemoved_AndABadIndexRemovesNothing()
    {
        var p = Make(new FakeAdminClient());
        p.AddBehavior(); p.AddBehavior(); p.Behaviors[1].Type = "second";
        Assert.False(p.RemoveBehavior(-1)); Assert.False(p.RemoveBehavior(2));
        Assert.True(p.RemoveBehavior(0));
        Assert.Equal("second", Assert.Single(p.Behaviors).Type);
    }

    [Fact]
    public void Preview_ShowsShortAndLong_AndTheWordsAPlayerCanType()
    {
        var p = Make(new FakeAdminClient());
        Assert.Equal((string.Empty, string.Empty), (p.PreviewShort, p.PreviewLong));
        Assert.Empty(p.MatchPhrases);
        p.Short = " a sword "; p.Long = " Sharp. ";
        p.Handles = "sword blade"; p.Adjectives = "iron, sharp";
        Assert.Equal(("a sword", "Sharp."), (p.PreviewShort, p.PreviewLong));
        Assert.Equal(new[] { "sword", "iron sword", "sharp sword", "blade", "iron blade", "sharp blade" }, p.MatchPhrases);
        Assert.False(p.MatchPhrasesTruncated);
        p.Handles = "a b c d"; p.Adjectives = "x y";   // 12 phrases: exactly the cap
        Assert.Equal(12, p.MatchPhrases.Count); Assert.False(p.MatchPhrasesTruncated);
        p.Handles = "a b c d e";
        Assert.Equal(ItemTemplateEditPresenter.MaxMatchPhrases, p.MatchPhrases.Count); Assert.True(p.MatchPhrasesTruncated);
        p.Handles = null; p.Adjectives = "iron";   // adjectives without handles name nothing
        Assert.Empty(p.MatchPhrases);
    }

    [Fact]
    public void TheWordHelpers_SplitJoinCheckAndPrettyPrint()
    {
        Assert.Equal(new[] { "a", "b" }, ItemTemplateWords.Split(" a,b  a "));
        Assert.Empty(ItemTemplateWords.Split(null));
        Assert.Equal("a, b", ItemTemplateWords.Join(new[] { "a", "b" }));
        Assert.Null(ItemTemplateWords.CheckJsonObject(null)); Assert.Null(ItemTemplateWords.CheckJsonObject("  ")); Assert.Null(ItemTemplateWords.CheckJsonObject("{}"));
        Assert.NotNull(ItemTemplateWords.CheckJsonObject("{")); Assert.NotNull(ItemTemplateWords.CheckJsonObject("3"));
        Assert.Equal(string.Empty, ItemTemplateWords.Pretty(null)); Assert.Equal(string.Empty, ItemTemplateWords.Pretty("{}"));
        Assert.Equal("[1]".Replace("[1]", "[\n  1\n]").Replace("\n", Environment.NewLine), ItemTemplateWords.Pretty("[1]"));
        Assert.Equal("not json", ItemTemplateWords.Pretty("not json"));
    }
}

public class ItemTemplatePageRenderTests
{
    private const string Token = "token-1";

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => default;
    }

    private sealed class TestNav : NavigationManager
    {
        public static string? LastNavigation;
        public TestNav() { Initialize("http://localhost/", "http://localhost/creator/items"); }
        protected override void NavigateToCore(string uri, bool forceLoad) { LastNavigation = uri; }
    }

    private sealed class Host<T> : ComponentBase where T : IComponent
    {
        [Parameter] public Dictionary<string, object?> Parameters { get; set; } = new();
        [Parameter] public Action<T>? Capture { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder b)
        {
            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "creator"), new Claim(SessionAuthentication.TokenClaim, Token) }, "test");
            var state = Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
            b.OpenComponent<CascadingValue<Task<AuthenticationState>>>(0);
            b.AddAttribute(1, "Value", state);
            b.AddAttribute(2, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<T>(0);
                var seq = 1;
                foreach (var kv in Parameters) { inner.AddAttribute(seq++, kv.Key, kv.Value); }
                inner.AddComponentReferenceCapture(seq, o => Capture?.Invoke((T)o));
                inner.CloseComponent();
            }));
            b.CloseComponent();
        }
    }

    private sealed class Page<T> : IAsyncDisposable where T : ComponentBase
    {
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private readonly Func<string> _root;
        private readonly T _component;

        private Page(ServiceProvider provider, HtmlRenderer renderer, Func<string> root, T component) { _provider = provider; _renderer = renderer; _root = root; _component = component; }

        public static async Task<Page<T>> OpenAsync(FakeAdminClient core, Dictionary<string, object?>? parameters = null)
        {
            TestNav.LastNavigation = null;
            var services = new ServiceCollection();
            services.AddSingleton<IAdminClient>(core);
            services.AddSingleton<NavigationManager, TestNav>();
            services.AddSingleton<NoticeService>();
            services.AddSingleton<IJSRuntime>(new NoJs());
            services.AddSingleton<DraftStore>();
            services.AddLogging();
            var provider = services.BuildServiceProvider();
            var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
            T? captured = null;
            var root = await renderer.Dispatcher.InvokeAsync(async () =>
                await renderer.RenderComponentAsync<Host<T>>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Parameters"] = new Dictionary<string, object?>(),
                    ["Capture"] = (Action<T>)(c => captured = c),
                })));
            var page = new Page<T>(provider, renderer, () => root.ToHtmlString(), captured!);
            // Query-string parameters cannot be passed as component parameters: set them as the router would, then run the page's parameter hook.
            if (parameters != null && parameters.Count > 0)
            {
                await renderer.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var kv in parameters) { typeof(T).GetProperty(kv.Key)!.SetValue(captured, kv.Value); }
                });
                await page.Invoke("OnParametersSetAsync");
            }
            return page;
        }

        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => _root());

        // Runs a private handler with arguments, then re-renders (what a UI event does).
        public Task Invoke(string method, params object?[] args) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var m = typeof(T).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
            if (m.Invoke(_component, args) is Task t) { await t; }
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_component, null);
        });

        public Task Set(string field, object? value) => _renderer.Dispatcher.InvokeAsync(() =>
            typeof(T).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_component, value));

        // What the live filter does when a typed value settles: run the page's Apply callback and publish its result.
        public Task InvokeApply(string text, string method = "ApplyFilterAsync") => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var m = typeof(T).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
            var publish = await (Task<Action?>)m.Invoke(_component, new object?[] { text, CancellationToken.None })!;
            publish?.Invoke();
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_component, null);
        });

        public T Component => _component;
        public ItemTemplateEditPresenter Presenter => (ItemTemplateEditPresenter)typeof(T).GetField("_presenter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_component)!;
        public ValueTask DisposeAsync() => DisposeAll();
        private async ValueTask DisposeAll() { await _renderer.DisposeAsync(); await _provider.DisposeAsync(); }
    }

    private static FakeAdminClient Seeded()
    {
        var core = new FakeAdminClient().AddArea("a", "Ay land", "The first region.").AddArea("b", "Bee land").AddArea("empty", "Nothing yet")
            .AddItemTemplate("a.sword", "a rusty sword").AddItemTemplate("b.honey", "a pot of honey");
        core.ItemTemplates["a.sword"].Handles.Add("blade");
        core.ItemTemplates["a.sword"].Version = 3;
        return core;
    }

    // ---- routes and menu

    [Fact]
    public void TheMenu_HasAnItemsLink_InTheCreatorSection_AndNotForAPlayer()
    {
        var creator = NavMenu.SectionsFor(creatorOrAdmin: true, admin: false).Single(s => s.Id == "creator");
        Assert.Equal(WebRoutes.ItemTemplates, creator.Items.Single(i => i.Label == "Items").Href);
        Assert.DoesNotContain(NavMenu.SectionsFor(false, false), s => s.Items.Any(i => i.Label == "Items"));
    }

    [Fact]
    public void TheRoutes_AreFixed_AndTheEditRouteEscapesTheId()
    {
        Assert.Equal("/creator/items", WebRoutes.ItemTemplates);
        Assert.Equal("/creator/items/edit", WebRoutes.ItemTemplateEdit());
        Assert.Equal("/creator/items/edit?id=a.sword", WebRoutes.ItemTemplateEdit("a.sword"));
        Assert.Equal("/creator/items/edit?id=a%26b", WebRoutes.ItemTemplateEdit("a&b"));
        Assert.Equal("/creator/items/edit?area=a", WebRoutes.ItemTemplateEdit(null, "a"));
        Assert.Equal("/creator/items/edit?id=a.x", WebRoutes.ItemTemplateEdit("a.x", "a"));
    }

    [Theory]
    [InlineData(typeof(ItemTemplateList), "/creator/items")]
    [InlineData(typeof(ItemTemplateEdit), "/creator/items/edit")]
    public void BothPages_RequireCreatorOrAdmin_OnTheirRoutes(Type type, string route)
    {
        Assert.Equal(WebPolicies.CreatorOrAdmin, type.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal(route, type.GetCustomAttribute<RouteAttribute>()!.Template);
    }

    // ---- the list page

    [Fact]
    public async Task ListPage_GroupsByArea_InNativeDetails_WithNameDescriptionAndCount()
    {
        await using var page = await Page<ItemTemplateList>.OpenAsync(Seeded());
        var html = await page.Html();
        Assert.Contains("<h1>Item templates</h1>", html);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, "<details").Count);
        Assert.Contains("data-area=\"a\"", html);
        Assert.Contains("<strong>Ay land</strong>", html);
        Assert.Contains("(a)", html);
        Assert.Contains("The first region.", html);
        Assert.Contains("<span class=\"mud-itemgroup-count\">1</span>", html);
        Assert.Contains("<span class=\"mud-itemgroup-count\">0</span>", html);
        Assert.Contains("a rusty sword", html);
        Assert.Contains("blade", html);
        Assert.Contains("No item templates in this area.", html);
        Assert.Contains("href=\"/creator/items/edit?id=a.sword\"", html);
        Assert.Contains("href=\"/creator/items/edit?area=a\"", html);
        Assert.Contains("id=\"it-new\"", html);
        Assert.DoesNotContain(" open", html.Substring(html.IndexOf("<details", StringComparison.Ordinal), 60));   // collapsed without a filter
        Assert.Contains("id=\"it-filter\"", html);
    }

    [Fact]
    public async Task ListPage_Filter_ShowsOnlyMatchingGroups_Open_AndSaysWhenNothingMatches()
    {
        await using var page = await Page<ItemTemplateList>.OpenAsync(Seeded());
        await page.InvokeApply("honey");
        var html = await page.Html();
        Assert.Contains("a pot of honey", html);
        Assert.DoesNotContain("a rusty sword", html);
        Assert.DoesNotContain("data-area=\"a\"", html);
        Assert.Contains("open data-area=\"b\"", html);

        await page.InvokeApply("nonsense");
        Assert.Contains("No item template matches the filter.", await page.Html());
    }

    [Fact]
    public async Task ListPage_WithNoAreas_SaysToCreateOneFirst_AndShowsLoadErrors()
    {
        await using (var none = await Page<ItemTemplateList>.OpenAsync(new FakeAdminClient()))
        {
            Assert.Contains("There are no areas yet.", await none.Html());
        }
        await using var down = await Page<ItemTemplateList>.OpenAsync(new FakeAdminClient { Unavailable = true });
        Assert.Contains(ItemTemplateListPresenter.UnavailableError, await down.Html());
    }

    [Fact]
    public async Task ListPage_Delete_AsksFirst_ThenDeletes_AndReloads()
    {
        var core = Seeded();
        await using var page = await Page<ItemTemplateList>.OpenAsync(core);
        await page.Invoke("Ask", core.ItemTemplates["a.sword"]);
        var html = await page.Html();
        Assert.Contains("Confirm delete", html);
        Assert.Contains(ItemTemplateListPresenter.DeleteConfirm, html);
        Assert.Contains("a.sword", core.ItemTemplates.Keys);

        await page.Invoke("CancelDelete");
        Assert.DoesNotContain("Confirm delete", await page.Html());
        await page.Invoke("Ask", core.ItemTemplates["a.sword"]);
        await page.Invoke("ConfirmDeleteAsync");
        html = await page.Html();
        Assert.DoesNotContain("a.sword", core.ItemTemplates.Keys);
        Assert.Contains("Item template deleted.", html);
        Assert.DoesNotContain("a rusty sword", html);
    }

    [Fact]
    public async Task ListPage_DeleteRefused_ShowsCoresMessage_AndKeepsTheTemplate()
    {
        var core = Seeded();
        core.ItemTemplateResult = AdminResult.Refused; core.ItemTemplateMessage = "Two rooms use it.";
        await using var page = await Page<ItemTemplateList>.OpenAsync(core);
        await page.Invoke("Ask", core.ItemTemplates["a.sword"]);
        await page.Invoke("ConfirmDeleteAsync");
        var html = await page.Html();
        Assert.Contains("Two rooms use it.", html);
        Assert.Contains("a rusty sword", html);
    }

    [Fact]
    public async Task ListPage_ConfirmWithNothingAsked_DoesNothing()
    {
        var core = Seeded();
        await using var page = await Page<ItemTemplateList>.OpenAsync(core);
        await page.Invoke("ConfirmDeleteAsync");
        Assert.DoesNotContain(core.ItemTemplateCalls, c => c.StartsWith("DeleteItemTemplate"));
    }

    // ---- the edit page

    private static Dictionary<string, object?> Query(string? id = null, string? area = null) => new() { ["Id"] = id, ["AreaQuery"] = area };

    [Fact]
    public async Task EditPage_New_HasTheAreaDropdown_ThePrefixKeyRow_TheListInputs_AndAnEmptyPreview()
    {
        await using var page = await Page<ItemTemplateEdit>.OpenAsync(Seeded(), Query());
        var html = await page.Html();
        Assert.Contains("<h1>New item template</h1>", html);
        Assert.Contains("<select id=\"it-area\"", html);
        Assert.Contains("a - Ay land", html);
        Assert.Contains("Choose an area...", html);
        Assert.Contains("class=\"mud-keyprefix\"", html);
        Assert.Contains("id=\"it-local\"", html);
        foreach (var id in new[] { "it-short", "it-long", "it-handles", "it-adjectives", "it-plurals", "it-components", "it-add-behavior" }) { Assert.Contains("id=\"" + id + "\"", html); }
        Assert.Contains("(no short description yet)", html);
        Assert.Contains("With no handles a player cannot name this item.", html);
        Assert.Contains("starts at version 1", html);
        Assert.DoesNotContain("id=\"it-id\"", html);
    }

    [Fact]
    public async Task EditPage_New_FromAnAreaGroup_StartsWithThatAreaChosen_AndAnUnknownAreaIsIgnored()
    {
        await using (var page = await Page<ItemTemplateEdit>.OpenAsync(Seeded(), Query(null, "b")))
        {
            Assert.Equal("b", page.Presenter.NewArea);
            Assert.Contains("<span class=\"mud-keyprefix\" id=\"it-prefix\" aria-hidden=\"true\">b.</span>", await page.Html());
        }
        await using var unknown = await Page<ItemTemplateEdit>.OpenAsync(Seeded(), Query(null, "nowhere"));
        Assert.Null(unknown.Presenter.NewArea);
    }

    [Fact]
    public async Task EditPage_Existing_ShowsTheFixedIdAreaAndVersion_AndThePreviewOfTheStoredValues()
    {
        await using var page = await Page<ItemTemplateEdit>.OpenAsync(Seeded(), Query("a.sword"));
        var html = await page.Html();
        Assert.Contains("<h1>Edit item template</h1>", html);
        Assert.Contains("id=\"it-id\" value=\"a.sword\" readonly", html);
        Assert.DoesNotContain("id=\"it-area\"", html);
        Assert.Contains("Version: 3.", html);
        Assert.Contains("from 3 to 4", html);
        Assert.Contains("<strong id=\"it-preview-short\">a rusty sword</strong>", html);
        Assert.Contains("Long a.sword", html);
        Assert.Contains("A player can type: &quot;blade&quot;", html);
    }

    private static ItemTemplateReferenceRecord Ref(string room, string entry, int version, bool stale) =>
        new() { RoomKey = room, EntryId = entry, TemplateVersion = version, Stale = stale };

    [Fact]
    public async Task EditPage_Existing_ListsWhereTheTemplateIsUsed_MarksStaleCopies_AndSaysRefreshIsNotAvailable()
    {
        var core = Seeded();
        core.ItemTemplateReferences = new ListItemTemplateReferencesReply { CurrentVersion = 3 };
        core.ItemTemplateReferences.References.Add(Ref("a.hall", "s1", 3, false));
        core.ItemTemplateReferences.References.Add(Ref("a.vault", "s2", 1, true));
        await using var page = await Page<ItemTemplateEdit>.OpenAsync(core, Query("a.sword"));
        var html = await page.Html();
        Assert.Contains("id=\"it-refs\"", html);
        Assert.Contains("2 search rewards use this template; 1 hold an older version.", html);
        Assert.Contains("<a href=\"/creator/rooms/a.vault\">a.vault</a> (search s2): version 1 - older than the template&#x27;s version 3", html.Replace("&#39;", "&#x27;"));
        Assert.Contains("(search s1): version 3</li>", html);
        Assert.Contains("refreshing them is not available yet", html);
        Assert.Contains("ListItemTemplateReferences:a.sword", core.ItemTemplateCalls);
        // Read-only: no control in the section writes anything.
        var section = html.Substring(html.IndexOf("id=\"it-refs\"", StringComparison.Ordinal), html.IndexOf("id=\"it-preview\"", StringComparison.Ordinal) - html.IndexOf("id=\"it-refs\"", StringComparison.Ordinal));
        Assert.DoesNotContain("<button", section);
        Assert.DoesNotContain("<input", section);
    }

    [Fact]
    public async Task EditPage_Existing_WithNoCopies_SaysSo_AndTheNewFormHasNoUsedBySection()
    {
        await using (var page = await Page<ItemTemplateEdit>.OpenAsync(Seeded(), Query("a.sword")))
        {
            var html = await page.Html();
            Assert.Contains("No search reward uses this template yet.", html);
            Assert.DoesNotContain("class=\"mud-refs\"", html);
        }
        await using var fresh = await Page<ItemTemplateEdit>.OpenAsync(Seeded(), Query());
        Assert.DoesNotContain("id=\"it-refs\"", await fresh.Html());
    }

    [Fact]
    public async Task EditPage_Existing_WhenTheReferencesCannotBeRead_StillShowsTheTemplate_WithANote()
    {
        var core = Seeded();
        core.CrashOnReferences = true;
        await using var page = await Page<ItemTemplateEdit>.OpenAsync(core, Query("a.sword"));
        var html = await page.Html();
        Assert.Contains("id=\"it-id\" value=\"a.sword\"", html);
        Assert.Contains("id=\"it-refs-error\"", html);
        Assert.Contains(ItemTemplateEditPresenter.ReferencesUnavailableText, html);
        Assert.DoesNotContain("id=\"it-refs-summary\"", html);
    }

    [Fact]
    public async Task TheUsedByCss_UsesPaletteVariablesOnly_AndAFullSizeTouchTarget()
    {
        var css = await File.ReadAllTextAsync(FindFile("MUD.Web/wwwroot/css/mobile.css"));
        var block = string.Join("\n", css.Split('\n').Where(l => l.TrimStart().StartsWith(".mud-refs")));
        Assert.Contains("min-height: var(--mud-hit)", block);
        Assert.Contains("overflow-wrap: anywhere", block);
        Assert.DoesNotMatch("#[0-9a-fA-F]{3,8}\\b", block);
        Assert.DoesNotMatch(@"rgb|hsl", block);
    }

    [Fact]
    public async Task EditPage_Preview_ListsAdjectiveHandlePairs_AndNotesATruncatedList()
    {
        var core = Seeded();
        core.ItemTemplates["a.sword"].Handles.AddRange(new[] { "sword", "steel", "edge", "iron" });
        core.ItemTemplates["a.sword"].Adjectives.AddRange(new[] { "rusty", "old" });
        await using var page = await Page<ItemTemplateEdit>.OpenAsync(core, Query("a.sword"));
        var html = await page.Html();
        Assert.Contains("&quot;rusty sword&quot;", html);
        Assert.EndsWith(", ...", System.Text.RegularExpressions.Regex.Match(html, "A player can type:[^<]*").Value);
    }

    [Fact]
    public async Task EditPage_MissingTemplate_ShowsTheMessageAndABackLink()
    {
        await using var page = await Page<ItemTemplateEdit>.OpenAsync(Seeded(), Query("a.nothing"));
        var html = await page.Html();
        Assert.Contains(ItemTemplateEditPresenter.GoneText, html);
        Assert.Contains("href=\"/creator/items\"", html);
        Assert.DoesNotContain("<form", html);
    }

    [Fact]
    public async Task EditPage_Behaviors_ShowTheRows_WithHookModuleTypePriorityAndConfig_AndTheHookSuggestions()
    {
        var core = Seeded();
        core.ItemTemplates["a.sword"].Behaviors.Add(new RoomBehavior { Hook = "item.take", Module = "desert", Type = "Trap", Priority = 7, ConfigJson = "{\"dc\":12}" });
        await using var page = await Page<ItemTemplateEdit>.OpenAsync(core, Query("a.sword"));
        var html = await page.Html();
        Assert.Contains("Behaviors (1)", html);
        Assert.Contains("id=\"it-b-hook-0\"", html);
        Assert.Contains("value=\"item.take\"", html);
        Assert.Contains("value=\"desert\"", html);
        Assert.Contains("value=\"Trap\"", html);
        Assert.Contains("value=\"7\"", html);
        Assert.Contains("&quot;dc&quot;: 12", html);
        Assert.Contains("<datalist id=\"it-hooks\">", html);
        Assert.Contains("<option value=\"heartbeat\">", html);
        Assert.Contains("Remove behavior 1", html);
    }

    [Fact]
    public async Task EditPage_AddingAndRemovingBehaviorRows_Works()
    {
        await using var page = await Page<ItemTemplateEdit>.OpenAsync(Seeded(), Query("a.sword"));
        Assert.Contains("Behaviors (0)", await page.Html());
        await page.Invoke("OnParametersSetAsync");   // same query: nothing reloads, nothing breaks
        page.Presenter.AddBehavior();
        await page.Invoke("OnParametersSetAsync");
        Assert.Contains("Behaviors (1)", await page.Html());
        page.Presenter.RemoveBehavior(0);
        await page.Invoke("OnParametersSetAsync");
        Assert.Contains("Behaviors (0)", await page.Html());
    }

    [Fact]
    public async Task EditPage_SaveNew_Creates_AndGoesToTheEditPageOfTheNewTemplate()
    {
        var core = Seeded();
        await using var page = await Page<ItemTemplateEdit>.OpenAsync(core, Query());
        var p = page.Presenter;
        p.NewArea = "b"; p.NewLocal = "comb"; p.Short = "a honeycomb"; p.Long = "Sticky.";
        await page.Invoke("SaveAsync");
        Assert.Contains("b.comb", core.ItemTemplates.Keys);
        Assert.Contains("/creator/items/edit?id=b.comb", TestNav.LastNavigation);
    }

    [Fact]
    public async Task EditPage_SaveExisting_Saves_ReloadsAndShowsTheNewVersion()
    {
        var core = Seeded();
        await using var page = await Page<ItemTemplateEdit>.OpenAsync(core, Query("a.sword"));
        page.Presenter.Short = "a shiny sword";
        await page.Invoke("SaveAsync");
        var html = await page.Html();
        Assert.Equal("a shiny sword", core.ItemTemplates["a.sword"].Short);
        Assert.Contains("Item template saved.", html);
        Assert.Contains("Version: 4.", html);
        Assert.Null(TestNav.LastNavigation);
    }

    [Fact]
    public async Task EditPage_CoreInvalid_ListsTheProblems_AndStaysOnThePage()
    {
        var core = Seeded();
        core.ItemTemplateResult = AdminResult.Invalid;
        core.ItemTemplateErrors.Add(new FieldError { Field = "Area", Message = "There is no area with that prefix. Create the area first." });
        await using var page = await Page<ItemTemplateEdit>.OpenAsync(core, Query());
        page.Presenter.NewArea = "a"; page.Presenter.NewLocal = "x"; page.Presenter.Short = "s"; page.Presenter.Long = "l";
        await page.Invoke("SaveAsync");
        var html = await page.Html();
        Assert.Contains("Area: There is no area with that prefix.", html);
        Assert.Null(TestNav.LastNavigation);
        Assert.Contains("value=\"x\"", html);   // the input stays
    }

    [Fact]
    public async Task EditPage_BadJson_IsShownByThePage_AndNothingIsSent()
    {
        var core = Seeded();
        await using var page = await Page<ItemTemplateEdit>.OpenAsync(core, Query("a.sword"));
        page.Presenter.Components = "{broken";
        await page.Invoke("SaveAsync");
        Assert.Contains("Component data: is not valid JSON.", await page.Html());
        Assert.Empty(core.ItemTemplateSaves);
    }

    [Fact]
    public async Task EditPage_SaveWithNoChange_SaysNothingChanged_AndStays()
    {
        var core = Seeded();
        core.ItemTemplateResult = AdminResult.NoChange;
        await using var page = await Page<ItemTemplateEdit>.OpenAsync(core, Query("a.sword"));
        await page.Invoke("SaveAsync");
        Assert.Contains("Nothing was changed.", await page.Html());
        Assert.Null(TestNav.LastNavigation);
    }

    [Fact]
    public async Task EditPage_ParseInt_KeepsThePriorValueOnJunk()
    {
        var m = typeof(ItemTemplateEdit).GetMethod("ParseInt", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(5, m.Invoke(null, new object?[] { "5", 1 }));
        Assert.Equal(1, m.Invoke(null, new object?[] { "x", 1 }));
        Assert.Equal(1, m.Invoke(null, new object?[] { null, 1 }));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task EditPage_ASecondSaveWhileBusy_IsIgnored()
    {
        var core = Seeded();
        await using var page = await Page<ItemTemplateEdit>.OpenAsync(core, Query("a.sword"));
        await page.Set("_busy", true);
        page.Presenter.Short = "changed";
        await page.Invoke("SaveAsync");
        Assert.Empty(core.ItemTemplateSaves);
    }

    // ---- phone layout rules (palette variables only; touch targets; no fixed widths)

    [Fact]
    public void TheNewCss_UsesPaletteVariablesOnly_AndNoHexColoursOrFixedWidths()
    {
        var css = File.ReadAllText(FindFile("MUD.Web/wwwroot/css/mobile.css"));
        var block = css.Substring(css.IndexOf("/* Item templates:", StringComparison.Ordinal));
        Assert.DoesNotMatch("#[0-9a-fA-F]{3,8}\\b", block);
        Assert.DoesNotMatch("width:\\s*\\d+px", block);   // no fixed pixel widths in the item rules
        Assert.Contains("var(--mud-text-soft)", block);
    }

    private static string FindFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, relative))) { dir = dir.Parent; }
        return Path.Combine(dir!.FullName, relative);
    }
}
