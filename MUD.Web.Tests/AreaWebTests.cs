using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MUD.Contracts;
using MUD.Web.Components.Pages;
using MUD.Web.Pages;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

// The areas page, the area dropdowns and the client calls (specs/WEB_BLAZOR.md "Areas"). Pages are rendered with HtmlRenderer over FakeAdminClient.
public class AreaGrpcClientTests : IDisposable
{
    private sealed class StandIn : Admin.AdminBase
    {
        private static void RequireToken(string token)
        {
            if (token == "stale") { throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unauthenticated, "no")); }
            if (token == "low") { throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.PermissionDenied, "no")); }
            if (token == "busy") { throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "no")); }
        }

        private static AreaEditReply Answer(string id) => id switch
        {
            "taken" => new AreaEditReply { Result = AdminResult.AlreadyExists, Message = "exists" },
            "gone" => new AreaEditReply { Result = AdminResult.NotFound, Message = "missing" },
            "used" => new AreaEditReply { Result = AdminResult.Refused, Message = "3 rooms still use this area." },
            "bad" => new AreaEditReply { Result = AdminResult.Invalid, Errors = { new FieldError { Field = "Name", Message = "required" } } },
            _ => new AreaEditReply { Result = AdminResult.Ok, Area = new AreaRecord { Id = id, Name = "N" } },
        };

        public override Task<ListAreasReply> ListAreas(TokenRequest request, Grpc.Core.ServerCallContext context)
        {
            RequireToken(request.Token);
            var reply = new ListAreasReply();
            reply.Areas.Add(new AreaRecord { Id = "a1", Name = "Area one", Description = "d", RoomCount = 2 });
            return Task.FromResult(reply);
        }

        public override Task<GetAreaReply> GetArea(AreaIdRequest request, Grpc.Core.ServerCallContext context)
        {
            RequireToken(request.Token);
            return Task.FromResult(request.Id == "a1" ? new GetAreaReply { Found = true, Area = new AreaRecord { Id = "a1", Name = "Area one" } } : new GetAreaReply());
        }

        public override Task<AreaEditReply> CreateArea(SaveAreaRequest request, Grpc.Core.ServerCallContext context) { RequireToken(request.Token); return Task.FromResult(Answer(request.Area.Id)); }
        public override Task<AreaEditReply> UpdateArea(SaveAreaRequest request, Grpc.Core.ServerCallContext context) { RequireToken(request.Token); return Task.FromResult(Answer(request.Area.Id)); }
        public override Task<AreaEditReply> DeleteArea(AreaIdRequest request, Grpc.Core.ServerCallContext context) { RequireToken(request.Token); return Task.FromResult(Answer(request.Id)); }
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wa" + Guid.NewGuid().ToString("N").Substring(0, 8));
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
    public async Task ListAndGet_AndGetOfAnUnknownIdIsNull()
    {
        var client = await Start();
        var listed = Assert.Single(await client.ListAreasAsync("tok"));
        Assert.Equal(("a1", "Area one", "d", 2), (listed.Id, listed.Name, listed.Description, listed.RoomCount));
        Assert.Equal("a1", (await client.GetAreaAsync("tok", "a1"))!.Id);
        Assert.Null(await client.GetAreaAsync("tok", "zz"));
    }

    [Theory]
    [InlineData("ok", AdminResult.Ok)]
    [InlineData("taken", AdminResult.AlreadyExists)]
    [InlineData("gone", AdminResult.NotFound)]
    [InlineData("used", AdminResult.Refused)]
    [InlineData("bad", AdminResult.Invalid)]
    public async Task WriteCalls_PassEveryResultBranchThrough(string id, AdminResult expected)
    {
        var client = await Start();
        Assert.Equal(expected, (await client.CreateAreaAsync("tok", new AreaRecord { Id = id })).Result);
        Assert.Equal(expected, (await client.UpdateAreaAsync("tok", new AreaRecord { Id = id })).Result);
        Assert.Equal(expected, (await client.DeleteAreaAsync("tok", id)).Result);
    }

    [Fact]
    public async Task TheRefusalMessageAndTheFieldErrors_ComeThrough()
    {
        var client = await Start();
        Assert.Equal("3 rooms still use this area.", (await client.DeleteAreaAsync("tok", "used")).Message);
        var invalid = await client.CreateAreaAsync("tok", new AreaRecord { Id = "bad" });
        Assert.Equal(("Name", "required"), (invalid.Errors[0].Field, invalid.Errors[0].Message));
    }

    [Fact]
    public async Task CoresStatuses_MapOntoTheTwoExceptions()
    {
        var client = await Start();
        Assert.True((await Assert.ThrowsAsync<CoreRefusedException>(() => client.ListAreasAsync("stale"))).SessionEnded);
        Assert.False((await Assert.ThrowsAsync<CoreRefusedException>(() => client.GetAreaAsync("low", "a1"))).SessionEnded);
        Assert.False((await Assert.ThrowsAsync<CoreRefusedException>(() => client.CreateAreaAsync("low", new AreaRecord { Id = "x" }))).SessionEnded);
        await Assert.ThrowsAsync<CoreUnavailableException>(() => client.ListAreasAsync("busy"));
        await Assert.ThrowsAsync<CoreUnavailableException>(() => client.DeleteAreaAsync("busy", "x"));
        await Assert.ThrowsAsync<CoreUnavailableException>(() => client.UpdateAreaAsync("busy", new AreaRecord { Id = "x" }));
    }

    [Fact]
    public async Task WhenNothingListens_EveryAreaCallReportsCoreUnavailable()
    {
        Directory.CreateDirectory(_directory);
        var client = new GrpcAdminClient(new Admin.AdminClient(CoreSocket.OpenChannel(Path.Combine(_directory, "none.sock"))));
        await Assert.ThrowsAsync<CoreUnavailableException>(() => client.ListAreasAsync("tok"));
        await Assert.ThrowsAsync<CoreUnavailableException>(() => client.GetAreaAsync("tok", "x"));
        await Assert.ThrowsAsync<CoreUnavailableException>(() => client.CreateAreaAsync("tok", new AreaRecord { Id = "x" }));
    }
}

public class FakeAreaClientTests
{
    [Fact]
    public async Task OkWrites_ChangeTheStore_AndRecordTheTokenAndCall()
    {
        var core = new FakeAdminClient();
        Assert.Equal(AdminResult.Ok, (await core.CreateAreaAsync("tok", new AreaRecord { Id = "a", Name = "A" })).Result);
        Assert.Equal("a", Assert.Single(await core.ListAreasAsync("tok")).Id);
        Assert.Equal("a", (await core.GetAreaAsync("tok", "a"))!.Id);
        Assert.Null(await core.GetAreaAsync("tok", "zz"));
        Assert.Equal(AdminResult.Ok, (await core.UpdateAreaAsync("tok", new AreaRecord { Id = "a", Name = "B" })).Result);
        Assert.Equal("B", core.Areas["a"].Name);
        Assert.Equal(AdminResult.Ok, (await core.DeleteAreaAsync("tok", "a")).Result);
        Assert.Empty(core.Areas);
        Assert.All(core.AreaTokens, t => Assert.Equal("tok", t));
    }

    [Theory]
    [InlineData(AdminResult.AlreadyExists)]
    [InlineData(AdminResult.NotFound)]
    [InlineData(AdminResult.Refused)]
    [InlineData(AdminResult.Invalid)]
    public async Task ANonOkResult_IsAnswered_AndTheStoreIsLeftAlone(AdminResult result)
    {
        var core = new FakeAdminClient { AreaResult = result, AreaMessage = "fixed" }.AddArea("keep");
        var reply = await core.DeleteAreaAsync("tok", "keep");
        Assert.Equal((result, "fixed"), (reply.Result, reply.Message));
        Assert.Single(core.Areas);
        Assert.Equal(result, (await core.CreateAreaAsync("tok", new AreaRecord { Id = "n" })).Result);
        Assert.False(core.Areas.ContainsKey("n"));
    }

    [Fact]
    public async Task UnavailableAndRefusedAreRaisedByEveryAreaCall()
    {
        var down = new FakeAdminClient { Unavailable = true };
        await Assert.ThrowsAsync<CoreUnavailableException>(() => down.ListAreasAsync("tok"));
        var refusing = new FakeAdminClient { RefuseAreaCalls = true };
        await Assert.ThrowsAsync<CoreRefusedException>(() => refusing.GetAreaAsync("tok", "a"));
    }
}

public class AreaListPresenterTests
{
    private const string Token = "token-1";

    private static AreaListPresenter Make(FakeAdminClient core) => new(core, Token);

    [Fact]
    public async Task Load_ListsTheAreasSortedByPrefix_WithRoomCounts_AndSendsTheToken()
    {
        var core = new FakeAdminClient().AddArea("b", "Bee", "bees", 2).AddArea("a", "Ay", "", 0);
        var p = Make(core);
        Assert.Equal(AreaOutcome.Ok, await p.LoadAsync());
        Assert.Equal(new[] { "a", "b" }, p.Areas.Select(a => a.Id));
        Assert.Equal(2, p.Areas[1].RoomCount);
        Assert.Equal(new[] { Token }, core.AreaTokens.Distinct());
    }

    [Theory]
    [InlineData(0, AreaListPresenter.UnavailableError)]
    [InlineData(1, AreaListPresenter.ForbiddenError)]
    public async Task Load_CoreUnavailableOrRefused_ShowsAFixedError_AndNoRows(int how, string expected)
    {
        var p = Make(how == 0 ? new FakeAdminClient { Unavailable = true } : new FakeAdminClient { RefuseAreaCalls = true });
        Assert.Equal(AreaOutcome.Failed, await p.LoadAsync());
        Assert.Equal(expected, p.Error);
        Assert.Empty(p.Areas);
    }

    [Fact]
    public async Task Create_Ok_SendsTheTypedPrefix_ClosesTheForm()
    {
        var core = new FakeAdminClient();
        var p = Make(core);
        p.BeginNew();
        Assert.True(p.IsNew);
        p.FormId = "sample_area"; p.FormName = "Sample area"; p.FormDescription = "Tiny.";
        Assert.Equal(AreaOutcome.Ok, await p.SaveAsync());
        Assert.Equal(("Sample area", "Tiny."), (core.Areas["sample_area"].Name, core.Areas["sample_area"].Description));
        Assert.Equal("Area created.", p.Notice);
        Assert.False(p.FormOpen);
        Assert.Contains("CreateArea", core.AreaCalls);
    }

    [Fact]
    public async Task Edit_WritesTheOriginalId_NeverTheTypedOne_AndSavesNameAndDescriptionOnly()
    {
        var core = new FakeAdminClient().AddArea("keep", "Old", "old");
        var p = Make(core);
        await p.LoadAsync();
        Assert.True(p.BeginEdit("keep"));
        Assert.False(p.IsNew);
        p.FormId = "renamed";   // the form's id field is read-only, but even if it were set it is not used
        p.FormName = "New"; p.FormDescription = "new";
        Assert.Equal(AreaOutcome.Ok, await p.SaveAsync());
        Assert.Equal(new[] { "keep" }, core.Areas.Keys);
        Assert.Equal(("New", "new"), (core.Areas["keep"].Name, core.Areas["keep"].Description));
        Assert.Contains("UpdateArea", core.AreaCalls);
        Assert.Equal("Area saved.", p.Notice);
    }

    [Fact]
    public async Task BeginEdit_OfAnUnknownArea_OpensNothing()
    {
        var p = Make(new FakeAdminClient().AddArea("a"));
        await p.LoadAsync();
        Assert.False(p.BeginEdit("zzz"));
        Assert.False(p.FormOpen);
    }

    [Fact]
    public async Task Save_NoChange_SaysSo_AndClosesTheForm()
    {
        var core = new FakeAdminClient { AreaResult = AdminResult.NoChange }.AddArea("a");
        var p = Make(core);
        await p.LoadAsync();
        p.BeginEdit("a");
        Assert.Equal(AreaOutcome.Ok, await p.SaveAsync());
        Assert.Equal("Nothing was changed.", p.Notice);
        Assert.False(p.FormOpen);
    }

    [Fact]
    public async Task Save_Invalid_ListsCoresMessages_KeepsTheForm_AndTheInput()
    {
        var core = new FakeAdminClient { AreaResult = AdminResult.Invalid };
        core.AreaErrors.Add(new FieldError { Field = "Name", Message = "Name is required." });
        core.AreaErrors.Add(new FieldError { Field = "", Message = "General." });
        var p = Make(core);
        p.BeginNew();
        p.FormId = "x";
        Assert.Equal(AreaOutcome.Failed, await p.SaveAsync());
        Assert.Equal(new[] { "Name: Name is required.", "General." }, p.Problems);
        Assert.True(p.FormOpen);
        Assert.Equal("x", p.FormId);
    }

    [Fact]
    public async Task Save_InvalidWithNoMessages_ShowsAGenericError()
    {
        var p = Make(new FakeAdminClient { AreaResult = AdminResult.Invalid });
        p.BeginNew();
        await p.SaveAsync();
        Assert.Equal(AreaListPresenter.FailedError, p.Error);
    }

    [Theory]
    [InlineData(AdminResult.AlreadyExists, AreaListPresenter.ExistsError)]
    [InlineData(AdminResult.NotFound, AreaListPresenter.GoneError)]
    [InlineData(AdminResult.Refused, AreaListPresenter.RefusedError)]
    [InlineData(AdminResult.AuditFailed, AreaListPresenter.AuditFailedError)]
    [InlineData(AdminResult.Forbidden, AreaListPresenter.ForbiddenError)]
    [InlineData(AdminResult.Failed, AreaListPresenter.FailedError)]
    public async Task Save_EveryOtherResult_ShowsItsFixedSentence_AndKeepsTheForm(AdminResult result, string expected)
    {
        var p = Make(new FakeAdminClient { AreaResult = result });
        p.BeginNew();
        p.FormId = "a"; p.FormName = "A";
        Assert.Equal(AreaOutcome.Failed, await p.SaveAsync());
        Assert.Equal(expected, p.Error);
        Assert.True(p.FormOpen);
    }

    [Fact]
    public async Task Save_CoreUnavailableOrRefusedOrThrowing_ShowsAFixedError()
    {
        var down = Make(new FakeAdminClient { Unavailable = true });
        down.BeginNew();
        await down.SaveAsync();
        Assert.Equal(AreaListPresenter.UnavailableError, down.Error);
        var refused = Make(new FakeAdminClient { RefuseAreaCalls = true });
        refused.BeginNew();
        await refused.SaveAsync();
        Assert.Equal(AreaListPresenter.ForbiddenError, refused.Error);
        var closed = Make(new FakeAdminClient());
        Assert.Equal(AreaOutcome.Failed, await closed.SaveAsync());   // no open form: nothing is sent
    }

    [Fact]
    public async Task Delete_Ok_And_NotFound_AreBothFinished()
    {
        var core = new FakeAdminClient().AddArea("a");
        var p = Make(core);
        Assert.Equal(AreaOutcome.Ok, await p.DeleteAsync("a"));
        Assert.Equal("Area deleted.", p.Notice);
        Assert.Empty(core.Areas);
        core.AreaResult = AdminResult.NotFound;
        Assert.Equal(AreaOutcome.Ok, await p.DeleteAsync("a"));
        Assert.Equal(AreaListPresenter.GoneError, p.Notice);
    }

    [Fact]
    public async Task Delete_RefusedWhileRoomsUseIt_ShowsCoresMessageWithTheCount()
    {
        var core = new FakeAdminClient { AreaResult = AdminResult.Refused, AreaMessage = "3 rooms still use this area. Move or delete them first." }.AddArea("a", rooms: 3);
        var p = Make(core);
        Assert.Equal(AreaOutcome.Failed, await p.DeleteAsync("a"));
        Assert.Equal("3 rooms still use this area. Move or delete them first.", p.Error);
        Assert.Single(core.Areas);
        core.AreaMessage = string.Empty;
        await p.DeleteAsync("a");
        Assert.Equal(AreaListPresenter.RefusedError, p.Error);
    }

    [Theory]
    [InlineData(AdminResult.AuditFailed, AreaListPresenter.AuditFailedError)]
    [InlineData(AdminResult.Forbidden, AreaListPresenter.ForbiddenError)]
    [InlineData(AdminResult.Failed, AreaListPresenter.FailedError)]
    public async Task Delete_OtherResults_ShowTheirFixedSentence(AdminResult result, string expected)
    {
        var p = Make(new FakeAdminClient { AreaResult = result }.AddArea("a"));
        Assert.Equal(AreaOutcome.Failed, await p.DeleteAsync("a"));
        Assert.Equal(expected, p.Error);
    }

    [Fact]
    public async Task Delete_CoreUnavailableOrRefused_ShowsAFixedError()
    {
        var down = Make(new FakeAdminClient { Unavailable = true });
        await down.DeleteAsync("a");
        Assert.Equal(AreaListPresenter.UnavailableError, down.Error);
        var refused = Make(new FakeAdminClient { RefuseAreaCalls = true });
        await refused.DeleteAsync(null);
        Assert.Equal(AreaListPresenter.ForbiddenError, refused.Error);
    }

    [Fact]
    public async Task CancelForm_ClosesIt_AndClearsProblems()
    {
        var p = Make(new FakeAdminClient { AreaResult = AdminResult.Invalid });
        p.BeginNew();
        p.FormId = "a";
        await p.SaveAsync();
        p.CancelForm();
        Assert.False(p.FormOpen);
        Assert.Empty(p.Problems);
    }
}

public class AreaDropdownPresenterTests
{
    private static CreatorPresenter Creator(FakeAdminClient core) => new(new RoomCreationService(core), "abc");

    [Fact]
    public async Task Creator_KeyPrefix_FollowsTheChosenArea_AndIsEmptyBeforeOrForAnUnknownOne()
    {
        var p = Creator(new FakeAdminClient().AddArea("sample_area", "Sample area").AddArea("desert"));
        await p.LoadAreasAsync();
        Assert.Equal(new[] { "desert", "sample_area" }, p.Areas.Select(a => a.Id));
        Assert.False(p.AreaChosen);
        Assert.Equal(string.Empty, p.KeyPrefix);
        p.Form.Area = "sample_area";
        Assert.True(p.AreaChosen);
        Assert.Equal("sample_area.", p.KeyPrefix);
        p.Form.Area = "not_an_area";
        Assert.False(p.AreaChosen);
        Assert.Equal(string.Empty, p.KeyPrefix);
    }

    [Fact]
    public async Task Creator_TheStoredKeyIsThePrefixPlusTheLocalPart_SoTheyCannotDisagree()
    {
        var core = new FakeAdminClient().AddArea("sample_area");
        var p = Creator(core);
        await p.LoadAreasAsync();
        p.Form.Area = "sample_area"; p.Form.Key = "hall"; p.Form.Short = "a hall"; p.Form.Long = "A hall.";
        Assert.Equal(CreatorOutcome.Created, await p.CreateAsync());
        Assert.Equal("sample_area.hall", p.CreatedKey);
        Assert.Equal("sample_area.hall", core.RoomCreates.Single().Key);
        Assert.Equal("sample_area", core.RoomCreates.Single().Area);
    }

    [Fact]
    public async Task Creator_AreasAreEmpty_WhenCoreCannotAnswer()
    {
        var p = Creator(new FakeAdminClient { Unavailable = true });
        await p.LoadAreasAsync();
        Assert.Empty(p.Areas);
        var refused = Creator(new FakeAdminClient { RefuseAreaCalls = true });
        await refused.LoadAreasAsync();
        Assert.Empty(refused.Areas);
    }

    [Fact]
    public async Task RoomEdit_MoveAreas_AreTheAreaRecords_OrNoneWhenCoreCannotAnswer()
    {
        var core = new FakeAdminClient().AddRoom("t.hall").AddArea("z").AddArea("t").AddArea("empty");
        var p = new RoomEditPresenter(core, "tok", "t.hall");
        Assert.Equal(new[] { "empty", "t", "z" }, (await p.GetAreasAsync()).Select(a => a.Id));
        Assert.Empty(await new RoomEditPresenter(new FakeAdminClient { Unavailable = true }, "tok", "t.hall").GetAreasAsync());
        Assert.Empty(await new RoomEditPresenter(new FakeAdminClient { RefuseAreaCalls = true }, "tok", "t.hall").GetAreasAsync());
    }

    [Fact]
    public async Task Map_ListsEmptyAreasToo_LabelsThemWithTheirName_AndKeepsRoomAreasWithoutARecord()
    {
        var core = new FakeAdminClient().AddArea("a", "Alpha").AddArea("empty", "Nothing yet").AddRoom("a.x", "a").AddRoom("legacy.x", "legacy");
        var p = new CreatorMapPresenter(core, "tok");
        Assert.Equal(MapOutcome.Ok, await p.LoadAsync(null, null));
        Assert.Equal(new[] { "a", "empty", "legacy" }, p.Areas);
        Assert.Equal("a - Alpha", p.LabelFor("a"));
        Assert.Equal("empty - Nothing yet", p.LabelFor("empty"));
        Assert.Equal("legacy", p.LabelFor("legacy"));
        Assert.Equal("(no area)", p.LabelFor(string.Empty));
        await p.LoadAsync("empty", null);
        Assert.Equal("empty", p.Area);
        Assert.Empty(p.Map!.Rects);
    }

    [Fact]
    public async Task Map_WhenTheAreaListIsRefused_TheRoomsAloneGiveTheList()
    {
        var core = new FakeAdminClient { RefuseAreaCalls = true }.AddRoom("a.x", "a");
        var p = new CreatorMapPresenter(core, "tok");
        Assert.Equal(MapOutcome.Ok, await p.LoadAsync(null, null));
        Assert.Equal(new[] { "a" }, p.Areas);
        Assert.Equal("a", p.LabelFor("a"));
    }

    [Fact]
    public async Task Map_AnAreaRecordWithABlankName_IsLabelledByItsPrefix()
    {
        var core = new FakeAdminClient().AddRoom("a.x", "a");
        core.Areas["a"] = new AreaRecord { Id = "a", Name = " " };
        var p = new CreatorMapPresenter(core, "tok");
        await p.LoadAsync(null, null);
        Assert.Equal("a", p.LabelFor("a"));
    }
}

// Pages rendered for real.
public class AreaPageRenderTests
{
    private const string Token = "token-1";

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => default;
    }

    private sealed class TestNav : NavigationManager
    {
        public TestNav() { Initialize("http://localhost/", "http://localhost/creator"); }
        protected override void NavigateToCore(string uri, bool forceLoad) { }
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
            var services = new ServiceCollection();
            services.AddSingleton<IAdminClient>(core);
            services.AddSingleton<IRoomCreationService>(new RoomCreationService(core));
            services.AddSingleton<NavigationManager, TestNav>();
            services.AddSingleton<NoticeService>();
            services.AddSingleton<IJSRuntime>(new NoJs());
            services.AddSingleton<DraftStore>();
            services.AddScoped<RoomDirectory>();
            services.AddSingleton(new AssetVersions(new Microsoft.Extensions.FileProviders.NullFileProvider()));
            services.AddLogging();
            var provider = services.BuildServiceProvider();
            var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
            T? captured = null;
            var root = await renderer.Dispatcher.InvokeAsync(async () =>
                await renderer.RenderComponentAsync<Host<T>>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Parameters"] = parameters ?? new Dictionary<string, object?>(),
                    ["Capture"] = (Action<T>)(c => captured = c),
                })));
            return new Page<T>(provider, renderer, () => root.ToHtmlString(), captured!);
        }

        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => _root());

        public Task Call(string method) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var m = typeof(T).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
            if (m.Invoke(_component, null) is Task t) { await t; }
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_component, null);
        });

        // Sets a private field of the page (a UI event would do this through a handler).
        public Task Set(string field, object? value) => _renderer.Dispatcher.InvokeAsync(() =>
            typeof(T).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_component, value));

        // Runs a private handler with arguments, then re-renders (what a UI event does).
        public Task Invoke(string method, params object?[] args) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var m = typeof(T).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
            if (m.Invoke(_component, args) is Task t) { await t; }
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_component, null);
        });

        // Re-renders without running a handler.
        public Task Refresh() => _renderer.Dispatcher.InvokeAsync(() =>
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_component, null));

        public T Component => _component;
        public ValueTask DisposeAsync() => DisposeAll();
        private async ValueTask DisposeAll() { await _renderer.DisposeAsync(); await _provider.DisposeAsync(); }
    }

    // ---- routes and menu

    [Fact]
    public void ThePage_RequiresCreatorOrAdmin_OnTheAreasRoute()
    {
        var type = typeof(AreaList);
        Assert.Equal(WebPolicies.CreatorOrAdmin, type.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal(WebRoutes.Areas, type.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal("/creator/areas", WebRoutes.Areas);
    }

    [Fact]
    public void TheMenu_HasAnAreasLink_InTheCreatorSection_AndNotForAPlayer()
    {
        var creator = NavMenu.SectionsFor(creatorOrAdmin: true, admin: false).Single(s => s.Id == "creator");
        Assert.Equal(WebRoutes.Areas, creator.Items.Single(i => i.Label == "Areas").Href);
        Assert.DoesNotContain(NavMenu.SectionsFor(false, false), s => s.Items.Any(i => i.Label == "Areas"));
    }

    // ---- the areas page

    [Fact]
    public async Task AreasPage_ListsPrefixNameDescriptionAndRoomCount_AsATableAndAsStackedCards_WithTheHelpText()
    {
        var core = new FakeAdminClient().AddArea("sample_area", "Sample area", "A tiny place.", 4).AddArea("desert", "Desert", "", 0);
        await using var page = await Page<AreaList>.OpenAsync(core);
        var html = await page.Html();
        Assert.Contains("<h1>Areas</h1>", html);
        Assert.Contains("<th>Prefix</th><th>Name</th><th>Description</th><th>Rooms</th>", html);
        Assert.Contains("<td>sample_area</td>", html);
        Assert.Contains("<td>4</td>", html);
        Assert.Contains("class=\"mud-cardlist mud-cards\"", html);
        Assert.Contains("<dt>Prefix</dt><dd>sample_area</dd>", html);
        Assert.Contains("<dt>Rooms</dt><dd>4</dd>", html);
        Assert.Contains("cannot be changed once the area exists", html);   // help text: renaming means moving rooms
        Assert.True(html.IndexOf("desert", StringComparison.Ordinal) < html.IndexOf("sample_area", StringComparison.Ordinal));   // sorted by prefix
        Assert.Contains("Delete", html);
        Assert.DoesNotContain("<h2 id=\"ar-form-h\">", html);   // the form starts closed
    }

    [Fact]
    public async Task AreasPage_WithNoAreas_SaysSo_InBothLayouts()
    {
        await using var page = await Page<AreaList>.OpenAsync(new FakeAdminClient());
        var html = await page.Html();
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "There are no areas\\.").Count);
    }

    [Fact]
    public async Task AreasPage_WhenCoreIsDown_ShowsTheFixedError()
    {
        await using var page = await Page<AreaList>.OpenAsync(new FakeAdminClient { Unavailable = true });
        Assert.Contains(AreaListPresenter.UnavailableError, await page.Html());
    }

    private static TPresenter PresenterOf<TPage, TPresenter>(Page<TPage> page) where TPage : ComponentBase =>
        (TPresenter)typeof(TPage).GetField("_presenter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page.Component)!;

    [Fact]
    public async Task AreasPage_NewAreaForm_HasATypedPrefix_AndTheEditFormAReadOnlyOne()
    {
        var core = new FakeAdminClient().AddArea("desert", "Desert", "Dunes.");
        await using var page = await Page<AreaList>.OpenAsync(core);
        var presenter = PresenterOf<AreaList, AreaListPresenter>(page);

        presenter.BeginNew();
        await page.Refresh();
        var html = await page.Html();
        Assert.Contains("<h2 id=\"ar-form-h\">New area</h2>", html);
        Assert.DoesNotContain("readonly", html.Substring(html.IndexOf("id=\"ar-id\"", StringComparison.Ordinal), 120));
        Assert.Contains("<label for=\"ar-name\">Name</label>", html);
        Assert.Contains("<label for=\"ar-desc\">Short description</label>", html);
        Assert.Contains("maxlength=\"200\"", html);   // the description limit

        presenter.BeginEdit("desert");
        await page.Refresh();
        html = await page.Html();
        Assert.Contains("<h2 id=\"ar-form-h\">Edit area</h2>", html);
        var idInput = html.Substring(html.IndexOf("id=\"ar-id\"", StringComparison.Ordinal), 160);
        Assert.Contains("readonly", idInput);
        Assert.Contains("value=\"desert\"", idInput);
        Assert.Contains("renaming an area means moving its rooms", html);
    }

    [Fact]
    public async Task AreasPage_Save_CreatesTheArea_ReloadsTheList_AndShowsTheNotice()
    {
        var core = new FakeAdminClient();
        await using var page = await Page<AreaList>.OpenAsync(core);
        var presenter = PresenterOf<AreaList, AreaListPresenter>(page);
        presenter.BeginNew();
        presenter.FormId = "sample_area"; presenter.FormName = "Sample area"; presenter.FormDescription = "Tiny.";
        await page.Call("SaveAsync");
        var html = await page.Html();
        Assert.Contains("Area created.", html);
        Assert.Contains("<td>sample_area</td>", html);
        Assert.DoesNotContain("<h2 id=\"ar-form-h\">", html);   // the form closed
    }

    [Fact]
    public async Task AreasPage_Save_Invalid_ShowsCoresMessages_AndKeepsTheForm()
    {
        var core = new FakeAdminClient { AreaResult = AdminResult.Invalid };
        core.AreaErrors.Add(new FieldError { Field = "Name", Message = "Name is required." });
        await using var page = await Page<AreaList>.OpenAsync(core);
        var presenter = PresenterOf<AreaList, AreaListPresenter>(page);
        presenter.BeginNew();
        presenter.FormId = "x";
        await page.Call("SaveAsync");
        var html = await page.Html();
        Assert.Contains("Name: Name is required.", html);
        Assert.Contains("<h2 id=\"ar-form-h\">New area</h2>", html);
    }

    [Fact]
    public async Task AreasPage_Delete_AsksForConfirmationNamingTheArea_ThenSendsOneDelete()
    {
        var core = new FakeAdminClient().AddArea("desert", "Desert").AddArea("keep", "Keep");
        await using var page = await Page<AreaList>.OpenAsync(core);
        await page.Invoke("Ask", core.Areas["desert"]);
        var html = await page.Html();
        Assert.Contains("role=\"alertdialog\"", html);
        Assert.Contains(AreaListPresenter.DeleteConfirm + " (desert)", html);
        Assert.DoesNotContain("DeleteArea", string.Join(",", core.AreaCalls));   // nothing sent before the confirm

        await page.Call("ConfirmDeleteAsync");
        html = await page.Html();
        Assert.Equal(1, core.AreaCalls.Count(c => c.StartsWith("DeleteArea")));
        Assert.Contains("DeleteArea:desert", core.AreaCalls);
        Assert.Contains("Area deleted.", html);
        Assert.DoesNotContain("<td>desert</td>", html);
        Assert.Contains("<td>keep</td>", html);
        Assert.DoesNotContain("role=\"alertdialog\"", html);
    }

    [Fact]
    public async Task AreasPage_Delete_Cancel_SendsNothing()
    {
        var core = new FakeAdminClient().AddArea("desert");
        await using var page = await Page<AreaList>.OpenAsync(core);
        await page.Invoke("Ask", core.Areas["desert"]);
        await page.Call("CancelDelete");
        Assert.DoesNotContain("role=\"alertdialog\"", await page.Html());
        Assert.DoesNotContain(core.AreaCalls, c => c.StartsWith("DeleteArea"));
        await page.Call("ConfirmDeleteAsync");   // nothing is pending: still nothing sent
        Assert.DoesNotContain(core.AreaCalls, c => c.StartsWith("DeleteArea"));
    }

    [Fact]
    public async Task AreasPage_Delete_RefusedWhileRoomsUseIt_ShowsTheCountMessage_AndKeepsTheRow()
    {
        var core = new FakeAdminClient { AreaResult = AdminResult.Refused, AreaMessage = "3 rooms still use this area. Move or delete them first. Nothing was changed." }.AddArea("t", "Test", "", 3);
        await using var page = await Page<AreaList>.OpenAsync(core);
        await page.Invoke("Ask", core.Areas["t"]);
        await page.Call("ConfirmDeleteAsync");
        var html = await page.Html();
        Assert.Contains("3 rooms still use this area.", html);
        Assert.Contains("<td>t</td>", html);
    }

    [Fact]
    public async Task AreasPage_EveryActionButtonIsAButton_WithTheMutatingMarker_ForTheReconnectGuard()
    {
        await using var page = await Page<AreaList>.OpenAsync(new FakeAdminClient().AddArea("a"));
        var html = await page.Html();
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(html, "data-mutating").Count);   // edit + delete, in the table and in the card
    }

    // ---- the Create room page

    [Fact]
    public async Task CreatePage_AreaIsARealSelect_WithALabel_OfTheAreaRecords_AndNoDatalist()
    {
        var core = new FakeAdminClient().AddArea("sample_area", "Sample area").AddArea("desert", "Desert");
        await using var page = await Page<Creator>.OpenAsync(core);
        var html = await page.Html();
        Assert.Contains("<label for=\"cr-area\">Area</label>", html);
        Assert.Contains("<select id=\"cr-area\"", html);
        Assert.Contains("<option value=\"desert\">desert - Desert</option>", html);
        Assert.Contains("<option value=\"sample_area\">sample_area - Sample area</option>", html);
        Assert.Contains("(choose an area)", html);
        Assert.DoesNotContain("<datalist", html);
        Assert.DoesNotContain("<input id=\"cr-area\"", html);
        Assert.True(html.IndexOf("id=\"cr-area\"", StringComparison.Ordinal) < html.IndexOf("id=\"cr-key\"", StringComparison.Ordinal));   // area first, then the local key
    }

    [Fact]
    public async Task CreatePage_PickingAnArea_ShowsItsPrefixAsFixedTextBeforeTheLocalKey()
    {
        var core = new FakeAdminClient().AddArea("sample_area", "Sample area");
        await using var page = await Page<Creator>.OpenAsync(core);
        Assert.Contains("<span class=\"mud-keyprefix\" id=\"cr-prefix\" aria-hidden=\"true\"></span>", await page.Html());   // empty until an area is chosen

        PresenterOf<Creator, CreatorPresenter>(page).Form.Area = "sample_area";   // what the select's change handler does
        await page.Refresh();
        var html = await page.Html();
        Assert.Contains("<span class=\"mud-keyprefix\" id=\"cr-prefix\" aria-hidden=\"true\">sample_area.</span>", html);
        Assert.Contains("<option value=\"sample_area\" selected>sample_area - Sample area</option>", html);
        Assert.DoesNotContain("(choose an area)", html);
        Assert.True(html.IndexOf("id=\"cr-prefix\"", StringComparison.Ordinal) < html.IndexOf("<input id=\"cr-key\"", StringComparison.Ordinal));
        Assert.Contains("aria-describedby=\"cr-prefix\"", html);   // a screen reader reads the prefix with the field
        Assert.DoesNotContain("<input id=\"cr-area\"", html);
    }

    [Fact]
    public async Task CreatePage_WithNoAreas_OffersOnlyThePlaceholder_AndLinksToTheAreasPage()
    {
        await using var page = await Page<Creator>.OpenAsync(new FakeAdminClient());
        var html = await page.Html();
        Assert.Contains("(choose an area)", html);
        Assert.Contains("href=\"" + WebRoutes.Areas + "\"", html);
    }

    // ---- the Move room section

    [Fact]
    public async Task RoomEditPage_MoveArea_IsARealSelect_WithTheCurrentAreaSelected_AndTheFixedPrefix_AndReadOnlyAreaText()
    {
        var core = new FakeAdminClient().AddRoom("t.start").AddRoom("t.hall", "t", 1, 1, 0).AddArea("t", "Test land").AddArea("elsewhere", "Elsewhere");
        await using var page = await Page<RoomEdit>.OpenAsync(core, new Dictionary<string, object?> { ["Key"] = "t.hall" });
        var html = await page.Html();
        Assert.Contains("<label for=\"re-newarea\">Area</label>", html);
        Assert.Contains("<select id=\"re-newarea\"", html);
        Assert.Contains("<option value=\"elsewhere\">elsewhere - Elsewhere</option>", html);
        Assert.Contains("<option value=\"t\" selected>t - Test land</option>", html);
        Assert.DoesNotContain("<datalist", html);
        Assert.DoesNotContain("<input id=\"re-newarea\"", html);
        Assert.Contains("<span class=\"mud-keyprefix\" id=\"re-newprefix\" aria-hidden=\"true\">t.</span>", html);
        Assert.Contains("<input id=\"re-newlocal\" value=\"hall\"", html);
        // The room's own area is plain read-only text (an output element, not an input), with its prefix and name.
        Assert.DoesNotContain("<input id=\"re-area\"", html);
        Assert.Contains("<output id=\"re-area\" class=\"mud-static\">t - Test land</output>", html);
    }

    [Fact]
    public async Task RoomEditPage_MovePrefixFollowsTheChosenArea_AndTheMoveSendsThatAreaWithTheLocalKey()
    {
        var core = new FakeAdminClient().AddRoom("t.start").AddRoom("t.hall", "t", 1, 1, 0).AddArea("t", "Test land").AddArea("elsewhere", "Elsewhere");
        await using var page = await Page<RoomEdit>.OpenAsync(core, new Dictionary<string, object?> { ["Key"] = "t.hall" });
        PresenterOf<RoomEdit, RoomEditPresenter>(page).NewArea = "elsewhere";   // what the select's change handler does
        await page.Refresh();
        var html = await page.Html();
        Assert.Contains("id=\"re-newprefix\" aria-hidden=\"true\">elsewhere.</span>", html);
        Assert.Contains("<option value=\"elsewhere\" selected>elsewhere - Elsewhere</option>", html);
        Assert.DoesNotContain("<option value=\"t\" selected>", html);
        var presenter = PresenterOf<RoomEdit, RoomEditPresenter>(page);
        Assert.Equal(RoomEditOutcome.Moved, await presenter.MoveAsync());
        Assert.Equal(("t.hall", "elsewhere", "hall"), core.RoomMoves.Single());
    }

    [Fact]
    public async Task RoomEditPage_MoveArea_WhenTheRoomsAreaHasNoRecord_ShowsAPlaceholder_AndNoPrefix()
    {
        var core = new FakeAdminClient().AddRoom("t.hall", "t", 1, 1, 0).AddArea("elsewhere");
        await using var page = await Page<RoomEdit>.OpenAsync(core, new Dictionary<string, object?> { ["Key"] = "t.hall" });
        var html = await page.Html();
        Assert.Contains("(choose an area)", html);
        Assert.Contains("id=\"re-newprefix\" aria-hidden=\"true\"></span>", html);
    }

    // ---- the map

    [Fact]
    public async Task MapPage_AreaSelect_ListsEmptyAreasToo_WithTheirNames()
    {
        var core = new FakeAdminClient().AddArea("a", "Alpha").AddArea("empty", "Nothing yet").AddRoom("a.x", "a");
        await using var page = await Page<CreatorMap>.OpenAsync(core);
        var html = await page.Html();
        Assert.Contains("<select id=\"map-area\"", html);
        Assert.Contains(">a - Alpha</option>", html);
        Assert.Contains(">empty - Nothing yet</option>", html);
    }
}
