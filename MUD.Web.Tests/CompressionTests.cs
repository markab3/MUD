using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

// Response compression covers static css/js only. HTML and JSON (antiforgery token, negotiate token) must never be compressed (BREACH).
// There is no TestHost package, so these run a real Kestrel on a loopback port with the same service registration as Program.cs.
public class CompressionTests
{
    private static readonly string Big = string.Concat(Enumerable.Repeat("body { color: #123456; margin: 0 }\n", 400));

    private static async Task<(WebApplication App, HttpClient Client)> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddStaticAssetCompression();
        var app = builder.Build();
        app.UseResponseCompression();
        app.MapGet("/a.css", () => Results.Text(Big, "text/css"));
        app.MapGet("/a.js", () => Results.Text(Big, "text/javascript"));
        app.MapGet("/page", () => Results.Text("<input name=\"__RequestVerificationToken\" value=\"" + Big + "\">", "text/html"));
        app.MapGet("/data", () => Results.Text("{\"connectionToken\":\"" + Big + "\"}", "application/json"));
        await app.StartAsync();
        var addr = app.Urls.First();
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None };
        var client = new HttpClient(handler) { BaseAddress = new Uri(addr) };
        return (app, client);
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient c, string path, string encoding)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue(encoding));
        return await c.SendAsync(req);
    }

    [Theory]
    [InlineData("/a.css", "br")]
    [InlineData("/a.js", "br")]
    [InlineData("/a.css", "gzip")]
    [InlineData("/a.js", "gzip")]
    public async Task StaticAssetsAreCompressed(string path, string encoding)
    {
        var (app, client) = await StartAsync();
        try
        {
            var r = await GetAsync(client, path, encoding);
            Assert.Equal(encoding, string.Join(",", r.Content.Headers.ContentEncoding));
            Assert.True((await r.Content.ReadAsByteArrayAsync()).Length < Big.Length / 4);
        }
        finally { client.Dispose(); await app.StopAsync(); }
    }

    [Theory]
    [InlineData("/page")]
    [InlineData("/data")]
    public async Task HtmlAndJsonAreNeverCompressed(string path)
    {
        var (app, client) = await StartAsync();
        try
        {
            var r = await GetAsync(client, path, "br");
            Assert.Empty(r.Content.Headers.ContentEncoding);
            Assert.Contains(Big.Substring(0, 50), await r.Content.ReadAsStringAsync());
        }
        finally { client.Dispose(); await app.StopAsync(); }
    }

    [Fact]
    public async Task NoAcceptEncodingMeansNoCompression()
    {
        var (app, client) = await StartAsync();
        try
        {
            var r = await client.GetAsync("/a.css");
            Assert.Empty(r.Content.Headers.ContentEncoding);
        }
        finally { client.Dispose(); await app.StopAsync(); }
    }

    [Fact]
    public void ConfigurationExcludesHtmlJsonAndHttps()
    {
        var services = new ServiceCollection().AddLogging().AddStaticAssetCompression().BuildServiceProvider();
        var o = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ResponseCompressionOptions>>().Value;
        Assert.False(o.EnableForHttps);
        Assert.DoesNotContain("text/html", o.MimeTypes);
        Assert.DoesNotContain("application/json", o.MimeTypes);
        Assert.DoesNotContain(o.MimeTypes, m => m.Contains('*'));
        Assert.Contains("text/css", o.MimeTypes);
        Assert.Contains("text/javascript", o.MimeTypes);
    }

    private static string Program()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MUD.sln"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "MUD.Web", "Program.cs"));
    }

    [Fact]
    public void ProgramRegistersCompressionBeforeStaticFiles()
    {
        var src = Program();
        Assert.Contains("AddStaticAssetCompression()", src);
        var comp = src.IndexOf("app.UseResponseCompression()", StringComparison.Ordinal);
        var files = src.IndexOf("app.UseStaticFiles(", StringComparison.Ordinal);
        Assert.True(comp >= 0 && files > comp);
    }
}
