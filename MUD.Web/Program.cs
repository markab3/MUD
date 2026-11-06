using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using MongoDB.Driver;
using MUD.Contracts;
using MUD.Web.Services;

// The connection string is a secret: only ever read from the environment, never printed.
var mongoUri = Environment.GetEnvironmentVariable("MUD_MONGO_URI");
if (string.IsNullOrWhiteSpace(mongoUri))
{
    Console.Error.WriteLine("MUD_MONGO_URI is not set. Set it to the MongoDB connection string (it lives in ~/.config/mud/mongo.env).");
    return 1;
}

var builder = WebApplication.CreateBuilder(args);

var databaseName = builder.Configuration["WorldSettings:DatabaseName"];
if (string.IsNullOrWhiteSpace(databaseName)) { databaseName = "testmud"; }

var url = Environment.GetEnvironmentVariable("MUD_WEB_URL");
builder.WebHost.UseUrls(string.IsNullOrWhiteSpace(url) ? "http://127.0.0.1:5080" : url);

builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoUri));

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

var app = builder.Build();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
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
