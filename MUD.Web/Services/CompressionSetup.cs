using Microsoft.AspNetCore.ResponseCompression;

namespace MUD.Web.Services;

// Response compression for static assets only (blazor.web.js, Fluent UI and site css/js).
//
// Why HTML and JSON are NOT compressed: compressing a response that mixes a secret with attacker-influenced text enables BREACH-style
// attacks. The dynamic HTML pages carry the antiforgery request token (logout form in App.razor) and /_blazor/negotiate returns a
// connection token. The MIME list below is explicit (not ResponseCompressionDefaults.MimeTypes, which includes text/html and
// application/json), so those responses are never compressed. Static css/js contain no secrets and no reflected input.
// EnableForHttps stays false (the framework default): the site binds to plain http on 127.0.0.1, and if a TLS reverse proxy is put in
// front the proxy sees plain http from us and decides for itself. WebSocket frames (the circuit) are not touched by this middleware.
public static class CompressionSetup
{
    public static readonly string[] StaticMimeTypes =
    {
        "text/css",
        "text/javascript",
        "application/javascript",
        "application/x-javascript",
        "image/svg+xml",
    };

    public static IServiceCollection AddStaticAssetCompression(this IServiceCollection services)
    {
        services.AddResponseCompression(o =>
        {
            o.EnableForHttps = false;
            o.MimeTypes = StaticMimeTypes;
            o.Providers.Add<BrotliCompressionProvider>();
            o.Providers.Add<GzipCompressionProvider>();
        });
        // Fastest keeps first-hit CPU low for on-the-fly compression (no precompressed assets in .NET 8 static files).
        services.Configure<BrotliCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
        services.Configure<GzipCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
        return services;
    }
}
