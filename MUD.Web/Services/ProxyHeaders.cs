using Microsoft.AspNetCore.HttpOverrides;

namespace MUD.Web.Services;

// The site sits behind `tailscale serve` (TLS on the tailnet, plain http to 127.0.0.1:5080). The proxy runs on this host, so only
// a loopback peer may set X-Forwarded-For/Proto/Host; from anyone else those headers are ignored (they cannot be forged).
// With them applied Request.IsHttps is true behind the proxy, so the session cookie is Secure; on plain http://127.0.0.1:5080
// there are no forwarded headers and the cookie stays non-Secure so local use still works.
public static class ProxyHeaders
{
    public static void Configure(ForwardedHeadersOptions o)
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        o.ForwardLimit = 1;
        o.KnownNetworks.Clear();
        o.KnownProxies.Clear();
        o.KnownProxies.Add(System.Net.IPAddress.Loopback);
        o.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
    }

    // A redirect must never carry an absolute URL built from the scheme and host this server believes it has: behind the proxy
    // that was http://persephone.../ on a port the proxy does not serve ("Unable to connect"). Any 3xx whose Location points at
    // this request's own authority is cut down to path and query, which the browser resolves against what it really used.
    public static void UsePathOnlyRedirects(IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var response = context.Response;
                if (response.StatusCode is >= 300 and < 400
                    && response.Headers.Location.Count == 1
                    && PathOnly(response.Headers.Location[0], context.Request.Host) is { } relative)
                {
                    response.Headers.Location = relative;
                }
                return Task.CompletedTask;
            });
            return next();
        });

    // The path and query of an absolute http(s) URL on `host`; null when the location is already relative or points elsewhere.
    public static string? PathOnly(string? location, HostString host)
    {
        if (string.IsNullOrEmpty(location) || !Uri.TryCreate(location, UriKind.Absolute, out var uri)) { return null; }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) { return null; }
        var sameHost = string.Equals(uri.Host, host.Host, StringComparison.OrdinalIgnoreCase);
        return sameHost ? uri.PathAndQuery + uri.Fragment : null;
    }
}
