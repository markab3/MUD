using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MUD.Web.Services;

// Versioned URLs for the site's own static files (css/mobile.css, js/app.js, js/roommap.js, img/...): "css/mobile.css?v=<hash of the
// file's bytes>". A deploy that changes a file changes its URL, so a browser holding the previous copy fetches the new one on the next
// page load instead of pairing new markup with an old stylesheet. Each hash is computed once per process (the files cannot change under
// a running release; a deploy restarts the site). A file that cannot be read gets the bare URL.
public sealed class AssetVersions
{
    private readonly IFileProvider _files;
    private readonly ConcurrentDictionary<string, string> _versions = new();

    public AssetVersions(IFileProvider webRoot) { _files = webRoot; }

    // Relative to the page's <base href="/">, e.g. "css/mobile.css?v=3fa9c1d20b7e".
    public string Url(string path)
    {
        path = path.TrimStart('/');
        var v = _versions.GetOrAdd(path, Hash);
        return v.Length == 0 ? path : path + "?v=" + v;
    }

    // Rooted, for dynamic import() which resolves against the module's own URL rather than the base href.
    public string RootedUrl(string path) => "/" + Url(path);

    private string Hash(string path)
    {
        try
        {
            var f = _files.GetFileInfo(path);
            if (!f.Exists) { return string.Empty; }
            using var s = f.CreateReadStream();
            return Convert.ToHexString(SHA256.HashData(s)).Substring(0, 12).ToLowerInvariant();
        }
        catch (IOException) { return string.Empty; }
    }

    // Static files must be revalidated on every load (Cache-Control: no-cache; the ETag makes that a cheap 304), because the
    // stylesheet and script are replaced by a deploy. Blazor's own /_framework files are fingerprinted by the framework and keep
    // whatever caching it gives them.
    public static void PrepareResponse(StaticFileResponseContext ctx)
    {
        if (ctx.Context.Request.Path.StartsWithSegments("/_framework")) { return; }
        ctx.Context.Response.Headers.CacheControl = "no-cache";
    }
}
