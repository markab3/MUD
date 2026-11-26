using Microsoft.AspNetCore.Antiforgery;

namespace MUD.Web.Services;

// POST /next/logout (plain HTTP: a cookie cannot be removed from a circuit). Antiforgery is validated here; GET only redirects.
public static class LogoutEndpoint
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet(WebRoutes.Logout, () => Results.Redirect(WebRoutes.Home));
        app.MapPost(WebRoutes.Logout, HandlePostAsync);
    }

    public static async Task<IResult> HandlePostAsync(HttpContext http, IAntiforgery antiforgery, IAdminClient core)
    {
        try { await antiforgery.ValidateRequestAsync(http); }
        catch (AntiforgeryValidationException) { return Results.BadRequest(); }

        // Core ends the session; the cookie goes either way, so the user is signed out of the browser even if core cannot be told.
        if (http.Request.Cookies.TryGetValue(SessionAuthentication.CookieName, out var token))
        {
            await core.LogoutAsync(token, http.RequestAborted);
        }
        http.Response.Cookies.Delete(SessionAuthentication.CookieName, SessionAuthentication.CookieOptionsFor(http.Request));
        return Results.Redirect(WebRoutes.Login);
    }
}
