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

    Task<RoomEditReply> MoveRoomAsync(string token, string key, string newArea, string newLocalKey, CancellationToken cancellation = default);

    // Players (creator or admin). Core applies the rules, audits every changed field and updates the live player; edits are last
    // write wins (D30). Same exceptions as the room calls. The reply never carries a password.
    Task<ListPlayersReply> ListPlayersAsync(string token, string nameFilter, bool descending, int page, int pageSize, CancellationToken cancellation = default);

    // Null when no player has this name key.
    Task<PlayerDetail?> GetPlayerAsync(string token, string nameKey, CancellationToken cancellation = default);

    Task<UpdatePlayerReply> UpdatePlayerAsync(string token, UpdatePlayerRequest request, CancellationToken cancellation = default);

    // Modules. List is creator or admin; start, stop and restart are admin only. They only queue the request on core's supervisor
    // (the reply says so); the list shows the outcome. Same exceptions as the room calls.
    Task<IReadOnlyList<ModuleInfo>> ListModulesAsync(string token, CancellationToken cancellation = default);

    // Generated rooms (creator or admin; specs/TERRAIN_GENERATORS.md sections 9 and 10). Regen replaces description, ALL features and properties
    // with the generator's output; update rewrites only template-referenced features whose template is newer. Same exceptions as the room calls.
    Task<GenerationReply> RegenerateRoomsAsync(string token, string key, CancellationToken cancellation = default);

    Task<GenerationReply> UpdateRoomsAsync(string token, string key, CancellationToken cancellation = default);

    // Legend colours by map and terrain, for the map view only. Empty when there are none.
    Task<IReadOnlyList<TerrainColor>> ListTerrainColorsAsync(string token, CancellationToken cancellation = default);

    Task<ModuleActionReply> StartModuleAsync(string token, string name, CancellationToken cancellation = default);

    Task<ModuleActionReply> StopModuleAsync(string token, string name, CancellationToken cancellation = default);

    // Restart is admin only; core starts the new instance before it ends the old one, and restarting a stopped module starts it.
    // Every module, required ones included, can be started, stopped and restarted.
    Task<ModuleActionReply> RestartModuleAsync(string token, string name, CancellationToken cancellation = default);

    // Terrain library (creator or admin; specs/TERRAIN_GENERATORS.md section 6). Same exceptions as the room calls. Get returns null when
    // no entity has the id. Create/update/delete answer in the reply's result (Ok, NoChange, Invalid, NotFound, AlreadyExists, Refused,
    // AuditFailed, Failed). An entity with an empty terrains list is used by no generator. A feature template's version is set by core.
    Task<IReadOnlyList<FragmentRecord>> ListFragmentsAsync(string token, CancellationToken cancellation = default);

    Task<FragmentRecord?> GetFragmentAsync(string token, string id, CancellationToken cancellation = default);

    Task<LibraryEditReply> CreateFragmentAsync(string token, FragmentRecord fragment, CancellationToken cancellation = default);

    Task<LibraryEditReply> UpdateFragmentAsync(string token, FragmentRecord fragment, CancellationToken cancellation = default);

    Task<LibraryEditReply> DeleteFragmentAsync(string token, string id, CancellationToken cancellation = default);

    Task<IReadOnlyList<FeatureTemplateRecord>> ListFeatureTemplatesAsync(string token, CancellationToken cancellation = default);

    Task<FeatureTemplateRecord?> GetFeatureTemplateAsync(string token, string id, CancellationToken cancellation = default);

    Task<LibraryEditReply> CreateFeatureTemplateAsync(string token, FeatureTemplateRecord template, CancellationToken cancellation = default);

    Task<LibraryEditReply> UpdateFeatureTemplateAsync(string token, FeatureTemplateRecord template, CancellationToken cancellation = default);

    Task<LibraryEditReply> DeleteFeatureTemplateAsync(string token, string id, CancellationToken cancellation = default);

    Task<IReadOnlyList<NeighborPhraseRecord>> ListNeighborPhrasesAsync(string token, CancellationToken cancellation = default);

    Task<NeighborPhraseRecord?> GetNeighborPhraseAsync(string token, string id, CancellationToken cancellation = default);

    Task<LibraryEditReply> CreateNeighborPhraseAsync(string token, NeighborPhraseRecord phrase, CancellationToken cancellation = default);

    Task<LibraryEditReply> UpdateNeighborPhraseAsync(string token, NeighborPhraseRecord phrase, CancellationToken cancellation = default);

    Task<LibraryEditReply> DeleteNeighborPhraseAsync(string token, string id, CancellationToken cancellation = default);
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

    public Task<RoomEditReply> MoveRoomAsync(string token, string key, string newArea, string newLocalKey, CancellationToken cancellation = default) =>
        Call(() => _client.MoveRoomAsync(new MoveRoomRequest { Token = token ?? string.Empty, Key = key ?? string.Empty, NewArea = newArea ?? string.Empty, NewLocalKey = newLocalKey ?? string.Empty }, deadline: DateTime.UtcNow + CallDeadline, cancellationToken: cancellation));

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

    public async Task<IReadOnlyList<ModuleInfo>> ListModulesAsync(string token, CancellationToken cancellation = default)
    {
        var reply = await Call(() => _client.ListModulesAsync(new TokenRequest { Token = token ?? string.Empty }, deadline: DateTime.UtcNow + CallDeadline, cancellationToken: cancellation));
        return reply.Modules.ToList();
    }

    public Task<GenerationReply> RegenerateRoomsAsync(string token, string key, CancellationToken cancellation = default) =>
        Call(() => _client.RegenerateRoomsAsync(new RegenerateRoomsRequest { Token = token ?? string.Empty, Keys = { key ?? string.Empty } }, deadline: Deadline(), cancellationToken: cancellation));

    public Task<GenerationReply> UpdateRoomsAsync(string token, string key, CancellationToken cancellation = default) =>
        Call(() => _client.UpdateRoomsAsync(new UpdateRoomsRequest { Token = token ?? string.Empty, Keys = { key ?? string.Empty } }, deadline: Deadline(), cancellationToken: cancellation));

    public async Task<IReadOnlyList<TerrainColor>> ListTerrainColorsAsync(string token, CancellationToken cancellation = default) =>
        (await Call(() => _client.ListTerrainColorsAsync(new TokenRequest { Token = token ?? string.Empty }, deadline: Deadline(), cancellationToken: cancellation))).Colors.ToList();

    public Task<ModuleActionReply> StartModuleAsync(string token, string name, CancellationToken cancellation = default) =>
        Call(() => _client.StartModuleAsync(new ModuleRequest { Token = token ?? string.Empty, Name = name ?? string.Empty }, deadline: DateTime.UtcNow + CallDeadline, cancellationToken: cancellation));

    public Task<ModuleActionReply> StopModuleAsync(string token, string name, CancellationToken cancellation = default) =>
        Call(() => _client.StopModuleAsync(new ModuleRequest { Token = token ?? string.Empty, Name = name ?? string.Empty }, deadline: DateTime.UtcNow + CallDeadline, cancellationToken: cancellation));

    public Task<ModuleActionReply> RestartModuleAsync(string token, string name, CancellationToken cancellation = default) =>
        Call(() => _client.RestartModuleAsync(new ModuleRequest { Token = token ?? string.Empty, Name = name ?? string.Empty }, deadline: DateTime.UtcNow + CallDeadline, cancellationToken: cancellation));

    private static DateTime Deadline() => DateTime.UtcNow + CallDeadline;

    public async Task<IReadOnlyList<FragmentRecord>> ListFragmentsAsync(string token, CancellationToken cancellation = default) =>
        (await Call(() => _client.ListFragmentsAsync(new TokenRequest { Token = token ?? string.Empty }, deadline: Deadline(), cancellationToken: cancellation))).Fragments.ToList();

    public async Task<FragmentRecord?> GetFragmentAsync(string token, string id, CancellationToken cancellation = default)
    {
        var reply = await Call(() => _client.GetFragmentAsync(new LibraryIdRequest { Token = token ?? string.Empty, Id = id ?? string.Empty }, deadline: Deadline(), cancellationToken: cancellation));
        return reply.Found ? reply.Fragment : null;
    }

    public Task<LibraryEditReply> CreateFragmentAsync(string token, FragmentRecord fragment, CancellationToken cancellation = default) =>
        Call(() => _client.CreateFragmentAsync(new SaveFragmentRequest { Token = token ?? string.Empty, Fragment = fragment }, deadline: Deadline(), cancellationToken: cancellation));

    public Task<LibraryEditReply> UpdateFragmentAsync(string token, FragmentRecord fragment, CancellationToken cancellation = default) =>
        Call(() => _client.UpdateFragmentAsync(new SaveFragmentRequest { Token = token ?? string.Empty, Fragment = fragment }, deadline: Deadline(), cancellationToken: cancellation));

    public Task<LibraryEditReply> DeleteFragmentAsync(string token, string id, CancellationToken cancellation = default) =>
        Call(() => _client.DeleteFragmentAsync(new LibraryIdRequest { Token = token ?? string.Empty, Id = id ?? string.Empty }, deadline: Deadline(), cancellationToken: cancellation));

    public async Task<IReadOnlyList<FeatureTemplateRecord>> ListFeatureTemplatesAsync(string token, CancellationToken cancellation = default) =>
        (await Call(() => _client.ListFeatureTemplatesAsync(new TokenRequest { Token = token ?? string.Empty }, deadline: Deadline(), cancellationToken: cancellation))).Templates.ToList();

    public async Task<FeatureTemplateRecord?> GetFeatureTemplateAsync(string token, string id, CancellationToken cancellation = default)
    {
        var reply = await Call(() => _client.GetFeatureTemplateAsync(new LibraryIdRequest { Token = token ?? string.Empty, Id = id ?? string.Empty }, deadline: Deadline(), cancellationToken: cancellation));
        return reply.Found ? reply.Template : null;
    }

    public Task<LibraryEditReply> CreateFeatureTemplateAsync(string token, FeatureTemplateRecord template, CancellationToken cancellation = default) =>
        Call(() => _client.CreateFeatureTemplateAsync(new SaveFeatureTemplateRequest { Token = token ?? string.Empty, Template = template }, deadline: Deadline(), cancellationToken: cancellation));

    public Task<LibraryEditReply> UpdateFeatureTemplateAsync(string token, FeatureTemplateRecord template, CancellationToken cancellation = default) =>
        Call(() => _client.UpdateFeatureTemplateAsync(new SaveFeatureTemplateRequest { Token = token ?? string.Empty, Template = template }, deadline: Deadline(), cancellationToken: cancellation));

    public Task<LibraryEditReply> DeleteFeatureTemplateAsync(string token, string id, CancellationToken cancellation = default) =>
        Call(() => _client.DeleteFeatureTemplateAsync(new LibraryIdRequest { Token = token ?? string.Empty, Id = id ?? string.Empty }, deadline: Deadline(), cancellationToken: cancellation));

    public async Task<IReadOnlyList<NeighborPhraseRecord>> ListNeighborPhrasesAsync(string token, CancellationToken cancellation = default) =>
        (await Call(() => _client.ListNeighborPhrasesAsync(new TokenRequest { Token = token ?? string.Empty }, deadline: Deadline(), cancellationToken: cancellation))).Phrases.ToList();

    public async Task<NeighborPhraseRecord?> GetNeighborPhraseAsync(string token, string id, CancellationToken cancellation = default)
    {
        var reply = await Call(() => _client.GetNeighborPhraseAsync(new LibraryIdRequest { Token = token ?? string.Empty, Id = id ?? string.Empty }, deadline: Deadline(), cancellationToken: cancellation));
        return reply.Found ? reply.Phrase : null;
    }

    public Task<LibraryEditReply> CreateNeighborPhraseAsync(string token, NeighborPhraseRecord phrase, CancellationToken cancellation = default) =>
        Call(() => _client.CreateNeighborPhraseAsync(new SaveNeighborPhraseRequest { Token = token ?? string.Empty, Phrase = phrase }, deadline: Deadline(), cancellationToken: cancellation));

    public Task<LibraryEditReply> UpdateNeighborPhraseAsync(string token, NeighborPhraseRecord phrase, CancellationToken cancellation = default) =>
        Call(() => _client.UpdateNeighborPhraseAsync(new SaveNeighborPhraseRequest { Token = token ?? string.Empty, Phrase = phrase }, deadline: Deadline(), cancellationToken: cancellation));

    public Task<LibraryEditReply> DeleteNeighborPhraseAsync(string token, string id, CancellationToken cancellation = default) =>
        Call(() => _client.DeleteNeighborPhraseAsync(new LibraryIdRequest { Token = token ?? string.Empty, Id = id ?? string.Empty }, deadline: Deadline(), cancellationToken: cancellation));

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
