using Grpc.Core;
using MUD.Contracts;
var run = Environment.GetEnvironmentVariable("MUD_RUN_DIRECTORY")!;
Directory.CreateDirectory(run);
var sock = Path.Combine(run, "frontends.sock");
if (File.Exists(sock)) File.Delete(sock);
var b = WebApplication.CreateBuilder(args);
b.WebHost.ConfigureKestrel(k => k.ListenUnixSocket(sock, o => o.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2));
b.Services.AddGrpc();
b.Services.AddSingleton<Stand>();
var app = b.Build();
app.MapGrpcService<Stand>();
app.Run();

class Stand : Admin.AdminBase
{
    static Dictionary<string, RoomDetail> rooms = new();
    static Stand()
    {
        for (int i = 0; i < 6; i++)
        {
            var r = new RoomDetail { Key = "castle.room" + i, ShortDescription = "A castle room number " + i, LongDescription = "Long description of the room " + i, Area = "castle", X = i % 3, Y = i / 3, Z = 0 };
            r.Exits.Add(new RoomExit { Name = "east", To = "castle.room" + ((i + 1) % 6) });
            r.Features.Add(new RoomFeature { Short = "a torch", Long = "A flickering torch." });
            rooms[r.Key] = r;
        }
    }
    string Check(string token, AdminRole min = AdminRole.Player)
    {
        if (token != "tok-admin" && token != "tok-player") throw new RpcException(new Status(StatusCode.Unauthenticated, "no"));
        return token;
    }
    public override Task<WebAuthReply> WebAuthenticate(WebAuthRequest r, ServerCallContext c) =>
        Task.FromResult(r.Password == "pw" ? new WebAuthReply { Result = AuthResult.AuthOk, Token = r.Name == "Player" ? "tok-player" : "tok-admin", PlayerName = r.Name, Role = r.Name == "Player" ? AdminRole.Player : AdminRole.Admin } : new WebAuthReply { Result = AuthResult.AuthBadCredentials });
    public override Task<SessionInfoReply> GetSession(TokenRequest r, ServerCallContext c)
    { Check(r.Token); return Task.FromResult(new SessionInfoReply { PlayerName = r.Token == "tok-player" ? "Player" : "Admin", Role = r.Token == "tok-player" ? AdminRole.Player : AdminRole.Admin }); }
    public override Task<Ack> Logout(TokenRequest r, ServerCallContext c) => Task.FromResult(new Ack());
    public override Task<ListRoomsReply> ListRooms(TokenRequest r, ServerCallContext c) { Check(r.Token); var x = new ListRoomsReply(); x.Rooms.AddRange(rooms.Values); return Task.FromResult(x); }
    public override Task<GetRoomReply> GetRoom(GetRoomRequest r, ServerCallContext c) { Check(r.Token); return Task.FromResult(rooms.TryGetValue(r.Key, out var d) ? new GetRoomReply { Found = true, Room = d } : new GetRoomReply()); }
    public override Task<RoomEditReply> UpdateRoom(RoomEditRequest r, ServerCallContext c) { Check(r.Token); return Task.FromResult(new RoomEditReply { Result = AdminResult.Ok, Room = rooms[r.Key] }); }
    public override Task<TerrainColorsReply> ListTerrainColors(TokenRequest r, ServerCallContext c) => Task.FromResult(new TerrainColorsReply());
    public override Task<ListPlayersReply> ListPlayers(ListPlayersRequest r, ServerCallContext c)
    {
        Check(r.Token); var x = new ListPlayersReply { Total = 3, Page = 1, PageSize = 50 };
        foreach (var n in new[] { "Admin", "Player", "Aldrin" }) x.Items.Add(new PlayerSummary { NameKey = n.ToLower(), Name = n, Race = "human", Class = "fighter", Gender = "male", Location = "castle.room0", Role = n == "Player" ? AdminRole.Player : AdminRole.Admin });
        return Task.FromResult(x);
    }
    public override Task<GetPlayerReply> GetPlayer(GetPlayerRequest r, ServerCallContext c) { Check(r.Token); return Task.FromResult(new GetPlayerReply { Found = true, Player = new PlayerDetail { NameKey = r.NameKey, Name = r.NameKey, ShortDescription = "a hero", LongDescription = "A long tale. ".PadRight(300, 'x'), Race = "human", Class = "fighter", Gender = "male", Role = AdminRole.Player } }); }
    public override Task<UpdatePlayerReply> UpdatePlayer(UpdatePlayerRequest r, ServerCallContext c) { Check(r.Token); return Task.FromResult(new UpdatePlayerReply { Result = AdminResult.Ok, Player = new PlayerDetail { NameKey = r.NameKey, Name = r.NameKey } }); }
    public override Task<ListModulesReply> ListModules(TokenRequest r, ServerCallContext c)
    { Check(r.Token); var x = new ListModulesReply(); x.Modules.Add(new ModuleInfo { Name = "Desert", Version = "1.0", State = ModuleRunState.Ready, Running = true }); x.Modules.Add(new ModuleInfo { Name = "Temple", Version = "1.0", State = ModuleRunState.Stopped, Required = false }); return Task.FromResult(x); }
    public override Task<ListFragmentsReply> ListFragments(TokenRequest r, ServerCallContext c) { Check(r.Token); return Task.FromResult(new ListFragmentsReply()); }
    public override Task<ListFeatureTemplatesReply> ListFeatureTemplates(TokenRequest r, ServerCallContext c) { Check(r.Token); return Task.FromResult(new ListFeatureTemplatesReply()); }
    public override Task<ListNeighborPhrasesReply> ListNeighborPhrases(TokenRequest r, ServerCallContext c) { Check(r.Token); return Task.FromResult(new ListNeighborPhrasesReply()); }
}
