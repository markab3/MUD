using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.SignalR;
using Microsoft.FluentUI.AspNetCore.Components;
using MUD.Contracts;
using MUD.Web.Services;

var builder = WebApplication.CreateBuilder(args);

var url = Environment.GetEnvironmentVariable("MUD_WEB_URL");
builder.WebHost.UseUrls(string.IsNullOrWhiteSpace(url) ? "http://127.0.0.1:5080" : url);

// Accounts and sign-in go through core's Admin service on frontends.sock (D22, D31). The cookie holds only the session token.
builder.Services.AddSingleton<IAdminClient>(_ =>
{
    var socketPath = Environment.GetEnvironmentVariable("MUD_FRONTENDS_SOCKET");
    if (string.IsNullOrWhiteSpace(socketPath)) { socketPath = CoreSocket.ResolvePath(); }
    return new GrpcAdminClient(new Admin.AdminClient(CoreSocket.OpenChannel(socketPath)));
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAccountService, SessionAccountService>();
// Still used by the password page until core throttles password changes as well.
builder.Services.AddSingleton<LoginThrottle>();

builder.Services.AddAuthentication(SessionAuthentication.Scheme)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthentication.Scheme, _ => { });

builder.Services.AddAuthorization(WebPolicies.Configure);
// Rooms and the room map go through core's Admin service too; core validates, audits and applies room edits.
builder.Services.AddSingleton<IRoomCreationService, RoomCreationService>();
// The player pages go through the Admin service as well; core applies the rules and writes the audit rows (D34).

builder.Services.AddRazorPages();

// Blazor Server with Fluent UI (WEB_BLAZOR.md): beside the Razor Pages, under /next, until the cutover card. Interactive server only.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddFluentUIComponents();
builder.Services.AddScoped<NoticeService>();
builder.Services.AddScoped<DraftStore>();
builder.Services.AddScoped<AuthenticationStateProvider, SessionAuthenticationStateProvider>();
builder.Services.Configure<CircuitOptions>(o =>
{
    // Mobile connections drop (screen lock, network change): keep a disconnected circuit long enough to be resumed (6.1).
    o.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(20);
    o.DisconnectedCircuitMaxRetained = 50;
});
builder.Services.Configure<HubOptions>(o =>
{
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    o.KeepAliveInterval = TimeSpan.FromSeconds(15);
});

var app = builder.Build();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapRazorPages();
LogoutEndpoint.Map(app);
app.MapRazorComponents<MUD.Web.Components.App>().AddInteractiveServerRenderMode();
app.Run();
return 0;

public static class WebPolicies
{
    public const string CreatorOrAdmin = "CreatorOrAdmin";

    public static void Configure(Microsoft.AspNetCore.Authorization.AuthorizationOptions o) =>
        o.AddPolicy(CreatorOrAdmin, p => p
            .RequireAuthenticatedUser()
            .RequireClaim(ClaimTypes.Role, nameof(AccountRole.Creator), nameof(AccountRole.Admin)));
}
