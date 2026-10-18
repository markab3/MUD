using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using MongoDB.Driver;
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
builder.Services.AddSingleton<IAccountStore>(sp => new MongoAccountStore(sp.GetRequiredService<IMongoClient>(), databaseName));
builder.Services.AddSingleton<IRoomReader>(sp => new MongoRoomReader(sp.GetRequiredService<IMongoClient>(), databaseName));
builder.Services.AddSingleton<IAccountService, AccountService>();
builder.Services.AddSingleton<LoginThrottle>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Login";
        o.AccessDeniedPath = "/access-denied";
        o.Cookie.Name = "mud.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
    });

builder.Services.AddAuthorization(WebPolicies.Configure);
var startRoomKey = builder.Configuration["WorldSettings:StartRoomKey"];
builder.Services.AddSingleton<IRoomEditStore>(sp => new MongoRoomEditStore(sp.GetRequiredService<IMongoClient>(), databaseName));
builder.Services.AddSingleton<IAuditLog>(sp => new MongoAuditLog(sp.GetRequiredService<IMongoClient>(), databaseName));
builder.Services.AddSingleton<IRoomWriter>(sp => new RoomWriter(sp.GetRequiredService<IRoomEditStore>(), sp.GetRequiredService<IAuditLog>(), startRoomKey, TimeProvider.System, sp.GetRequiredService<ILoggerFactory>().CreateLogger("RoomWriter")));
builder.Services.AddSingleton<IRoomCreationService, RoomCreationService>();

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
