using System.Net.Sockets;
using Grpc.Core;
using Grpc.Net.Client;
using MUD.Contracts;

namespace MUD.Web.Services;

// The web's whole view of core for accounts: the Admin service on frontends.sock (D22, D31). The web never reads the player
// collection for sign-in any more. Passwords are passed through and never logged.
public interface IAdminClient
{
    Task<WebAuthReply> WebAuthenticateAsync(string name, string password, string remoteKey, CancellationToken cancellation = default);

    // Always succeeds from the caller's point of view, also for a token that is already gone or when core is not reachable.
    Task LogoutAsync(string token, CancellationToken cancellation = default);

    // The player name and CURRENT role behind the token, or null when the token is unknown, revoked, or its player is retired or gone.
    // Throws CoreUnavailableException when core could not answer.
    Task<SessionInfoReply?> GetSessionAsync(string token, CancellationToken cancellation = default);

    Task<ChangePasswordReply> ChangePasswordAsync(string token, string currentPassword, string newPassword, string confirmPassword, CancellationToken cancellation = default);

    // Rooms (creator or admin). Core validates, audits and applies every edit; edits are last write wins (D30), so there is no revision.
    // Every room call throws CoreUnavailableException when core could not answer and CoreRefusedException when core refused the
    // token or the caller's current role.
    Task<IReadOnlyList<RoomDetail>> ListRoomsAsync(string token, CancellationToken cancellation = default);

    // Null when no room has this key.
    Task<RoomDetail?> GetRoomAsync(string token, string key, CancellationToken cancellation = default);

    Task<RoomEditReply> CreateRoomAsync(string token, RoomEditRequest request, CancellationToken cancellation = default);

    Task<RoomEditReply> UpdateRoomAsync(string token, RoomEditRequest request, CancellationToken cancellation = default);

    Task<RoomEditReply> DeleteRoomAsync(string token, string key, CancellationToken cancellation = default);

    // Players (creator or admin). Core applies the rules, audits every changed field and updates the live player; edits are last
    // write wins (D30). Same exceptions as the room calls. The reply never carries a password.
    Task<ListPlayersReply> ListPlayersAsync(string token, string nameFilter, bool descending, int page, int pageSize, CancellationToken cancellation = default);

    // Null when no player has this name key.
    Task<PlayerDetail?> GetPlayerAsync(string token, string nameKey, CancellationToken cancellation = default);

    Task<UpdatePlayerReply> UpdatePlayerAsync(string token, UpdatePlayerRequest request, CancellationToken cancellation = default);
}

// Core refused the session token (SessionEnded: unknown, revoked, or its player is retired) or the caller's current role is too low.
public class CoreRefusedException : Exception
{
    public CoreRefusedException(bool sessionEnded) : base("Core refused the request.") { SessionEnded = sessionEnded; }

    public bool SessionEnded { get; }
}

// Core did not answer (not running, restarting, game thread busy). The message is fixed and carries no request values.
public class CoreUnavailableException : Exception
{
    public CoreUnavailableException() : base("Core is not reachable.") { }
}

// Where frontends.sock is. Same rule as core's RunDirectory.ResolvePath (CORE_API 7.3): MUD_RUN_DIRECTORY, else
// $XDG_RUNTIME_DIR/mud, else ~/.local/share/mud/run. The web does not reference MUD.Core, so the rule is repeated here.
public static class CoreSocket
{
    public const string RunDirectoryVariable = "MUD_RUN_DIRECTORY";
    public const string FrontEndsSocketName = "frontends.sock";

    public static string ResolvePath()
    {
        var overridePath = Environment.GetEnvironmentVariable(RunDirectoryVariable);
        var directory = !string.IsNullOrWhiteSpace(overridePath)
            ? overridePath
            : !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"))
                ? Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")!, "mud")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "mud", "run");
        return Path.Combine(Path.GetFullPath(directory), FrontEndsSocketName);
    }

    public static GrpcChannel OpenChannel(string socketPath)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellation) =>
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
            },
        };
        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });
    }
}

public sealed class GrpcAdminClient : IAdminClient
{
    private static readonly TimeSpan CallDeadline = TimeSpan.FromSeconds(10);

    private readonly Admin.AdminClient _client;

    public GrpcAdminClient(Admin.AdminClient client) { _client = client; }

    public async Task<WebAuthReply> WebAuthenticateAsync(string name, string password, string remoteKey, CancellationToken cancellation = default)
    {
        try
        {
            return await _client.WebAuthenticateAsync(
                new WebAuthRequest { Name = name ?? string.Empty, Password = password ?? string.Empty, RemoteKey = remoteKey ?? string.Empty },
                deadline: DateTime.UtcNow + CallDeadline, cancellationToken: cancellation);
        }
        catch (RpcException)
        {
            throw new CoreUnavailableException();
        }
    }

    public async Task LogoutAsync(string token, CancellationToken cancellation = default)
    {
        if (string.IsNullOrEmpty(token)) { return; }
        try { await _client.LogoutAsync(new TokenRequest { Token = token }, deadline: DateTime.UtcNow + CallDeadline, cancellationToken: cancellation); }
        catch (RpcException) { /* Logout always succeeds for the user; a session core cannot be told about ends when core restarts. */ }
    }

    public async Task<SessionInfoReply?> GetSessionAsync(string token, CancellationToken cancellation = default)
    {
        if (string.IsNullOrEmpty(token)) { return null; }
        try
        {
            return await _client.GetSessionAsync(new TokenRequest { Token = token }, deadline: DateTime.UtcNow + CallDeadline, cancellationToken: cancellation);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unauthenticated or StatusCode.PermissionDenied)
        {
            return null;
        }
        catch (RpcException)
        {
            throw new CoreUnavailableException();
        }
    }

    public async Task<ChangePasswordReply> ChangePasswordAsync(string token, string currentPassword, string newPassword, string confirmPassword, CancellationToken cancellation = default)
    {
        try
        {
            return await _client.ChangePasswordAsync(
                new ChangePasswordRequest
                {
                    Token = token ?? string.Empty,
                    CurrentPassword = currentPassword ?? string.Empty,
                    NewPassword = newPassword ?? string.Empty,
                    ConfirmPassword = confirmPassword ?? string.Empty,
                },
                deadline: DateTime.UtcNow + CallDeadline, cancellationToken: cancellation);
        }
        catch (RpcException)
        {
            throw new CoreUnavailableException();
        }
    }

    public async Task<IReadOnlyList<RoomDetail>> ListRoomsAsync(string token, CancellationToken cancellation = default)
    {
        var reply = await Call(() => _client.ListRoomsAsync(new TokenRequest { Token = token ?? string.Empty }, deadline: DateTime.UtcNow + CallDeadline, cancellationToken: cancellation));
        return reply.Rooms.ToList();
    }

    public async Task<RoomDetail?> GetRoomAsync(string token, string key, CancellationToken cancellation = default)
    {
        var reply = await Call(() => _client.GetRoomAsync(new GetRoomRequest { Token = token ?? string.Empty, Key = key ?? string.Empty }, deadline: DateTime.UtcNow + CallDeadline, cancellationToken: cancellation));
        return reply.Found ? reply.Room : null;
    }

    public Task<RoomEditReply> CreateRoomAsync(string token, RoomEditRequest request, CancellationToken cancellation = default)
    {
        request.Token = token ?? string.Empty;
        return Call(() => _client.CreateRoomAsync(request, deadline: DateTime.UtcNow + CallDeadline, cancellationToken: cancellation));
    }

    public Task<RoomEditReply> UpdateRoomAsync(string token, RoomEditRequest request, CancellationToken cancellation = default)
    {
        request.Token = token ?? string.Empty;
        return Call(() => _client.UpdateRoomAsync(request, deadline: DateTime.UtcNow + CallDeadline, cancellationToken: cancellation));
    }

    public Task<RoomEditReply> DeleteRoomAsync(string token, string key, CancellationToken cancellation = default) =>
        Call(() => _client.DeleteRoomAsync(new DeleteRoomRequest { Token = token ?? string.Empty, Key = key ?? string.Empty }, deadline: DateTime.UtcNow + CallDeadline, cancellationToken: cancellation));

    public Task<ListPlayersReply> ListPlayersAsync(string token, string nameFilter, bool descending, int page, int pageSize, CancellationToken cancellation = default) =>
        Call(() => _client.ListPlayersAsync(
            new ListPlayersRequest { Token = token ?? string.Empty, NameFilter = nameFilter ?? string.Empty, Descending = descending, Page = page, PageSize = pageSize },
            deadline: DateTime.UtcNow + CallDeadline, cancellationToken: cancellation));

    public async Task<PlayerDetail?> GetPlayerAsync(string token, string nameKey, CancellationToken cancellation = default)
    {
        var reply = await Call(() => _client.GetPlayerAsync(new GetPlayerRequest { Token = token ?? string.Empty, NameKey = nameKey ?? string.Empty }, deadline: DateTime.UtcNow + CallDeadline, cancellationToken: cancellation));
        return reply.Found ? reply.Player : null;
    }

    public Task<UpdatePlayerReply> UpdatePlayerAsync(string token, UpdatePlayerRequest request, CancellationToken cancellation = default)
    {
        request.Token = token ?? string.Empty;
        return Call(() => _client.UpdatePlayerAsync(request, deadline: DateTime.UtcNow + CallDeadline, cancellationToken: cancellation));
    }

    // Unauthenticated and PermissionDenied are core's answers about the caller; any other failure means core could not answer.
    private static async Task<T> Call<T>(Func<Grpc.Core.AsyncUnaryCall<T>> call)
    {
        try
        {
            return await call();
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
        {
            throw new CoreRefusedException(sessionEnded: true);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
        {
            throw new CoreRefusedException(sessionEnded: false);
        }
        catch (RpcException)
        {
            throw new CoreUnavailableException();
        }
    }
}
