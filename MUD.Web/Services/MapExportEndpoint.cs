using System.Text;
using Microsoft.AspNetCore.Authorization;

namespace MUD.Web.Services;

// GET /creator/map/export?area=<prefix>: the area as a YAML download (Blazor Server cannot set Content-Disposition from a component).
// Read-only, so no antiforgery. Anonymous -> challenge (login redirect); a signed-in player -> 403; never cacheable. The file name is the
// validated area prefix and nothing else. On any failure nothing is downloaded: the browser is sent back to the map with a notice.
public static class MapExportEndpoint
{
    public const string ContentType = "text/yaml; charset=utf-8";
    public const int MaxErrors = 5;
    public const int MaxErrorLength = 300;

    // Notice kinds carried in ?export= on the map page.
    public const string KindFailed = "failed";
    public const string KindUnavailable = "unavailable";
    public const string KindNotFound = "notfound";
    public const string KindInvalid = "invalid";
    public const string KindForbidden = "forbidden";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet(WebRoutes.MapExport, HandleAsync);
    }

    public static async Task<IResult> HandleAsync(HttpContext http, IAdminClient core, IAuthorizationService authorization, string? area)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";

        if (http.User.Identity?.IsAuthenticated != true) { return Results.Challenge(authenticationSchemes: new[] { SessionAuthentication.Scheme }); }
        var allowed = await authorization.AuthorizeAsync(http.User, WebPolicies.CreatorOrAdmin);
        if (!allowed.Succeeded) { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        // The prefix grammar is the only thing that can reach the file name or the core call: no quote, slash, CR or LF can match it.
        if (!IsValidPrefix(area)) { return Results.Redirect(WebRoutes.MapExportNotice(area, KindInvalid), false); }
        var token = SessionAuthentication.TokenFrom(http.Request);
        if (token.Length == 0) { token = http.User.FindFirst(SessionAuthentication.TokenClaim)?.Value ?? string.Empty; }

        MUD.Contracts.ExportAreaReply reply;
        try { reply = await core.ExportAreaAsync(token, area!, http.RequestAborted); }
        catch (CoreUnavailableException) { return Results.Redirect(WebRoutes.MapExportNotice(area, KindUnavailable), false); }
        catch (CoreRefusedException) { return Results.Redirect(WebRoutes.MapExportNotice(area, KindForbidden), false); }

        if (!reply.Found) { return Results.Redirect(WebRoutes.MapExportNotice(area, KindNotFound), false); }
        if (reply.Errors.Count > 0 || string.IsNullOrEmpty(reply.Yaml))
        {
            return Results.Redirect(WebRoutes.MapExportNotice(area, KindFailed, reply.Errors), false);
        }
        return Results.File(new UTF8Encoding(false).GetBytes(reply.Yaml), ContentType, area + ".yaml");
    }

    public static bool IsValidPrefix(string? area) =>
        !string.IsNullOrEmpty(area) && area.Length <= 100 && MUD.Data.RoomRules.PrefixPattern.IsMatch(area);

    // The sentence the map page shows for a notice kind (fixed text; the core errors, when there are any, follow as a list).
    public static string? NoticeFor(string? kind) => kind switch
    {
        KindFailed => "The area could not be exported. Nothing was downloaded.",
        KindUnavailable => "The game is not reachable right now, so the area could not be exported. Please try again later.",
        KindNotFound => "That area has no rooms to export. Nothing was downloaded.",
        KindInvalid => "That is not a valid area prefix. Nothing was downloaded.",
        KindForbidden => "You are not allowed to export areas, or your session ended. Nothing was downloaded.",
        _ => null,
    };
}
