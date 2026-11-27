using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

// Behind `tailscale serve` the site sees plain http and the wrong authority. These run a real Kestrel on a loopback port with the
// same ProxyHeaders setup as Program.cs; a middleware ahead of UseForwardedHeaders stands in for a non-loopback peer.
public class ProxyHeadersTests
{
    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(IPAddress? pretendPeer = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.Configure<ForwardedHeadersOptions>(ProxyHeaders.Configure);
        var app = builder.Build();
        if (pretendPeer != null)
        {
            app.Use((ctx, next) => { ctx.Connection.RemoteIpAddress = pretendPeer; return next(); });
        }
        app.UseForwardedHeaders();
        ProxyHeaders.UsePathOnlyRedirects(app);
        app.MapGet("/seen", (HttpContext c) => $"{c.Request.IsHttps}|{c.Request.Scheme}|{c.Request.Host}|{c.Connection.RemoteIpAddress}|" +
            SessionAuthentication.CookieOptionsFor(c.Request).Secure);
        app.MapGet("/go", (HttpContext c) => Results.Redirect($"{c.Request.Scheme}://{c.Request.Host}/character"));
        app.MapGet("/elsewhere", () => Results.Redirect("https://example.org/x?y=1"));
        app.MapGet("/rel", () => Results.Redirect("/login?ReturnUrl=%2Fcharacter"));
        await app.StartAsync();
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.First()) };
        return (app, client);
    }

    private static HttpRequestMessage Forwarded(string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("X-Forwarded-Proto", "https");
        req.Headers.Add("X-Forwarded-Host", "persephone.tail901d99.ts.net");
        req.Headers.Add("X-Forwarded-For", "100.64.0.9");
        return req;
    }

    [Fact]
    public async Task Loopback_proxy_headers_are_applied_so_the_request_is_https_and_the_cookie_secure()
    {
        var (app, client) = await StartAsync();
        try
        {
            var seen = await (await client.SendAsync(Forwarded("/seen"))).Content.ReadAsStringAsync();
            Assert.Equal("True|https|persephone.tail901d99.ts.net|100.64.0.9|True", seen);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task Plain_local_request_without_forwarded_headers_stays_http_and_the_cookie_not_secure()
    {
        var (app, client) = await StartAsync();
        try
        {
            var seen = await client.GetStringAsync("/seen");
            Assert.StartsWith("False|http|127.0.0.1:", seen);
            Assert.EndsWith("|False", seen);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task Forwarded_headers_from_a_non_loopback_peer_are_ignored()
    {
        var (app, client) = await StartAsync(IPAddress.Parse("203.0.113.5"));
        try
        {
            var seen = await (await client.SendAsync(Forwarded("/seen"))).Content.ReadAsStringAsync();
            Assert.StartsWith("False|http|127.0.0.1:", seen);
            Assert.Contains("|203.0.113.5|", seen);
            Assert.EndsWith("|False", seen);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public void Only_the_loopback_addresses_are_trusted_proxies()
    {
        var o = new ForwardedHeadersOptions();
        ProxyHeaders.Configure(o);
        Assert.Empty(o.KnownNetworks);
        Assert.Equal(new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }, o.KnownProxies);
        Assert.Equal(1, o.ForwardLimit);
    }

    [Fact]
    public async Task A_redirect_to_this_hosts_absolute_url_leaves_as_path_only()
    {
        var (app, client) = await StartAsync();
        try
        {
            var resp = await client.SendAsync(Forwarded("/go"));
            Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
            Assert.Equal("/character", resp.Headers.Location!.OriginalString);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task The_path_only_rewrite_applies_even_without_forwarded_headers_and_leaves_other_locations_alone()
    {
        var (app, client) = await StartAsync();
        try
        {
            Assert.Equal("/character", (await client.GetAsync("/go")).Headers.Location!.OriginalString);
            Assert.Equal("/login?ReturnUrl=%2Fcharacter", (await client.GetAsync("/rel")).Headers.Location!.OriginalString);
            Assert.Equal("https://example.org/x?y=1", (await client.GetAsync("/elsewhere")).Headers.Location!.OriginalString);
        }
        finally { await app.StopAsync(); }
    }

    [Theory]
    [InlineData("http://persephone.tail901d99.ts.net/character", "/character")]
    [InlineData("https://persephone.tail901d99.ts.net/a?b=1", "/a?b=1")]
    [InlineData("/character", null)]
    [InlineData("http://other.example/character", null)]
    [InlineData("", null)]
    public void PathOnly_maps_own_authority_only(string location, string? expected) =>
        Assert.Equal(expected, ProxyHeaders.PathOnly(location, new HostString("persephone.tail901d99.ts.net")));
}
