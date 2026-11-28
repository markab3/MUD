using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MUD.Contracts;
using MUD.Web.Components.Pages;
using MUD.Web.Pages;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

// GET /creator/map/export and the Export YAML button (specs/WEB.md, WEB_BLAZOR.md "Room map").
public class MapExportEndpointTests
{
    private static IAuthorizationService Authorization()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(WebPolicies.Configure);
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static DefaultHttpContext Http(AccountRole? role, string token = "tok-1")
    {
        var http = new DefaultHttpContext();
        if (role.HasValue)
        {
            http.User = SessionAuthentication.BuildPrincipal(new SessionInfoReply { PlayerName = "Ann", Role = role.Value == AccountRole.Admin ? AdminRole.Admin : role.Value == AccountRole.Creator ? AdminRole.Creator : AdminRole.Player }, token, "1.1.1.1");
            http.Request.Headers.Cookie = SessionAuthentication.CookieName + "=" + token;
        }
        return http;
    }

    private static Task<IResult> Call(DefaultHttpContext http, FakeAdminClient core, string? area) =>
        MapExportEndpoint.HandleAsync(http, core, Authorization(), area);

    private static string RedirectOf(IResult result) => Assert.IsType<RedirectHttpResult>(result).Url;

    [Theory]
    [InlineData(AccountRole.Creator)]
    [InlineData(AccountRole.Admin)]
    public async Task CreatorAndAdmin_GetTheFile_WithDownloadHeaders_AndNoStore(AccountRole role)
    {
        var core = new FakeAdminClient { ExportResult = new ExportAreaReply { Found = true, Yaml = "area:\n  prefix: a1\n", RoomCount = 2 } };
        var http = Http(role);
        var result = await Call(http, core, "a1");

        var file = Assert.IsType<FileContentHttpResult>(result);
        Assert.Equal("text/yaml; charset=utf-8", file.ContentType);
        Assert.Equal("a1.yaml", file.FileDownloadName);
        Assert.Equal("area:\n  prefix: a1\n", System.Text.Encoding.UTF8.GetString(file.FileContents.ToArray()));
        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
        Assert.Equal(("tok-1", "a1"), Assert.Single(core.ExportCalls));
    }

    [Fact]
    public async Task TheFileResponse_CarriesAnAttachmentContentDisposition()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var http = Http(AccountRole.Creator);
        http.RequestServices = services;
        http.Response.Body = new MemoryStream();
        var result = await Call(http, new FakeAdminClient(), "a1");
        await result.ExecuteAsync(http);
        var disposition = http.Response.Headers.ContentDisposition.ToString();
        Assert.StartsWith("attachment;", disposition);
        Assert.Contains("a1.yaml", disposition);
        Assert.Equal("text/yaml; charset=utf-8", http.Response.ContentType);
        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Anonymous_IsChallenged_AndCoreIsNeverCalled()
    {
        var core = new FakeAdminClient();
        var http = Http(null);
        var result = await Call(http, core, "a1");
        Assert.IsType<ChallengeHttpResult>(result);
        Assert.Empty(core.ExportCalls);
        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task APlainPlayer_Gets403_AndCoreIsNeverCalled()
    {
        var core = new FakeAdminClient();
        var http = Http(AccountRole.Player);
        var result = await Call(http, core, "a1");
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<StatusCodeHttpResult>(result).StatusCode);
        Assert.Empty(core.ExportCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A1")]
    [InlineData("a b")]
    [InlineData("a/../b")]
    [InlineData("a\"; filename=\"x")]
    [InlineData("a\r\nSet-Cookie: x=1")]
    [InlineData("a.")]
    [InlineData(".a")]
    [InlineData("a%0d%0ab")]
    public async Task AnAreaThatIsNotAPrefix_IsRefused_NoCoreCall_NoFile(string? area)
    {
        var core = new FakeAdminClient();
        var result = await Call(Http(AccountRole.Creator), core, area);
        var url = RedirectOf(result);
        Assert.StartsWith(WebRoutes.Map + "?area=", url);
        Assert.Contains("export=" + MapExportEndpoint.KindInvalid, url);
        Assert.DoesNotContain("\r", url);
        Assert.DoesNotContain("\n", url);
        Assert.Empty(core.ExportCalls);
    }

    [Theory]
    [InlineData("a1", true)]
    [InlineData("a_b.c2", true)]
    [InlineData("a-b", false)]
    [InlineData("A", false)]
    [InlineData("a..b", false)]
    [InlineData("", false)]
    public void IsValidPrefix_IsTheAreaPrefixGrammar(string area, bool valid) => Assert.Equal(valid, MapExportEndpoint.IsValidPrefix(area));

    [Fact]
    public async Task ExporterErrors_RedirectToTheMap_WithTheFirstFiveAndTheCount_AndNoDownload()
    {
        var reply = new ExportAreaReply { Found = true };
        for (int i = 1; i <= 8; i++) { reply.Errors.Add("Room 'a1.r" + i + "': LongDescription is required."); }
        var http = Http(AccountRole.Creator);
        var result = await Call(http, new FakeAdminClient { ExportResult = reply }, "a1");

        var url = RedirectOf(result);
        Assert.StartsWith(WebRoutes.Map + "?area=a1&export=failed&errors=8", url);
        Assert.Equal(5, url.Split("&e=").Length - 1);
        Assert.Contains("a1.r5", Uri.UnescapeDataString(url));
        Assert.DoesNotContain("a1.r6", Uri.UnescapeDataString(url));
        Assert.False(http.Response.Headers.ContainsKey("Content-Disposition"));
        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task ErrorLines_AreCutAndEscaped_SoTheyCannotInjectIntoTheRedirect()
    {
        var reply = new ExportAreaReply { Found = true };
        reply.Errors.Add("bad\r\n&export=x&e=" + new string('z', 1000));
        var url = RedirectOf(await Call(Http(AccountRole.Creator), new FakeAdminClient { ExportResult = reply }, "a1"));
        Assert.DoesNotContain("\r", url);
        Assert.DoesNotContain("\n", url);
        Assert.Equal(1, url.Split("&e=").Length - 1);
        Assert.Equal(1, url.Split("&export=").Length - 1);
        Assert.True(url.Length < 1200);
    }

    [Fact]
    public async Task AFoundReplyWithNoYaml_IsNotADownload()
    {
        var url = RedirectOf(await Call(Http(AccountRole.Creator), new FakeAdminClient { ExportResult = new ExportAreaReply { Found = true } }, "a1"));
        Assert.Contains("export=failed", url);
    }

    [Fact]
    public async Task NotFound_Unavailable_AndRefused_RedirectToTheMap_WithAKind()
    {
        Assert.Contains("export=notfound", RedirectOf(await Call(Http(AccountRole.Creator), new FakeAdminClient { ExportResult = new ExportAreaReply() }, "a1")));
        Assert.Contains("export=unavailable", RedirectOf(await Call(Http(AccountRole.Creator), new FakeAdminClient { Unavailable = true }, "a1")));
        Assert.Contains("export=forbidden", RedirectOf(await Call(Http(AccountRole.Creator), new FakeAdminClient { RefuseModuleCalls = true }, "a1")));
    }

    [Fact]
    public void TheRoute_IsMapped_AndFixed()
    {
        Assert.Equal("/creator/map/export", WebRoutes.MapExport);
        Assert.Equal("/creator/map/export?area=a1", WebRoutes.MapExportFor("a1"));
    }

    [Fact]
    public void NoticeText_IsFixed_AndUnknownKindsShowNothing()
    {
        Assert.Null(MapExportEndpoint.NoticeFor(null));
        Assert.Null(MapExportEndpoint.NoticeFor("<script>"));
        foreach (var kind in new[] { "failed", "unavailable", "notfound", "invalid", "forbidden" }) { Assert.NotNull(MapExportEndpoint.NoticeFor(kind)); }
    }
}

public class MapExportButtonTests
{
    private sealed class Host<T> : ComponentBase where T : ComponentBase
    {
        [Parameter] public Dictionary<string, object?> Parameters { get; set; } = new();
        [Parameter] public Action<T>? Capture { get; set; }
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder b)
        {
            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "creator"), new Claim(SessionAuthentication.TokenClaim, "tok") }, "test");
            var state = Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
            b.OpenComponent<CascadingValue<Task<AuthenticationState>>>(0);
            b.AddAttribute(1, "Value", state);
            b.AddAttribute(2, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<T>(0);
                var seq = 1;
                foreach (var p in Parameters) { inner.AddAttribute(seq++, p.Key, p.Value); }
                inner.AddComponentReferenceCapture(seq, o => Capture?.Invoke((T)o));
                inner.CloseComponent();
            }));
            b.CloseComponent();
        }
    }

    private sealed class Nav : NavigationManager { public Nav() { Initialize("http://localhost/", "http://localhost/creator/map"); } protected override void NavigateToCore(string uri, NavigationOptions options) { } }
    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => default;
    }

    private static async Task<string> Render(FakeAdminClient core, Dictionary<string, object?>? query = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAdminClient>(core);
        services.AddSingleton<IRoomCreationService>(new RoomCreationService(core));
        services.AddSingleton<NavigationManager, Nav>();
        services.AddSingleton<NoticeService>();
        services.AddSingleton<IJSRuntime>(new NoJs());
        services.AddSingleton<DraftStore>();
        services.AddScoped<RoomDirectory>();
        services.AddSingleton(new AssetVersions(new Microsoft.Extensions.FileProviders.NullFileProvider()));
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
        CreatorMap? page = null;
        var root = await renderer.Dispatcher.InvokeAsync(async () =>
            await renderer.RenderComponentAsync<Host<CreatorMap>>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Parameters"] = new Dictionary<string, object?>(),
                ["Capture"] = (Action<CreatorMap>)(c => page = c),
            })));
        if (query != null)
        {
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                foreach (var q in query) { typeof(CreatorMap).GetProperty(q.Key)!.SetValue(page, q.Value); }
                var m = typeof(CreatorMap).GetMethod("OnParametersSetAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                await (Task)m.Invoke(page, null)!;
                typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);
            });
        }
        return await renderer.Dispatcher.InvokeAsync(() => root.ToHtmlString());
    }

    [Fact]
    public async Task TheMapPage_ShowsAnExportLink_ToTheEndpoint_WithTheRoomCountInTheTitle_AndTheHelpLine()
    {
        var core = new FakeAdminClient().AddArea("a1", "Alpha", rooms: 7).AddRoom("a1.x", "a1");
        var html = await Render(core);
        Assert.Contains("id=\"map-export\"", html);
        Assert.Contains("href=\"/creator/map/export?area=a1\"", html);
        Assert.Contains(">Export YAML</a>", html);
        Assert.Contains("title=\"Export 7 room(s) of a1 as a1.yaml\"", html);
        Assert.Contains("data-enhance-nav=\"false\"", html);
        Assert.Contains("Exports every room of this area as an area file (rooms, exits, features, searches). Items on the floor and creatures are not included.", html);
        // Beside the dropdowns, in the same toolbar, and it works without script: a plain link, no onclick.
        var toolbar = html.IndexOf("mud-map-toolbar", StringComparison.Ordinal);
        Assert.True(toolbar >= 0 && html.IndexOf("id=\"map-export\"", StringComparison.Ordinal) > html.IndexOf("id=\"map-z\"", StringComparison.Ordinal));
        Assert.DoesNotContain("map-export\" onclick", html);
    }

    [Fact]
    public async Task TheButton_FollowsTheSelectedArea()
    {
        var core = new FakeAdminClient().AddArea("a1", "Alpha").AddArea("b2", "Beta").AddRoom("a1.x", "a1").AddRoom("b2.x", "b2");
        var html = await Render(core, new Dictionary<string, object?> { ["Area"] = "b2" });
        Assert.Contains("href=\"/creator/map/export?area=b2\"", html);
        Assert.DoesNotContain("href=\"/creator/map/export?area=a1\"", html);
    }

    [Fact]
    public async Task TheNotice_ShowsFixedText_TheCount_AndAtMostFiveLines_AndEscapesTheRest()
    {
        var core = new FakeAdminClient().AddArea("a1", "Alpha").AddRoom("a1.x", "a1");
        var lines = Enumerable.Range(1, 8).Select(i => i == 1 ? "<b>bad</b> room" : "line " + i).ToArray();
        var html = await Render(core, new Dictionary<string, object?> { ["Export"] = "failed", ["Errors"] = 8, ["E"] = lines });
        Assert.Contains("id=\"map-export-notice\"", html);
        Assert.Contains("The area could not be exported. Nothing was downloaded.", html);
        Assert.Contains("8 problems (the first 5 are shown)", html);
        Assert.Contains("&lt;b&gt;bad&lt;/b&gt; room", html);
        Assert.DoesNotContain("<b>bad</b>", html);
        Assert.Contains("line 5", html);
        Assert.DoesNotContain("line 6", html);
    }

    [Fact]
    public async Task AnUnknownNoticeKind_ShowsNothing()
    {
        var core = new FakeAdminClient().AddArea("a1", "Alpha").AddRoom("a1.x", "a1");
        var html = await Render(core, new Dictionary<string, object?> { ["Export"] = "<script>alert(1)</script>" });
        Assert.DoesNotContain("map-export-notice", html);
        Assert.DoesNotContain("<script>alert", html);
    }

    [Fact]
    public void TheButtonStyle_UsesPaletteVariablesOnly_AndA44pxTarget()
    {
        var css = File.ReadAllText(Path.Combine(RepositoryRoot(), "MUD.Web", "wwwroot", "css", "mobile.css"));
        var lines = css.Split('\n').Where(l => l.Contains(".mud-map-export")).ToList();
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("min-height: var(--mud-hit)"));
        foreach (var line in lines) { Assert.DoesNotMatch("#[0-9A-Fa-f]{3,6}\\b", line); }
    }

    private static string RepositoryRoot()
    {
        for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d != null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "MUD.sln"))) { return d.FullName; }
        }
        throw new FileNotFoundException("MUD.sln");
    }
}
