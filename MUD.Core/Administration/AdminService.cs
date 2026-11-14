using System;
using System.Threading.Tasks;
using Grpc.Core;
using MUD.Contracts;

namespace MUD.Core.Administration
{
    /// <summary>
    /// The Admin gRPC service served on frontends.sock (owner decisions D22, D66). A thin layer over <see cref="AdminApi"/>: it only maps
    /// a refused call to a gRPC status. A bad, revoked or unknown token answers Unauthenticated, a role that is too low answers
    /// PermissionDenied, and a game thread that did not run the call answers Unavailable. Messages are fixed sentences; no token, password or
    /// stored value is ever put in a status.
    /// </summary>
    public sealed class AdminService : Admin.AdminBase
    {
        private readonly AdminApi _api;

        public AdminService(AdminApi api)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
        }

        public override Task<WebAuthReply> WebAuthenticate(WebAuthRequest request, ServerCallContext context) { return Run(() => _api.WebAuthenticate(request)); }

        public override Task<Ack> Logout(TokenRequest request, ServerCallContext context) { return Run(() => _api.Logout(request)); }

        public override Task<SessionInfoReply> GetSession(TokenRequest request, ServerCallContext context) { return Run(() => _api.GetSession(request)); }

        public override Task<ChangePasswordReply> ChangePassword(ChangePasswordRequest request, ServerCallContext context) { return Run(() => _api.ChangePassword(request)); }

        public override Task<ListRoomsReply> ListRooms(TokenRequest request, ServerCallContext context) { return Run(() => _api.ListRooms(request)); }

        public override Task<GetRoomReply> GetRoom(GetRoomRequest request, ServerCallContext context) { return Run(() => _api.GetRoom(request)); }

        public override Task<RoomEditReply> CreateRoom(RoomEditRequest request, ServerCallContext context) { return Run(() => _api.CreateRoom(request)); }

        public override Task<RoomEditReply> UpdateRoom(RoomEditRequest request, ServerCallContext context) { return Run(() => _api.UpdateRoom(request)); }

        public override Task<RoomEditReply> DeleteRoom(DeleteRoomRequest request, ServerCallContext context) { return Run(() => _api.DeleteRoom(request)); }

        public override Task<ListPlayersReply> ListPlayers(ListPlayersRequest request, ServerCallContext context) { return Run(() => _api.ListPlayers(request)); }

        public override Task<GetPlayerReply> GetPlayer(GetPlayerRequest request, ServerCallContext context) { return Run(() => _api.GetPlayer(request)); }

        public override Task<UpdatePlayerReply> UpdatePlayer(UpdatePlayerRequest request, ServerCallContext context) { return Run(() => _api.UpdatePlayer(request)); }

        public override Task<ListAuditReply> ListAudit(ListAuditRequest request, ServerCallContext context) { return Run(() => _api.ListAudit(request)); }

        // The database calls block, so they run on a pool thread and never hold a Kestrel request thread.
        private static Task<T> Run<T>(Func<T> call)
        {
            return Task.Run(() =>
            {
                try { return call(); }
                catch (AdminCallException ex) { throw new RpcException(new Status(ToStatus(ex.Failure), ex.Message)); }
            });
        }

        public static StatusCode ToStatus(AdminFailure failure)
        {
            switch (failure)
            {
                case AdminFailure.Unauthenticated: return StatusCode.Unauthenticated;
                case AdminFailure.Forbidden: return StatusCode.PermissionDenied;
                default: return StatusCode.Unavailable;
            }
        }
    }
}
