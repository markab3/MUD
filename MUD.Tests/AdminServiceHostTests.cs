using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using MUD.Contracts;
using MUD.Core.Administration;
using MUD.Core.Modules;
using MUD.Main;
using Xunit;

namespace MUD.Tests
{
    /// <summary>Card P6-1: the Admin service over the real Kestrel host on frontends.sock. No database: the API runs on fakes.</summary>
    public class AdminServiceHostTests : IDisposable
    {
        private readonly string _directory;
        private readonly List<string> _log = new List<string>();

        public AdminServiceHostTests()
        {
            // Short path: a Unix socket path is limited to 107 bytes.
            _directory = Path.Combine(Path.GetTempPath(), "ah" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_directory);
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, true); } catch (Exception) { }
        }

        private static GrpcChannel ChannelFor(string socketPath)
        {
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (context, cancellation) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellation);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };
            return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler });
        }

        private ModuleRegistry NewRegistry()
        {
            string grants = Path.Combine(_directory, "grants.yaml");
            File.WriteAllText(grants, "modules: {}\n");
            File.SetUnixFileMode(grants, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var moduleGrants = new ModuleGrants(grants, _log.Add);
            Assert.True(moduleGrants.Reload());
            return new ModuleRegistry(new ModuleTokenRegistry(), moduleGrants, ContractVersion.Current, new ModuleHostOptions(), _log.Add);
        }

        [Fact]
        public async Task TheAdminServiceAnswersOnFrontendsSockAndNotOnModulesSock()
        {
            var tokens = new AdminSessions();
            tokens.Issue("p.ann", "ann");
            // A real AdminApi over a host that never runs game work: the only calls made here are refused before they need the game thread.
            var host = new NoGameThreadHost();
            var api = new AdminApi(host, new SyncPlayerRepo(), new SyncRoomRepo(), new NoQueries(), new NoAudit(), tokens, new FakeClock(), _log.Add);
            using (RunDirectory directory = RunDirectory.Acquire(Path.Combine(_directory, "r"), _log.Add))
            using (var runner = new ModuleHostRunner(directory, NewRegistry(), action => false, _log.Add, null, api))
            {
                runner.Start();
                using (GrpcChannel channel = ChannelFor(directory.FrontEndsSocketPath))
                {
                    var client = new Admin.AdminClient(channel);

                    // An unknown token answers Unauthenticated, not a result code.
                    RpcException unknown = await Assert.ThrowsAsync<RpcException>(async () => await client.GetSessionAsync(new TokenRequest { Token = "nope" }));
                    Assert.Equal(StatusCode.Unauthenticated, unknown.StatusCode);

                    // A mutating call needs the game thread; one that is not running answers Unavailable.
                    RpcException unavailable = await Assert.ThrowsAsync<RpcException>(async () => await client.UpdateRoomAsync(new RoomEditRequest { Token = "nope", Key = "t.a" }));
                    Assert.Equal(StatusCode.Unavailable, unavailable.StatusCode);

                    // Logout needs no token and always succeeds.
                    Assert.True((await client.LogoutAsync(new TokenRequest { Token = "nope" })).Ok);

                    // A throttled address is answered without touching the game thread.
                    host.Throttled = true;
                    WebAuthReply throttled = await client.WebAuthenticateAsync(new WebAuthRequest { Name = "ann", Password = "x", RemoteKey = "k" });
                    Assert.Equal(AuthResult.AuthThrottled, throttled.Result);
                    Assert.Equal(string.Empty, throttled.Token);
                }

                // Not served on modules.sock (D22): the same path answers Unimplemented there.
                using (GrpcChannel channel = ChannelFor(directory.ModulesSocketPath))
                {
                    var client = new Admin.AdminClient(channel);
                    RpcException wrongSocket = await Assert.ThrowsAsync<RpcException>(async () => await client.LogoutAsync(new TokenRequest { Token = "nope" }));
                    Assert.Equal(StatusCode.Unimplemented, wrongSocket.StatusCode);
                }
            }
        }

        [Fact]
        public void WithNoAdminApiTheFrontendsSockServesNoAdminService()
        {
            using (RunDirectory directory = RunDirectory.Acquire(Path.Combine(_directory, "r"), _log.Add))
            using (var runner = new ModuleHostRunner(directory, NewRegistry(), action => false, _log.Add))
            {
                runner.Start();
                using (GrpcChannel channel = ChannelFor(directory.FrontEndsSocketPath))
                {
                    var client = new Admin.AdminClient(channel);
                    RpcException none = Assert.Throws<RpcException>(() => client.Logout(new TokenRequest { Token = "x" }));
                    Assert.Equal(StatusCode.Unimplemented, none.StatusCode);
                }
            }
        }

        [Theory]
        [InlineData(AdminFailure.Unauthenticated, StatusCode.Unauthenticated)]
        [InlineData(AdminFailure.Forbidden, StatusCode.PermissionDenied)]
        [InlineData(AdminFailure.Unavailable, StatusCode.Unavailable)]
        public void RefusalsMapToGrpcStatusCodes(AdminFailure failure, StatusCode expected)
        {
            Assert.Equal(expected, AdminService.ToStatus(failure));
        }

        private class NoQueries : IAdminQueries
        {
            public List<MongoDB.Bson.BsonDocument> ListPlayers(string nameFilter, bool descending, int page, int pageSize, out int total) { total = 0; return new List<MongoDB.Bson.BsonDocument>(); }
            public MongoDB.Bson.BsonDocument FindPlayer(string nameKey) { return null; }
            public int CountAdmins() { return 0; }
            public List<MongoDB.Bson.BsonDocument> ListRooms() { return new List<MongoDB.Bson.BsonDocument>(); }
            public List<string> RoomIdsWithExitTo(string key) { return new List<string>(); }
        }

        private class NoAudit : IAuditStore
        {
            public void Insert(IReadOnlyList<AuditRecord> rows) { }
            public void SetOutcome(IReadOnlyList<string> ids, bool applied) { }
            public AuditPage Query(string entity, string entityId, int page, int pageSize) { return new AuditPage { Items = new List<AuditRecord>(), Total = 0 }; }
        }

        private class NoGameThreadHost : IAdminHost
        {
            public bool Throttled;
            public string StartRoomKey { get { return null; } }
            public bool TryRunOnGameThread<T>(Func<T> work, out T result) { result = default(T); return false; }
            public bool IsThrottled(string remoteKey) { return Throttled; }
            public void RecordLoginOutcome(string remoteKey, bool success) { }
            public bool IsOnline(MUD.Core.GameObjects.Player player) { return false; }
        }
    }
}
