using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MUD.Contracts;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

// A stale or missing mobile.css must not break the map (black discs, invisible glyphs), and a deploy must reach browsers without a hard refresh.
public class StaticAssetTests
{
    private static string TempSite(string css)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mud-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "css"));
        File.WriteAllText(Path.Combine(dir, "css", "mobile.css"), css);
        return dir;
    }

    [Fact]
    public void Url_ChangesWhenTheFileContentChanges_AndIsStableOtherwise()
    {
        var a = TempSite("a { color: red }");
        var b = TempSite("a { color: blue }");
        var a2 = TempSite("a { color: red }");
        try
        {
            string U(string d) => new AssetVersions(new PhysicalFileProvider(d)).Url("css/mobile.css");
            Assert.Matches(@"^css/mobile\.css\?v=[0-9a-f]{12}$", U(a));
            Assert.Equal(U(a), U(a2));
            Assert.NotEqual(U(a), U(b));
        }
        finally { foreach (var d in new[] { a, b, a2 }) { Directory.Delete(d, true); } }
    }

    [Fact]
    public void Url_ForAMissingFile_IsTheBareUrl_AndIsComputedOnce()
    {
        var d = TempSite("x");
        try
        {
            var v = new AssetVersions(new PhysicalFileProvider(d));
            Assert.Equal("js/none.js", v.Url("js/none.js"));
            var first = v.Url("css/mobile.css");
            File.WriteAllText(Path.Combine(d, "css", "mobile.css"), "changed");
            Assert.Equal(first, v.Url("css/mobile.css"));
            Assert.Equal("/" + first, v.RootedUrl("css/mobile.css"));
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void TheRealSiteFiles_AllGetAVersion()
    {
        var root = Path.Combine(FindRepoRoot(), "MUD.Web", "wwwroot");
        var v = new AssetVersions(new PhysicalFileProvider(root));
        foreach (var p in new[] { "css/mobile.css", "js/app.js", "js/roommap.js", "img/planar-realms-logo.png" })
        {
            Assert.Contains("?v=", v.Url(p));
        }
    }

    [Fact]
    public async Task StaticFiles_SendNoCache_ButFrameworkFilesAreLeftAlone()
    {
        var root = Path.Combine(FindRepoRoot(), "MUD.Web", "wwwroot");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = root });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = AssetVersions.PrepareResponse });
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            var r = await client.GetAsync("/css/mobile.css");
            Assert.True(r.IsSuccessStatusCode);
            Assert.True(r.Headers.CacheControl!.NoCache);
            Assert.NotNull(r.Headers.ETag);
            // Revalidation with the ETag is a cheap 304.
            var req = new HttpRequestMessage(HttpMethod.Get, "/css/mobile.css");
            req.Headers.IfNoneMatch.Add(r.Headers.ETag!);
            Assert.Equal(System.Net.HttpStatusCode.NotModified, (await client.SendAsync(req)).StatusCode);
            // The versioned URL is the same file.
            Assert.True((await client.GetAsync("/css/mobile.css?v=abc")).IsSuccessStatusCode);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task Map_RendersReadableWithoutAnyStylesheet()
    {
        var map = new RoomMapLayout().Build(new[] { new RoomRecord("a.o", "a", "origin", 0, 0, 0, new List<RoomExitRecord> { new("north", "a.n") }), new RoomRecord("a.n", "a", "north", 0, 1, 0, new List<RoomExitRecord>()) }, "a", 0);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new AssetVersions(new NullFileProvider()));
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NoJs());
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new Microsoft.AspNetCore.Components.Web.HtmlRenderer(provider, NullLoggerFactory.Instance);
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<MUD.Web.Components.Shared.RoomMap>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Map"] = map,
                ["FillFor"] = (Func<MUD.Web.Services.MapRect, string>)(_ => "#eee"),
            }));
            return root.ToHtmlString();
        });

        var circles = Regex.Matches(html, "<circle[^>]*class=\"mud-arrow-hit\"[^>]*>").Select(m => m.Value).ToList();
        Assert.NotEmpty(circles);
        Assert.All(circles, c => { Assert.Contains("fill=\"transparent\"", c); Assert.Contains("stroke=\"none\"", c); });

        var glyphs = Regex.Matches(html, "<text[^>]*font-weight=\"700\"[^>]*>").Select(m => m.Value).ToList();
        Assert.Equal(map.Arrows.Count, glyphs.Count);
        Assert.All(glyphs, g => { Assert.Contains("fill=\"currentColor\"", g); });

        // The group carries the colour inline: black arrows (an exit exists) and gray ones differ.
        Assert.Contains("class=\"mud-arrow mud-arrow-black\" style=\"color:#1B2B2E\"", html);
        Assert.Contains("class=\"mud-arrow mud-arrow-gray\" style=\"color:#7A8C90\"", html);
        // No arrow element is left to the stylesheet's mercy for its colour.
        Assert.Equal(map.Arrows.Count, Regex.Matches(html, "style=\"color:#[0-9A-F]{6}\"").Count);
    }

    private sealed class NoJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new Microsoft.JSInterop.JSDisconnectedException("test");
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new Microsoft.JSInterop.JSDisconnectedException("test");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MUD.Web", "wwwroot", "css", "mobile.css"))) { dir = dir.Parent; }
        return dir!.FullName;
    }
}
