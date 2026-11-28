using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MUD.Contracts;
using MUD.Web.Components.Pages;
using MUD.Web.Pages;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

// NPC templates (specs/WEB.md "NPC templates page"): the gRPC client calls, the fake client, the presenter and the read-only placeholder page.
public class NpcTemplateGrpcClientTests : IDisposable
{
    private sealed class StandIn : Admin.AdminBase
    {
        public static string? LastArea;
        public static NpcTemplateRecord? LastSaved;

        private static void RequireToken(string token)
        {
            if (token == "stale") { throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unauthenticated, "no")); }
            if (token == "low") { throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.PermissionDenied, "no")); }
            if (token == "busy") { throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "no")); }
        }

        private static NpcTemplateEditReply Answer(string id) => id switch
        {
            "t.taken" => new NpcTemplateEditReply { Result = AdminResult.AlreadyExists, Message = "exists" },
            "t.refused" => new NpcTemplateEditReply { Result = AdminResult.Refused, Message = "no store" },
            "t.gone" => new NpcTemplateEditReply { Result = AdminResult.NotFound, Message = "gone" },
            "t.bad" => new NpcTemplateEditReply { Result = AdminResult.Invalid, Errors = { new FieldError { Field = "Strength", Message = "range" } } },
            _ => new NpcTemplateEditReply { Result = AdminResult.Ok, Template = new NpcTemplateRecord { Id = id, Version = 1 } },
        };

        public override Task<ListNpcTemplatesReply> ListNpcTemplates(ListNpcTemplatesRequest request, Grpc.Core.ServerCallContext context)
        {
            RequireToken(request.Token);
            LastArea = request.Area;
            var reply = new ListNpcTemplatesReply();
            reply.Templates.Add(new NpcTemplateRecord { Id = "t.a", Area = "t", Short = "a guard", Version = 3, Strength = 14 });
            return Task.FromResult(reply);
        }

        public override Task<GetNpcTemplateReply> GetNpcTemplate(NpcTemplateIdRequest request, Grpc.Core.ServerCallContext context)
        {
            RequireToken(request.Token);
            return Task.FromResult(request.Id == "t.a" ? new GetNpcTemplateReply { Found = true, Template = new NpcTemplateRecord { Id = "t.a" } } : new GetNpcTemplateReply());
        }

        public override Task<NpcTemplateEditReply> CreateNpcTemplate(SaveNpcTemplateRequest request, Grpc.Core.ServerCallContext context) { RequireToken(request.Token); LastSaved = request.Template; return Task.FromResult(Answer(request.Template.Id)); }
        public override Task<NpcTemplateEditReply> UpdateNpcTemplate(SaveNpcTemplateRequest request, Grpc.Core.ServerCallContext context) { RequireToken(request.Token); return Task.FromResult(Answer(request.Template.Id)); }
        public override Task<NpcTemplateEditReply> DeleteNpcTemplate(NpcTemplateIdRequest request, Grpc.Core.ServerCallContext context) { RequireToken(request.Token); return Task.FromResult(Answer(request.Id)); }
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wn" + Guid.NewGuid().ToString("N").Substring(0, 8));
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
        var listed = Assert.Single(await client.ListNpcTemplatesAsync("tok"));
        Assert.Equal(("t.a", "t", "a guard", 3), (listed.Id, listed.Area, listed.Short, listed.Version));
        Assert.True(listed.HasStrength); Assert.False(listed.HasWisdom);
        Assert.Equal(string.Empty, StandIn.LastArea);
        await client.ListNpcTemplatesAsync("tok", "t");
        Assert.Equal("t", StandIn.LastArea);
        Assert.Equal("t.a", (await client.GetNpcTemplateAsync("tok", "t.a"))!.Id);
        Assert.Null(await client.GetNpcTemplateAsync("tok", "t.zz"));
    }

    [Theory]
    [InlineData("t.ok", AdminResult.Ok)]
    [InlineData("t.taken", AdminResult.AlreadyExists)]
    [InlineData("t.refused", AdminResult.Refused)]
    [InlineData("t.gone", AdminResult.NotFound)]
    [InlineData("t.bad", AdminResult.Invalid)]
    public async Task WriteCalls_ReturnTheReplyResult(string id, AdminResult expected)
    {
        var client = await Start();
        Assert.Equal(expected, (await client.CreateNpcTemplateAsync("tok", new NpcTemplateRecord { Id = id })).Result);
        Assert.Equal(expected, (await client.UpdateNpcTemplateAsync("tok", new NpcTemplateRecord { Id = id })).Result);
        Assert.Equal(expected, (await client.DeleteNpcTemplateAsync("tok", id)).Result);
        var invalid = await client.CreateNpcTemplateAsync("tok", new NpcTemplateRecord { Id = "t.bad" });
        Assert.Equal("Strength", Assert.Single(invalid.Errors).Field);
    }

    [Fact]
    public async Task AStatThatIsNotSet_StaysNotSetOnTheWire_AndZeroIsAValue()
    {
        var client = await Start();
        await client.CreateNpcTemplateAsync("tok", new NpcTemplateRecord { Id = "t.ok", Charisma = 0 });
        Assert.True(StandIn.LastSaved!.HasCharisma);
        Assert.False(StandIn.LastSaved.HasStrength);
    }

    [Fact]
    public async Task RefusalsAreMapped_AndAnUnreachableCoreIsUnavailable()
    {
        var client = await Start();
        Assert.True((await Assert.ThrowsAsync<CoreRefusedException>(() => client.ListNpcTemplatesAsync("stale"))).SessionEnded);
        Assert.False((await Assert.ThrowsAsync<CoreRefusedException>(() => client.GetNpcTemplateAsync("low", "t.a"))).SessionEnded);
        await Assert.ThrowsAsync<CoreUnavailableException>(() => client.DeleteNpcTemplateAsync("busy", "x"));
        var dead = new GrpcAdminClient(new Admin.AdminClient(CoreSocket.OpenChannel(Path.Combine(_directory, "nothing.sock"))));
        await Assert.ThrowsAsync<CoreUnavailableException>(() => dead.ListNpcTemplatesAsync("tok"));
        await Assert.ThrowsAsync<CoreUnavailableException>(() => dead.CreateNpcTemplateAsync("tok", new NpcTemplateRecord { Id = "x" }));
    }
}

public class FakeNpcTemplateClientTests
{
    [Fact]
    public async Task TheFakeStoresListsGetsAndRemoves_AndRecordsTheToken()
    {
        var core = new FakeAdminClient().AddNpcTemplate("a.one").AddNpcTemplate("b.two");
        Assert.Equal(new[] { "a.one", "b.two" }, (await core.ListNpcTemplatesAsync("tok")).Select(i => i.Id));
        Assert.Equal(new[] { "b.two" }, (await core.ListNpcTemplatesAsync("tok", "b")).Select(i => i.Id));
        Assert.Equal("a.one", (await core.GetNpcTemplateAsync("tok", "a.one"))!.Id);
        Assert.Null(await core.GetNpcTemplateAsync("tok", "zz"));
        Assert.Equal(AdminResult.Ok, (await core.CreateNpcTemplateAsync("tok", new NpcTemplateRecord { Id = "c.new" })).Result);
        var update = await core.UpdateNpcTemplateAsync("tok", new NpcTemplateRecord { Id = "c.new", Short = "x" });
        Assert.Equal(2, update.Template.Version);
        Assert.Equal(AdminResult.Ok, (await core.DeleteNpcTemplateAsync("tok", "c.new")).Result);
        Assert.False(core.NpcTemplates.ContainsKey("c.new"));
        Assert.All(core.NpcTemplateTokens, t => Assert.Equal("tok", t));
    }
}

public class NpcTemplateListPresenterTests
{
    private const string Token = "token-1";

    [Fact]
    public async Task Load_GroupsByArea_InIdOrder_ListsEmptyAreas_AndNamesAnOrphanByItsPrefix()
    {
        var core = new FakeAdminClient().AddArea("b", "Bee land").AddArea("a", "Ay land", "First.").AddArea("empty", "Nothing")
            .AddNpcTemplate("b.wasp", "a wasp").AddNpcTemplate("a.guard", "a guard").AddNpcTemplate("a.cook", "a cook").AddNpcTemplate("lost.ghost", "a ghost");
        var p = new NpcTemplateListPresenter(core, Token);
        Assert.True(await p.LoadAsync());
        Assert.Null(p.Error);
        Assert.Equal(4, p.Total);
        Assert.Equal(new[] { "a", "b", "empty", "lost" }, p.Groups.Select(g => g.AreaId));
        Assert.Equal(new[] { "a.cook", "a.guard" }, p.Groups[0].Items.Select(i => i.Id));
        Assert.Equal(("Ay land", "First."), (p.Groups[0].Name, p.Groups[0].Description));
        Assert.Empty(p.Groups[2].Items);
        Assert.Equal("lost", p.Groups[3].Name);
        Assert.All(core.NpcTemplateTokens, t => Assert.Equal(Token, t));
    }

    [Theory]
    [InlineData(0, NpcTemplateListPresenter.UnavailableError)]
    [InlineData(1, NpcTemplateListPresenter.ForbiddenError)]
    [InlineData(2, NpcTemplateListPresenter.FailedError)]
    public async Task Load_Failures_GiveFixedMessages_AndNoData(int mode, string expected)
    {
        var core = new FakeAdminClient().AddArea("a", "A").AddNpcTemplate("a.x");
        if (mode == 0) { core.Unavailable = true; } else if (mode == 1) { core.RefuseNpcTemplateCalls = true; } else { core.Crash = true; }
        var p = new NpcTemplateListPresenter(core, Token);
        Assert.False(await p.LoadAsync());
        Assert.Equal(expected, p.Error);
        Assert.Empty(p.Groups);
        Assert.Equal(0, p.Total);
    }
}

public class NpcTemplatePageTests
{
    private const string Token = "token-1";

    private sealed class NoJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => default;
    }

    private sealed class TestNav : NavigationManager
    {
        public TestNav() { Initialize("http://localhost/", "http://localhost/creator/npcs"); }
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    private sealed class Host : ComponentBase
    {
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder b)
        {
            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "creator"), new Claim(SessionAuthentication.TokenClaim, Token) }, "test");
            var state = Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
            b.OpenComponent<CascadingValue<Task<AuthenticationState>>>(0);
            b.AddAttribute(1, "Value", state);
            b.AddAttribute(2, "ChildContent", (RenderFragment)(inner => { inner.OpenComponent<NpcTemplateList>(0); inner.CloseComponent(); }));
            b.CloseComponent();
        }
    }

    private static async Task<string> Render(FakeAdminClient core)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAdminClient>(core);
        services.AddSingleton<NavigationManager, TestNav>();
        services.AddSingleton<NoticeService>();
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NoJs());
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<Host>()).ToHtmlString());
    }

    private static FakeAdminClient Seeded()
    {
        var core = new FakeAdminClient().AddArea("a", "Ay land", "The first region.").AddArea("b", "Bee land").AddArea("empty", "Nothing yet")
            .AddNpcTemplate("a.guard", "a stern guard").AddNpcTemplate("b.wasp", "a wasp");
        core.NpcTemplates["a.guard"].Version = 3;
        return core;
    }

    // ---- routes, menu, policy

    [Fact]
    public void TheRoute_IsFixed_AndThereIsNoNpcEditRoute()
    {
        Assert.Equal("/creator/npcs", WebRoutes.NpcTemplates);
        Assert.Equal("/creator/npcs", typeof(NpcTemplateList).GetCustomAttribute<RouteAttribute>()!.Template);
        var routes = typeof(NpcTemplateList).Assembly.GetTypes().Where(t => typeof(ComponentBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>()).Select(r => r.Template);
        Assert.DoesNotContain(routes, r => r.StartsWith("/creator/npcs/", StringComparison.Ordinal));
        Assert.Single(routes, r => r == "/creator/npcs");
    }

    [Fact]
    public void ThePage_RequiresCreatorOrAdmin()
    {
        Assert.Equal(WebPolicies.CreatorOrAdmin, typeof(NpcTemplateList).GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }

    private static async Task<PolicyAuthorizationResult> Evaluate(ClaimsPrincipal? user)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(WebPolicies.Configure);
        var sp = services.BuildServiceProvider();
        var policy = await sp.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(typeof(NpcTemplateList).GetCustomAttribute<AuthorizeAttribute>()!.Policy!);
        var evaluator = new PolicyEvaluator(sp.GetRequiredService<IAuthorizationService>());
        var ctx = new DefaultHttpContext();
        if (user != null) { ctx.User = user; }
        var auth = user == null ? AuthenticateResult.NoResult() : AuthenticateResult.Success(new AuthenticationTicket(user, "test"));
        return await evaluator.AuthorizeAsync(policy!, auth, ctx, null);
    }

    private static ClaimsPrincipal User(AccountRole role) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "Someone"), new Claim(ClaimTypes.Role, role.ToString()) }, "test"));

    [Fact]
    public async Task APlainPlayer_IsForbidden_AnAnonymousVisitorIsSentToLogin_CreatorAndAdminGetIn()
    {
        var player = await Evaluate(User(AccountRole.Player));
        Assert.True(player.Forbidden);          // the access-denied page (403)
        Assert.False(player.Succeeded);
        var anonymous = await Evaluate(null);
        Assert.True(anonymous.Challenged);      // the login page
        Assert.False(anonymous.Succeeded);
        Assert.True((await Evaluate(User(AccountRole.Creator))).Succeeded);
        Assert.True((await Evaluate(User(AccountRole.Admin))).Succeeded);
    }

    [Fact]
    public void TheMenu_HasANpcTemplatesLink_ForCreatorsAndAdminsOnly_InAlphabeticalPosition()
    {
        foreach (var admin in new[] { false, true })
        {
            var creator = NavMenu.SectionsFor(creatorOrAdmin: true, admin: admin).Single(s => s.Id == "creator");
            var labels = creator.Items.Select(i => i.Label).ToList();
            Assert.Equal(WebRoutes.NpcTemplates, creator.Items.Single(i => i.Label == "NPC templates").Href);
            Assert.Equal(labels.OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList(), labels);
            Assert.True(labels.IndexOf("NPC templates") > labels.IndexOf("Modules") && labels.IndexOf("NPC templates") < labels.IndexOf("Terrain"));
        }
        Assert.DoesNotContain(NavMenu.SectionsFor(false, false), s => s.Items.Any(i => i.Label == "NPC templates"));
    }

    // ---- the stub page

    [Fact]
    public async Task Page_ShowsTheHeading_TheNotice_TheCount_AndOneCollapsedDetailsPerArea()
    {
        var html = await Render(Seeded());
        Assert.Contains("<h1>NPC templates</h1>", html);
        Assert.Contains("NPC templates are not available yet. This page is a placeholder.", html);
        Assert.Contains("2 NPC templates", html);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, "<details").Count);
        Assert.Contains("data-area=\"a\"", html);
        Assert.Contains("<strong>Ay land</strong>", html);
        Assert.Contains("The first region.", html);
        Assert.Contains("<span class=\"mud-itemgroup-count\">1</span>", html);
        Assert.Contains("<span class=\"mud-itemgroup-count\">0</span>", html);
        Assert.Contains("a stern guard", html);
        Assert.Contains("a.guard", html);
        Assert.Contains("<dt>Version</dt><dd>3</dd>", html);
        Assert.Contains("No NPC templates in this area.", html);
        Assert.DoesNotContain(" open", html.Substring(html.IndexOf("<details", StringComparison.Ordinal), 60));   // collapsed
    }

    [Fact]
    public async Task Page_HasNoCreateEditOrDeleteControls()
    {
        var html = await Render(Seeded());
        Assert.DoesNotContain("<button", html);
        Assert.DoesNotContain("<form", html);
        Assert.DoesNotContain("<input", html);
        Assert.DoesNotContain("/edit", html);
        Assert.DoesNotContain("Delete", html);
        Assert.DoesNotContain("Edit", html);
        Assert.DoesNotContain("New ", html);
    }

    [Fact]
    public async Task Page_WithNoTemplates_ShowsTheEmptyState_AndZero()
    {
        var html = await Render(new FakeAdminClient().AddArea("a", "Ay land"));
        Assert.Contains("0 NPC templates", html);
        Assert.Contains(NpcTemplateListPresenter.Empty, html);
        Assert.Contains("NPC templates are not available yet.", html);
        var none = await Render(new FakeAdminClient());
        Assert.Contains(NpcTemplateListPresenter.Empty, none);
        Assert.DoesNotContain("<details", none);
    }

    [Fact]
    public async Task Page_AnOneTemplateCount_IsSingular()
    {
        Assert.Contains("1 NPC template<", await Render(new FakeAdminClient().AddArea("a", "A").AddNpcTemplate("a.x")));
    }

    [Fact]
    public async Task Page_WhenCoreIsUnavailable_ShowsTheErrorAndTheNotice_NoCount()
    {
        var html = await Render(new FakeAdminClient { Unavailable = true });
        Assert.Contains(NpcTemplateListPresenter.UnavailableError, html);
        Assert.Contains("NPC templates are not available yet.", html);
        Assert.DoesNotContain("npc-count", html);
        Assert.DoesNotContain("<details", html);
        var refused = await Render(new FakeAdminClient { RefuseNpcTemplateCalls = true });
        Assert.Contains(NpcTemplateListPresenter.ForbiddenError, refused);
    }

    [Fact]
    public async Task Page_ListsATemplateWhoseAreaHasNoRecord_UnderItsPrefix()
    {
        var html = await Render(new FakeAdminClient().AddNpcTemplate("lost.ghost", "a ghost"));
        Assert.Contains("data-area=\"lost\"", html);
        Assert.Contains("a ghost", html);
    }
}

public class NpcTemplateAreaHubTests
{
    [Fact]
    public async Task TheAreasHub_CountsNpcTemplatesPerArea_AndAFailingListShowsNull()
    {
        var core = new FakeAdminClient().AddArea("a", "A").AddArea("b", "B").AddNpcTemplate("a.x").AddNpcTemplate("a.y");
        var p = new AreaListPresenter(core, "tok");
        await p.LoadAsync();
        Assert.Equal(2, p.NpcTemplatesIn("a"));
        Assert.Equal(0, p.NpcTemplatesIn("b"));
        Assert.Equal(WebRoutes.NpcTemplates, AreaListPresenter.NpcTemplatesUrl("a"));

        var failing = new FakeAdminClient().AddArea("a", "A");
        failing.RefuseNpcTemplateCalls = true;
        var q = new AreaListPresenter(failing, "tok");
        await q.LoadAsync();
        Assert.Null(q.NpcTemplatesIn("a"));
        Assert.Null(q.Error);   // the page still loads without the count
    }
}
