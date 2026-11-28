using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MUD.Contracts;
using MUD.Web.Pages;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

// A fake of core's Admin service as the web sees it. Tests drive the outcome; nothing here touches a database or a socket.
public class FakeAdminClient : IAdminClient
{
    public AuthResult AuthenticateResult { get; set; } = AuthResult.AuthOk;
    public string IssuedToken { get; set; } = "token-1";
    public bool Unavailable { get; set; }
    public List<(string Name, string Password, string RemoteKey)> Authentications { get; } = new();
    public List<string> LoggedOut { get; } = new();
    public Dictionary<string, SessionInfoReply> Sessions { get; } = new();
    public PasswordChangeOutcome PasswordOutcome { get; set; } = PasswordChangeOutcome.Changed;
    public string PasswordMessage { get; set; } = string.Empty;
    public List<(string Token, string Current, string New, string Confirm)> PasswordChanges { get; } = new();

    public Task<WebAuthReply> WebAuthenticateAsync(string name, string password, string remoteKey, CancellationToken cancellation = default)
    {
        if (Unavailable) { throw new CoreUnavailableException(); }
        Authentications.Add((name, password, remoteKey));
        return Task.FromResult(new WebAuthReply
        {
            Result = AuthenticateResult,
            Token = AuthenticateResult == AuthResult.AuthOk ? IssuedToken : string.Empty,
            PlayerName = name,
        });
    }

    public Task LogoutAsync(string token, CancellationToken cancellation = default)
    {
        LoggedOut.Add(token);
        Sessions.Remove(token);
        return Task.CompletedTask;
    }

    public Task<SessionInfoReply?> GetSessionAsync(string token, CancellationToken cancellation = default)
    {
        if (Unavailable) { throw new CoreUnavailableException(); }
        return Task.FromResult(Sessions.TryGetValue(token, out var session) ? session : null);
    }

    public Task<ChangePasswordReply> ChangePasswordAsync(string token, string currentPassword, string newPassword, string confirmPassword, CancellationToken cancellation = default)
    {
        if (Unavailable) { throw new CoreUnavailableException(); }
        PasswordChanges.Add((token, currentPassword, newPassword, confirmPassword));
        return Task.FromResult(new ChangePasswordReply { Outcome = PasswordOutcome, Message = PasswordMessage });
    }

    public Dictionary<string, OwnPlayerReply> OwnPlayers { get; } = new();
    public List<string> OwnPlayerTokens { get; } = new();
    public bool RefuseOwnPlayer { get; set; }

    public Task<OwnPlayerReply> GetOwnPlayerAsync(string token, CancellationToken cancellation = default)
    {
        if (Unavailable) { throw new CoreUnavailableException(); }
        OwnPlayerTokens.Add(token);
        if (RefuseOwnPlayer || !OwnPlayers.TryGetValue(token, out var own)) { throw new CoreRefusedException(sessionEnded: true); }
        return Task.FromResult(own);
    }

    // Rooms. The fake keeps the rooms it is given and answers edits with the reply a test sets; it records what the web sent.
    public Dictionary<string, RoomDetail> Rooms { get; } = new();
    public bool RefuseRoomCalls { get; set; }
    public bool RefuseBecauseSessionEnded { get; set; }
    public AdminResult RoomEditResult { get; set; } = AdminResult.Ok;
    public string RoomEditMessage { get; set; } = string.Empty;
    public List<FieldError> RoomEditErrors { get; } = new();
    public List<RoomEditRequest> RoomCreates { get; } = new();
    public List<RoomEditRequest> RoomUpdates { get; } = new();
    public List<string> RoomDeletes { get; } = new();
    public List<string> RoomTokens { get; } = new();

    public FakeAdminClient AddRoom(string key, string area = "t", int? x = 0, int? y = 0, int? z = 0, params (string Dir, string To)[] exits)
    {
        var room = new RoomDetail { Key = key, ShortDescription = "short " + key, LongDescription = "long " + key, Area = area };
        if (x.HasValue) { room.X = x.Value; }
        if (y.HasValue) { room.Y = y.Value; }
        if (z.HasValue) { room.Z = z.Value; }
        room.Exits.AddRange(exits.Select(e => new RoomExit { Name = e.Dir, To = e.To }));
        Rooms[key] = room;
        return this;
    }

    private void CheckRoomCall(string token)
    {
        if (Unavailable) { throw new CoreUnavailableException(); }
        RoomTokens.Add(token);
        if (RefuseRoomCalls) { throw new CoreRefusedException(RefuseBecauseSessionEnded); }
    }

    private RoomEditReply EditReply(RoomDetail? room)
    {
        var reply = new RoomEditReply { Result = RoomEditResult, Message = RoomEditMessage };
        reply.Errors.AddRange(RoomEditErrors);
        if (room != null) { reply.Room = room; }
        return reply;
    }

    public Task<IReadOnlyList<RoomDetail>> ListRoomsAsync(string token, CancellationToken cancellation = default)
    {
        CheckRoomCall(token);
        return Task.FromResult<IReadOnlyList<RoomDetail>>(Rooms.Values.ToList());
    }

    public Task<RoomDetail?> GetRoomAsync(string token, string key, CancellationToken cancellation = default)
    {
        CheckRoomCall(token);
        return Task.FromResult(Rooms.TryGetValue(key, out var room) ? room : null);
    }

    public Task<RoomEditReply> CreateRoomAsync(string token, RoomEditRequest request, CancellationToken cancellation = default)
    {
        CheckRoomCall(token);
        RoomCreates.Add(request);
        return Task.FromResult(EditReply(null));
    }

    public Task<RoomEditReply> UpdateRoomAsync(string token, RoomEditRequest request, CancellationToken cancellation = default)
    {
        CheckRoomCall(token);
        RoomUpdates.Add(request);
        RoomDetail? current = null;
        if (RoomEditResult == AdminResult.Ok && Rooms.TryGetValue(request.Key, out var existing))
        {
            // What core would answer: the room as it is after the edit.
            current = existing.Clone();
            current.ShortDescription = request.ShortDescription;
            current.LongDescription = request.LongDescription;
            current.Area = request.Area;
            current.ClearX(); current.ClearY(); current.ClearZ();
            if (request.HasX) { current.X = request.X; }
            if (request.HasY) { current.Y = request.Y; }
            if (request.HasZ) { current.Z = request.Z; }
            current.Exits.Clear();
            current.Exits.AddRange(request.Exits);
            // Behaviors and searches are read-only: an edit never changes them (they stay as cloned from the stored room).
            // What core does: Features change only when the caller says it sent them (features_present).
            if (request.FeaturesPresent)
            {
                current.Features.Clear();
                current.Features.AddRange(request.Features);
            }
            Rooms[request.Key] = current;
        }
        else if (Rooms.TryGetValue(request.Key, out var unchanged))
        {
            current = unchanged;
        }
        return Task.FromResult(EditReply(current));
    }

    public Task<RoomEditReply> DeleteRoomAsync(string token, string key, CancellationToken cancellation = default)
    {
        CheckRoomCall(token);
        RoomDeletes.Add(key);
        if (RoomEditResult == AdminResult.Ok) { Rooms.Remove(key); }
        return Task.FromResult(EditReply(null));
    }

    public List<(string Key, string Area, string Local)> RoomMoves { get; } = new();

    public Task<RoomEditReply> MoveRoomAsync(string token, string key, string newArea, string newLocalKey, CancellationToken cancellation = default)
    {
        CheckRoomCall(token);
        RoomMoves.Add((key, newArea, newLocalKey));
        if (RoomEditResult == AdminResult.Ok && Rooms.Remove(key, out var room))
        {
            room.Key = newArea + "." + newLocalKey;
            room.Area = newArea;
            Rooms[room.Key] = room;
            return Task.FromResult(EditReply(room));
        }
        return Task.FromResult(EditReply(null));
    }

    // Players. The fake keeps the players it is given and answers an edit with the reply a test sets; it records what the web sent.
    public Dictionary<string, PlayerDetail> Players { get; } = new();
    public AdminResult PlayerEditResult { get; set; } = AdminResult.Ok;
    public List<FieldError> PlayerEditErrors { get; } = new();
    public List<UpdatePlayerRequest> PlayerUpdates { get; } = new();
    public List<string> PlayerTokens { get; } = new();
    public List<(string Filter, bool Descending, int Page, int PageSize)> PlayerListCalls { get; } = new();
    public bool RefusePlayerCalls { get; set; }

    public FakeAdminClient AddPlayer(string name, AdminRole role = AdminRole.Player, bool retired = false)
    {
        var key = name.ToLowerInvariant();
        Players[key] = new PlayerDetail
        {
            NameKey = key, Name = name, ShortDescription = name, LongDescription = "tall", Race = "human", Gender = "female", Class = "fighter",
            Retired = retired, Role = role,
        };
        return this;
    }

    private void CheckPlayerCall(string token)
    {
        if (Unavailable) { throw new CoreUnavailableException(); }
        PlayerTokens.Add(token);
        if (RefusePlayerCalls) { throw new CoreRefusedException(RefuseBecauseSessionEnded); }
    }

    public Task<ListPlayersReply> ListPlayersAsync(string token, string nameFilter, bool descending, int page, int pageSize, CancellationToken cancellation = default)
    {
        CheckPlayerCall(token);
        PlayerListCalls.Add((nameFilter, descending, page, pageSize));
        var all = Players.Values
            .Where(p => string.IsNullOrWhiteSpace(nameFilter) || p.Name.Contains(nameFilter.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.NameKey, StringComparer.Ordinal).ToList();
        if (descending) { all.Reverse(); }
        var reply = new ListPlayersReply { Total = all.Count, Page = page, PageSize = pageSize };
        reply.Items.AddRange(all.Skip((page - 1) * pageSize).Take(pageSize).Select(p => new PlayerSummary
        {
            NameKey = p.NameKey, Name = p.Name, Race = p.Race, Class = p.Class, Gender = p.Gender, Location = "t.hall", Role = p.Role, Retired = p.Retired,
        }));
        return Task.FromResult(reply);
    }

    public Task<PlayerDetail?> GetPlayerAsync(string token, string nameKey, CancellationToken cancellation = default)
    {
        CheckPlayerCall(token);
        return Task.FromResult(Players.TryGetValue(nameKey, out var player) ? player : null);
    }

    public Task<UpdatePlayerReply> UpdatePlayerAsync(string token, UpdatePlayerRequest request, CancellationToken cancellation = default)
    {
        CheckPlayerCall(token);
        PlayerUpdates.Add(request);
        var reply = new UpdatePlayerReply { Result = PlayerEditResult };
        reply.Errors.AddRange(PlayerEditErrors);
        if (Players.TryGetValue(request.NameKey, out var existing))
        {
            var current = existing.Clone();
            if (PlayerEditResult == AdminResult.Ok)
            {
                current.ShortDescription = request.ShortDescription;
                current.LongDescription = request.LongDescription;
                current.Race = request.Race;
                current.Gender = request.Gender;
                current.Class = request.Class;
                if (request.HasRetired) { current.Retired = request.Retired; }
                if (request.HasRole) { current.Role = request.Role; }
                Players[request.NameKey] = current;
            }
            reply.Player = current;
        }
        return Task.FromResult(reply);
    }

    // Modules. The fake keeps the modules it is given; start and stop record the call and answer with the reply a test sets.
    public Dictionary<string, ModuleInfo> Modules { get; } = new();
    public bool RefuseModuleCalls { get; set; }
    public AdminResult ModuleActionResult { get; set; } = AdminResult.Ok;
    public string ModuleActionMessage { get; set; } = string.Empty;
    public List<string> ModuleStarts { get; } = new();
    public List<string> ModuleStops { get; } = new();
    public List<string> ModuleRestarts { get; } = new();
    public List<string> ModuleTokens { get; } = new();

    public FakeAdminClient AddModule(string name, ModuleRunState state = ModuleRunState.Stopped, bool required = false, string version = "1.0", string lastFailure = "")
    {
        Modules[name] = new ModuleInfo
        {
            Name = name, Version = version, State = state, Required = required, LastFailure = lastFailure,
            Running = state == ModuleRunState.Connected || state == ModuleRunState.Ready,
        };
        return this;
    }

    private void CheckModuleCall(string token)
    {
        if (Unavailable) { throw new CoreUnavailableException(); }
        ModuleTokens.Add(token);
        if (RefuseModuleCalls) { throw new CoreRefusedException(RefuseBecauseSessionEnded); }
    }

    public List<(string Kind, string Key)> GenerationCalls { get; } = new();
    public GenerationReply GenerationResult { get; set; } = new GenerationReply { Result = AdminResult.Ok, UpdatedCount = 1 };
    public List<TerrainColor> TerrainColors { get; } = new();
    public bool ColorsUnavailable { get; set; }
    public int ColorCalls { get; set; }

    public Task<GenerationReply> RegenerateRoomsAsync(string token, string key, CancellationToken cancellation = default)
    {
        CheckRoomCall(token);
        GenerationCalls.Add(("regen", key));
        return Task.FromResult(GenerationResult);
    }

    public Task<GenerationReply> UpdateRoomsAsync(string token, string key, CancellationToken cancellation = default)
    {
        CheckRoomCall(token);
        GenerationCalls.Add(("update", key));
        return Task.FromResult(GenerationResult);
    }

    public Task<IReadOnlyList<TerrainColor>> ListTerrainColorsAsync(string token, CancellationToken cancellation = default)
    {
        ColorCalls++;
        if (ColorsUnavailable) { throw new CoreUnavailableException(); }
        return Task.FromResult<IReadOnlyList<TerrainColor>>(TerrainColors.ToList());
    }

    public Task<IReadOnlyList<ModuleInfo>> ListModulesAsync(string token, CancellationToken cancellation = default)
    {
        CheckModuleCall(token);
        return Task.FromResult<IReadOnlyList<ModuleInfo>>(Modules.Values.OrderBy(m => m.Name, StringComparer.Ordinal).ToList());
    }

    public Task<ModuleActionReply> StartModuleAsync(string token, string name, CancellationToken cancellation = default)
    {
        CheckModuleCall(token);
        ModuleStarts.Add(name);
        return Task.FromResult(ModuleReply(name));
    }

    public Task<ModuleActionReply> StopModuleAsync(string token, string name, CancellationToken cancellation = default)
    {
        CheckModuleCall(token);
        ModuleStops.Add(name);
        return Task.FromResult(ModuleReply(name));
    }

    public Task<ModuleActionReply> RestartModuleAsync(string token, string name, CancellationToken cancellation = default)
    {
        CheckModuleCall(token);
        ModuleRestarts.Add(name);
        return Task.FromResult(ModuleReply(name));
    }

    private ModuleActionReply ModuleReply(string name)
    {
        var reply = new ModuleActionReply { Result = ModuleActionResult, Message = ModuleActionMessage };
        if (Modules.TryGetValue(name, out var module)) { reply.Module = module; }
        return reply;
    }

    // Areas. Tests seed AreaRecords and set AreaResult (what create/update/delete answer); every call records its token and name.
    public AdminResult AreaResult { get; set; } = AdminResult.Ok;
    public string AreaMessage { get; set; } = string.Empty;
    public List<FieldError> AreaErrors { get; } = new();
    public bool RefuseAreaCalls { get; set; }
    public List<string> AreaTokens { get; } = new();
    public List<string> AreaCalls { get; } = new();
    public Dictionary<string, AreaRecord> Areas { get; } = new();

    public FakeAdminClient AddArea(string id, string name = "", string description = "", int rooms = 0)
    {
        Areas[id] = new AreaRecord { Id = id, Name = string.IsNullOrEmpty(name) ? id : name, Description = description, RoomCount = rooms };
        return this;
    }

    private void CheckAreaCall(string token, string call)
    {
        if (Unavailable) { throw new CoreUnavailableException(); }
        if (Crash) { throw new InvalidOperationException("boom"); }
        AreaTokens.Add(token);
        AreaCalls.Add(call);
        if (RefuseAreaCalls) { throw new CoreRefusedException(false); }
    }

    private AreaEditReply AreaReply()
    {
        var reply = new AreaEditReply { Result = AreaResult, Message = AreaMessage };
        reply.Errors.AddRange(AreaErrors);
        return reply;
    }

    public Task<IReadOnlyList<AreaRecord>> ListAreasAsync(string token, CancellationToken cancellation = default)
    {
        CheckAreaCall(token, "ListAreas");
        return Task.FromResult<IReadOnlyList<AreaRecord>>(Areas.Values.OrderBy(a => a.Id, StringComparer.Ordinal).ToList());
    }

    public Task<AreaRecord?> GetAreaAsync(string token, string id, CancellationToken cancellation = default)
    {
        CheckAreaCall(token, "GetArea");
        return Task.FromResult(Areas.TryGetValue(id, out var a) ? a : null);
    }

    public Task<AreaEditReply> CreateAreaAsync(string token, AreaRecord area, CancellationToken cancellation = default)
    {
        CheckAreaCall(token, "CreateArea");
        var reply = AreaReply();
        if (reply.Result == AdminResult.Ok) { Areas[area.Id] = area; reply.Area = area; }
        return Task.FromResult(reply);
    }

    public Task<AreaEditReply> UpdateAreaAsync(string token, AreaRecord area, CancellationToken cancellation = default)
    {
        CheckAreaCall(token, "UpdateArea");
        var reply = AreaReply();
        if (reply.Result == AdminResult.Ok) { Areas[area.Id] = area; reply.Area = area; }
        return Task.FromResult(reply);
    }

    public Task<AreaEditReply> DeleteAreaAsync(string token, string id, CancellationToken cancellation = default)
    {
        CheckAreaCall(token, "DeleteArea:" + id);
        var reply = AreaReply();
        if (reply.Result == AdminResult.Ok) { Areas.Remove(id); }
        return Task.FromResult(reply);
    }

    // Item templates. Tests seed ItemTemplateRecords and set ItemTemplateResult (what create/update/delete answer); every call records its token and name.
    public AdminResult ItemTemplateResult { get; set; } = AdminResult.Ok;
    public string ItemTemplateMessage { get; set; } = string.Empty;
    public List<FieldError> ItemTemplateErrors { get; } = new();
    public bool RefuseItemTemplateCalls { get; set; }
    public List<string> ItemTemplateTokens { get; } = new();
    public List<string> ItemTemplateCalls { get; } = new();
    public List<ItemTemplateRecord> ItemTemplateSaves { get; } = new();
    public Dictionary<string, ItemTemplateRecord> ItemTemplates { get; } = new();

    public FakeAdminClient AddItemTemplate(string id, string shortText = "", string area = "")
    {
        var item = new ItemTemplateRecord { Id = id, Area = string.IsNullOrEmpty(area) ? id.Substring(0, id.IndexOf('.')) : area, Short = string.IsNullOrEmpty(shortText) ? id : shortText, Long = "Long " + id, Version = 1 };
        ItemTemplates[id] = item;
        return this;
    }

    // Any other failure (not core unavailable, not refused): what the presenters must turn into a generic error.
    public bool Crash { get; set; }

    private void CheckItemTemplateCall(string token, string call)
    {
        if (Unavailable) { throw new CoreUnavailableException(); }
        if (Crash) { throw new InvalidOperationException("boom"); }
        ItemTemplateTokens.Add(token);
        ItemTemplateCalls.Add(call);
        if (RefuseItemTemplateCalls) { throw new CoreRefusedException(false); }
    }

    private ItemTemplateEditReply ItemTemplateReply()
    {
        var reply = new ItemTemplateEditReply { Result = ItemTemplateResult, Message = ItemTemplateMessage };
        reply.Errors.AddRange(ItemTemplateErrors);
        return reply;
    }

    public Task<IReadOnlyList<ItemTemplateRecord>> ListItemTemplatesAsync(string token, string? area = null, CancellationToken cancellation = default)
    {
        CheckItemTemplateCall(token, "ListItemTemplates:" + area);
        return Task.FromResult<IReadOnlyList<ItemTemplateRecord>>(ItemTemplates.Values.Where(i => string.IsNullOrEmpty(area) || i.Area == area).OrderBy(i => i.Id, StringComparer.Ordinal).ToList());
    }

    public Task<ItemTemplateRecord?> GetItemTemplateAsync(string token, string id, CancellationToken cancellation = default)
    {
        CheckItemTemplateCall(token, "GetItemTemplate");
        return Task.FromResult(ItemTemplates.TryGetValue(id, out var i) ? i : null);
    }

    public Task<ItemTemplateEditReply> CreateItemTemplateAsync(string token, ItemTemplateRecord template, CancellationToken cancellation = default)
    {
        CheckItemTemplateCall(token, "CreateItemTemplate");
        ItemTemplateSaves.Add(template);
        var reply = ItemTemplateReply();
        if (reply.Result == AdminResult.Ok) { template.Version = 1; ItemTemplates[template.Id] = template; reply.Template = template; }
        return Task.FromResult(reply);
    }

    public Task<ItemTemplateEditReply> UpdateItemTemplateAsync(string token, ItemTemplateRecord template, CancellationToken cancellation = default)
    {
        CheckItemTemplateCall(token, "UpdateItemTemplate");
        ItemTemplateSaves.Add(template);
        var reply = ItemTemplateReply();
        if (reply.Result == AdminResult.Ok) { template.Version = ItemTemplates.TryGetValue(template.Id, out var old) ? old.Version + 1 : 1; ItemTemplates[template.Id] = template; reply.Template = template; }
        return Task.FromResult(reply);
    }

    public Task<ItemTemplateEditReply> DeleteItemTemplateAsync(string token, string id, CancellationToken cancellation = default)
    {
        CheckItemTemplateCall(token, "DeleteItemTemplate:" + id);
        var reply = ItemTemplateReply();
        if (reply.Result == AdminResult.Ok) { ItemTemplates.Remove(id); }
        return Task.FromResult(reply);
    }

    // Item template references: tests set what the reply holds.
    public ListItemTemplateReferencesReply ItemTemplateReferences { get; set; } = new();
    // Only the references call fails (any exception), so a page can be shown with the rest of its data.
    public bool CrashOnReferences { get; set; }

    public Task<ListItemTemplateReferencesReply> ListItemTemplateReferencesAsync(string token, string id, CancellationToken cancellation = default)
    {
        CheckItemTemplateCall(token, "ListItemTemplateReferences:" + id);
        if (CrashOnReferences) { throw new InvalidOperationException("boom"); }
        return Task.FromResult(ItemTemplateReferences);
    }

    // NPC templates. Same shape as the item templates above: seed NpcTemplates (AddNpcTemplate), set NpcTemplateResult for create/update/delete.
    public AdminResult NpcTemplateResult { get; set; } = AdminResult.Ok;
    public string NpcTemplateMessage { get; set; } = string.Empty;
    public List<FieldError> NpcTemplateErrors { get; } = new();
    public bool RefuseNpcTemplateCalls { get; set; }
    public List<string> NpcTemplateTokens { get; } = new();
    public List<string> NpcTemplateCalls { get; } = new();
    public List<NpcTemplateRecord> NpcTemplateSaves { get; } = new();
    public Dictionary<string, NpcTemplateRecord> NpcTemplates { get; } = new();

    public FakeAdminClient AddNpcTemplate(string id, string shortText = "", string area = "")
    {
        NpcTemplates[id] = new NpcTemplateRecord { Id = id, Area = string.IsNullOrEmpty(area) ? id.Substring(0, id.IndexOf('.')) : area, Short = string.IsNullOrEmpty(shortText) ? id : shortText, Long = "Long " + id, Version = 1 };
        return this;
    }

    private void CheckNpcTemplateCall(string token, string call)
    {
        if (Unavailable) { throw new CoreUnavailableException(); }
        if (Crash) { throw new InvalidOperationException("boom"); }
        NpcTemplateTokens.Add(token);
        NpcTemplateCalls.Add(call);
        if (RefuseNpcTemplateCalls) { throw new CoreRefusedException(false); }
    }

    private NpcTemplateEditReply NpcTemplateReply()
    {
        var reply = new NpcTemplateEditReply { Result = NpcTemplateResult, Message = NpcTemplateMessage };
        reply.Errors.AddRange(NpcTemplateErrors);
        return reply;
    }

    public Task<IReadOnlyList<NpcTemplateRecord>> ListNpcTemplatesAsync(string token, string? area = null, CancellationToken cancellation = default)
    {
        CheckNpcTemplateCall(token, "ListNpcTemplates:" + area);
        return Task.FromResult<IReadOnlyList<NpcTemplateRecord>>(NpcTemplates.Values.Where(i => string.IsNullOrEmpty(area) || i.Area == area).OrderBy(i => i.Id, StringComparer.Ordinal).ToList());
    }

    public Task<NpcTemplateRecord?> GetNpcTemplateAsync(string token, string id, CancellationToken cancellation = default)
    {
        CheckNpcTemplateCall(token, "GetNpcTemplate");
        return Task.FromResult(NpcTemplates.TryGetValue(id, out var i) ? i : null);
    }

    public Task<NpcTemplateEditReply> CreateNpcTemplateAsync(string token, NpcTemplateRecord template, CancellationToken cancellation = default)
    {
        CheckNpcTemplateCall(token, "CreateNpcTemplate");
        NpcTemplateSaves.Add(template);
        var reply = NpcTemplateReply();
        if (reply.Result == AdminResult.Ok) { template.Version = 1; NpcTemplates[template.Id] = template; reply.Template = template; }
        return Task.FromResult(reply);
    }

    public Task<NpcTemplateEditReply> UpdateNpcTemplateAsync(string token, NpcTemplateRecord template, CancellationToken cancellation = default)
    {
        CheckNpcTemplateCall(token, "UpdateNpcTemplate");
        NpcTemplateSaves.Add(template);
        var reply = NpcTemplateReply();
        if (reply.Result == AdminResult.Ok) { template.Version = NpcTemplates.TryGetValue(template.Id, out var old) ? old.Version + 1 : 1; NpcTemplates[template.Id] = template; reply.Template = template; }
        return Task.FromResult(reply);
    }

    public Task<NpcTemplateEditReply> DeleteNpcTemplateAsync(string token, string id, CancellationToken cancellation = default)
    {
        CheckNpcTemplateCall(token, "DeleteNpcTemplate:" + id);
        var reply = NpcTemplateReply();
        if (reply.Result == AdminResult.Ok) { NpcTemplates.Remove(id); }
        return Task.FromResult(reply);
    }

    // Terrain types. Tests seed TerrainTypes (AddTerrainType) and set TerrainTypeResult (what create/update/delete answer); every call records its token.
    public AdminResult TerrainTypeResult { get; set; } = AdminResult.Ok;
    public string TerrainTypeMessage { get; set; } = string.Empty;
    public List<FieldError> TerrainTypeErrors { get; } = new();
    public bool RefuseTerrainTypeCalls { get; set; }
    public List<string> TerrainTypeTokens { get; } = new();
    public List<string> TerrainTypeCalls { get; } = new();
    public Dictionary<string, TerrainTypeRecord> TerrainTypes { get; } = new();

    public FakeAdminClient AddTerrainType(string id, string name = "", string description = "")
    {
        TerrainTypes[id] = new TerrainTypeRecord { Id = id, Area = id.Substring(0, id.LastIndexOf('.')), Name = string.IsNullOrEmpty(name) ? id : name, Description = description };
        return this;
    }

    // The optional terrain and area filters of the library list calls (the area of a terrain id is the part before the last '.').
    private static bool ScopeMatches(string entityTerrain, string? terrain, string? area) =>
        (string.IsNullOrEmpty(terrain) || entityTerrain == terrain)
        && (string.IsNullOrEmpty(area) || (entityTerrain.Contains('.') && entityTerrain.Substring(0, entityTerrain.LastIndexOf('.')) == area));

    private void CheckTerrainTypeCall(string token, string call)
    {
        if (Unavailable) { throw new CoreUnavailableException(); }
        TerrainTypeTokens.Add(token);
        TerrainTypeCalls.Add(call);
        if (RefuseTerrainTypeCalls) { throw new CoreRefusedException(false); }
    }

    private TerrainTypeEditReply TerrainTypeReply()
    {
        var reply = new TerrainTypeEditReply { Result = TerrainTypeResult, Message = TerrainTypeMessage };
        reply.Errors.AddRange(TerrainTypeErrors);
        return reply;
    }

    public Task<IReadOnlyList<TerrainTypeRecord>> ListTerrainTypesAsync(string token, string? area = null, CancellationToken cancellation = default)
    {
        CheckTerrainTypeCall(token, "ListTerrainTypes");
        return Task.FromResult<IReadOnlyList<TerrainTypeRecord>>(TerrainTypes.Values.Where(t => string.IsNullOrEmpty(area) || t.Area == area).OrderBy(t => t.Id, StringComparer.Ordinal).ToList());
    }

    public Task<TerrainTypeRecord?> GetTerrainTypeAsync(string token, string id, CancellationToken cancellation = default)
    {
        CheckTerrainTypeCall(token, "GetTerrainType");
        return Task.FromResult(TerrainTypes.TryGetValue(id, out var t) ? t : null);
    }

    public Task<TerrainTypeEditReply> CreateTerrainTypeAsync(string token, TerrainTypeRecord terrainType, CancellationToken cancellation = default)
    {
        CheckTerrainTypeCall(token, "CreateTerrainType");
        var reply = TerrainTypeReply();
        if (reply.Result == AdminResult.Ok) { terrainType.Area = terrainType.Id.Substring(0, terrainType.Id.LastIndexOf('.')); TerrainTypes[terrainType.Id] = terrainType; reply.TerrainType = terrainType; }
        return Task.FromResult(reply);
    }

    public Task<TerrainTypeEditReply> UpdateTerrainTypeAsync(string token, TerrainTypeRecord terrainType, CancellationToken cancellation = default)
    {
        CheckTerrainTypeCall(token, "UpdateTerrainType");
        var reply = TerrainTypeReply();
        if (reply.Result == AdminResult.Ok) { TerrainTypes[terrainType.Id] = terrainType; reply.TerrainType = terrainType; }
        return Task.FromResult(reply);
    }

    public Task<TerrainTypeEditReply> DeleteTerrainTypeAsync(string token, string id, CancellationToken cancellation = default)
    {
        CheckTerrainTypeCall(token, "DeleteTerrainType:" + id);
        var reply = TerrainTypeReply();
        if (reply.Result == AdminResult.Ok) { TerrainTypes.Remove(id); }
        return Task.FromResult(reply);
    }

    // Terrain library. Tests set LibraryResult (what the create/update/delete calls answer) and seed the stores; every call records its token.
    public AdminResult LibraryResult { get; set; } = AdminResult.Ok;
    public string LibraryMessage { get; set; } = string.Empty;
    public bool RefuseLibraryCalls { get; set; }
    public bool RefuseBecauseSessionEndedForLibrary { get; set; }
    public List<string> LibraryTokens { get; } = new();
    public List<string> LibraryCalls { get; } = new();
    public Dictionary<string, FragmentRecord> Fragments { get; } = new();
    public Dictionary<string, FeatureTemplateRecord> FeatureTemplates { get; } = new();
    public Dictionary<string, NeighborPhraseRecord> NeighborPhrases { get; } = new();

    private void CheckLibraryCall(string token, string call)
    {
        if (Unavailable) { throw new CoreUnavailableException(); }
        LibraryTokens.Add(token);
        LibraryCalls.Add(call);
        if (RefuseLibraryCalls) { throw new CoreRefusedException(RefuseBecauseSessionEndedForLibrary); }
    }

    private LibraryEditReply LibraryReply() => new() { Result = LibraryResult, Message = LibraryMessage };

    public Task<IReadOnlyList<FragmentRecord>> ListFragmentsAsync(string token, string? terrain = null, string? area = null, CancellationToken cancellation = default)
    {
        CheckLibraryCall(token, "ListFragments");
        return Task.FromResult<IReadOnlyList<FragmentRecord>>(Fragments.Values.Where(f => ScopeMatches(f.Terrain, terrain, area)).OrderBy(f => f.Id, StringComparer.Ordinal).ToList());
    }

    public Task<FragmentRecord?> GetFragmentAsync(string token, string id, CancellationToken cancellation = default)
    {
        CheckLibraryCall(token, "GetFragment");
        return Task.FromResult(Fragments.TryGetValue(id, out var f) ? f : null);
    }

    public Task<LibraryEditReply> CreateFragmentAsync(string token, FragmentRecord fragment, CancellationToken cancellation = default)
    {
        CheckLibraryCall(token, "CreateFragment");
        var reply = LibraryReply();
        if (reply.Result == AdminResult.Ok) { Fragments[fragment.Id] = fragment; reply.Fragment = fragment; }
        return Task.FromResult(reply);
    }

    public Task<LibraryEditReply> UpdateFragmentAsync(string token, FragmentRecord fragment, CancellationToken cancellation = default)
    {
        CheckLibraryCall(token, "UpdateFragment");
        var reply = LibraryReply();
        if (reply.Result == AdminResult.Ok) { Fragments[fragment.Id] = fragment; reply.Fragment = fragment; }
        return Task.FromResult(reply);
    }

    public Task<LibraryEditReply> DeleteFragmentAsync(string token, string id, CancellationToken cancellation = default)
    {
        CheckLibraryCall(token, "DeleteFragment");
        var reply = LibraryReply();
        if (reply.Result == AdminResult.Ok) { Fragments.Remove(id); }
        return Task.FromResult(reply);
    }

    public Task<IReadOnlyList<FeatureTemplateRecord>> ListFeatureTemplatesAsync(string token, string? terrain = null, string? area = null, CancellationToken cancellation = default)
    {
        CheckLibraryCall(token, "ListFeatureTemplates");
        return Task.FromResult<IReadOnlyList<FeatureTemplateRecord>>(FeatureTemplates.Values.Where(f => ScopeMatches(f.Terrain, terrain, area)).OrderBy(f => f.Id, StringComparer.Ordinal).ToList());
    }

    public Task<FeatureTemplateRecord?> GetFeatureTemplateAsync(string token, string id, CancellationToken cancellation = default)
    {
        CheckLibraryCall(token, "GetFeatureTemplate");
        return Task.FromResult(FeatureTemplates.TryGetValue(id, out var f) ? f : null);
    }

    public Task<LibraryEditReply> CreateFeatureTemplateAsync(string token, FeatureTemplateRecord template, CancellationToken cancellation = default)
    {
        CheckLibraryCall(token, "CreateFeatureTemplate");
        var reply = LibraryReply();
        if (reply.Result == AdminResult.Ok) { template.Version = 1; FeatureTemplates[template.Id] = template; reply.Template = template; }
        return Task.FromResult(reply);
    }

    public Task<LibraryEditReply> UpdateFeatureTemplateAsync(string token, FeatureTemplateRecord template, CancellationToken cancellation = default)
    {
        CheckLibraryCall(token, "UpdateFeatureTemplate");
        var reply = LibraryReply();
        if (reply.Result == AdminResult.Ok)
        {
            // Like core: the client-sent version is ignored; a text change bumps the stored one.
            var stored = FeatureTemplates.TryGetValue(template.Id, out var s) ? s : null;
            template.Version = stored == null ? 1 : (stored.Short != template.Short || stored.Long != template.Long ? stored.Version + 1 : stored.Version);
            FeatureTemplates[template.Id] = template;
            reply.Template = template;
        }
        return Task.FromResult(reply);
    }

    public Task<LibraryEditReply> DeleteFeatureTemplateAsync(string token, string id, CancellationToken cancellation = default)
    {
        CheckLibraryCall(token, "DeleteFeatureTemplate");
        var reply = LibraryReply();
        if (reply.Result == AdminResult.Ok) { FeatureTemplates.Remove(id); }
        return Task.FromResult(reply);
    }

    public Task<IReadOnlyList<NeighborPhraseRecord>> ListNeighborPhrasesAsync(string token, string? terrain = null, string? area = null, CancellationToken cancellation = default)
    {
        CheckLibraryCall(token, "ListNeighborPhrases");
        return Task.FromResult<IReadOnlyList<NeighborPhraseRecord>>(NeighborPhrases.Values.Where(f => ScopeMatches(f.Terrain, terrain, area)).OrderBy(f => f.Id, StringComparer.Ordinal).ToList());
    }

    public Task<NeighborPhraseRecord?> GetNeighborPhraseAsync(string token, string id, CancellationToken cancellation = default)
    {
        CheckLibraryCall(token, "GetNeighborPhrase");
        return Task.FromResult(NeighborPhrases.TryGetValue(id, out var f) ? f : null);
    }

    public Task<LibraryEditReply> CreateNeighborPhraseAsync(string token, NeighborPhraseRecord phrase, CancellationToken cancellation = default)
    {
        CheckLibraryCall(token, "CreateNeighborPhrase");
        var reply = LibraryReply();
        if (reply.Result == AdminResult.Ok) { NeighborPhrases[phrase.Id] = phrase; reply.Phrase = phrase; }
        return Task.FromResult(reply);
    }

    public Task<LibraryEditReply> UpdateNeighborPhraseAsync(string token, NeighborPhraseRecord phrase, CancellationToken cancellation = default)
    {
        CheckLibraryCall(token, "UpdateNeighborPhrase");
        var reply = LibraryReply();
        if (reply.Result == AdminResult.Ok) { NeighborPhrases[phrase.Id] = phrase; reply.Phrase = phrase; }
        return Task.FromResult(reply);
    }

    public Task<LibraryEditReply> DeleteNeighborPhraseAsync(string token, string id, CancellationToken cancellation = default)
    {
        CheckLibraryCall(token, "DeleteNeighborPhrase");
        var reply = LibraryReply();
        if (reply.Result == AdminResult.Ok) { NeighborPhrases.Remove(id); }
        return Task.FromResult(reply);
    }
}

public class SessionAuthenticationHandlerTests
{
    private static async Task<(AuthenticateResult Result, HttpContext Http)> Run(FakeAdminClient core, string? token)
    {
        var http = new DefaultHttpContext();
        if (token != null) { http.Request.Headers.Cookie = SessionAuthentication.CookieName + "=" + token; }
        var handler = new SessionAuthenticationHandler(new StaticOptions(), NullLoggerFactory.Instance, UrlEncoder.Default, core);
        await handler.InitializeAsync(new AuthenticationScheme(SessionAuthentication.Scheme, null, typeof(SessionAuthenticationHandler)), http);
        return (await handler.AuthenticateAsync(), http);
    }

    private sealed class StaticOptions : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue { get; } = new();
        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }

    private static FakeAdminClient WithSession(AdminRole role)
    {
        var core = new FakeAdminClient();
        core.Sessions["token-1"] = new SessionInfoReply { PlayerName = "Ann", Role = role };
        return core;
    }

    [Theory]
    [InlineData(AdminRole.Player, "Player")]
    [InlineData(AdminRole.Creator, "Creator")]
    [InlineData(AdminRole.Admin, "Admin")]
    public async Task AKnownToken_BecomesTheSignedInUser_WithTheRoleCoreReports(AdminRole role, string expected)
    {
        var (result, _) = await Run(WithSession(role), "token-1");
        Assert.True(result.Succeeded);
        Assert.Equal("Ann", result.Principal!.FindFirstValue(ClaimTypes.Name));
        Assert.Equal(expected, result.Principal.FindFirstValue(ClaimTypes.Role));
    }

    [Fact]
    public async Task TheRole_IsReadFromCoreOnEveryRequest_SoADemotionAppliesAtOnce()
    {
        var core = WithSession(AdminRole.Admin);
        Assert.Equal("Admin", (await Run(core, "token-1")).Result.Principal!.FindFirstValue(ClaimTypes.Role));
        core.Sessions["token-1"] = new SessionInfoReply { PlayerName = "Ann", Role = AdminRole.Player };
        Assert.Equal("Player", (await Run(core, "token-1")).Result.Principal!.FindFirstValue(ClaimTypes.Role));
    }

    [Fact]
    public async Task NoCookie_IsAnonymous()
    {
        var (result, _) = await Run(WithSession(AdminRole.Admin), null);
        Assert.False(result.Succeeded);
        Assert.True(result.None);
    }

    [Fact]
    public async Task ATokenCoreNoLongerKnows_IsAnonymous_AndTheStaleCookieIsRemoved()
    {
        var core = WithSession(AdminRole.Admin);
        core.Sessions.Clear(); // revoked: retire, demote, logout elsewhere or a core restart
        var (result, http) = await Run(core, "token-1");
        Assert.False(result.Succeeded);
        Assert.Contains(SessionAuthentication.CookieName + "=;", http.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public async Task WhenCoreCannotBeAsked_TheRequestIsAnonymous_ButTheCookieIsKept()
    {
        var core = WithSession(AdminRole.Admin);
        core.Unavailable = true;
        var (result, http) = await Run(core, "token-1");
        Assert.False(result.Succeeded);
        Assert.False(http.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public async Task AGuessedTokenIsNotAUser()
    {
        var (result, _) = await Run(WithSession(AdminRole.Admin), "not-a-real-token");
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task PolicyCreatorOrAdmin_FollowsTheRoleFromCore()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(WebPolicies.Configure);
        var authorization = services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
        var creator = (await Run(WithSession(AdminRole.Creator), "token-1")).Result.Principal!;
        var player = (await Run(WithSession(AdminRole.Player), "token-1")).Result.Principal!;
        Assert.True((await authorization.AuthorizeAsync(creator, WebPolicies.CreatorOrAdmin)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(player, WebPolicies.CreatorOrAdmin)).Succeeded);
    }
}

public class SessionAccountServiceTests
{
    private static SessionAccountService For(string? name, string? role)
    {
        var http = new DefaultHttpContext();
        if (name != null)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.Role, role!) }, "test"));
        }
        return new SessionAccountService(new HttpContextAccessor { HttpContext = http });
    }

    [Fact]
    public void TheSignedInPlayer_IsFoundByNameCaseInsensitively_WithTheRoleFromTheSession()
    {
        var account = For("Ann", "Creator").GetByName("  ANN ");
        Assert.NotNull(account);
        Assert.Equal("Ann", account!.PlayerName);
        Assert.Equal(AccountRole.Creator, account.Role);
        Assert.False(account.Retired);
    }

    [Fact]
    public void ADifferentName_IsNotFound() => Assert.Null(For("Ann", "Admin").GetByName("Bob"));

    [Fact]
    public void Anonymous_IsNotFound() => Assert.Null(For(null, null).GetByName("Ann"));

    [Fact]
    public void ARolelessOrUnknownRoleClaim_IsNotFound() => Assert.Null(For("Ann", "Wizard").GetByName("Ann"));

    [Fact]
    public void RoleMapping_CoversEveryContractRole()
    {
        Assert.Equal(AccountRole.Admin, SessionAuthentication.RoleFrom(AdminRole.Admin));
        Assert.Equal(AccountRole.Creator, SessionAuthentication.RoleFrom(AdminRole.Creator));
        Assert.Equal(AccountRole.Player, SessionAuthentication.RoleFrom(AdminRole.Player));
        Assert.Equal(AccountRole.Player, SessionAuthentication.RoleFrom(AdminRole.Unspecified));
    }
}

public class LoginThrottleTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void BlocksAfterMaxFailuresThenExpires()
    {
        var clock = new Clock();
        var t = new LoginThrottle(clock);
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            Assert.False(t.IsBlocked("1.1.1.1"));
            t.RecordFailure("1.1.1.1");
        }
        Assert.True(t.IsBlocked("1.1.1.1"));
        Assert.False(t.IsBlocked("2.2.2.2"));
        clock.Now += LoginThrottle.Window + TimeSpan.FromSeconds(1);
        Assert.False(t.IsBlocked("1.1.1.1"));
    }

    [Fact]
    public void ResetClears()
    {
        var t = new LoginThrottle();
        for (var i = 0; i < LoginThrottle.MaxFailures; i++) t.RecordFailure("x");
        t.Reset("x");
        Assert.False(t.IsBlocked("x"));
    }
}

public class CoreSocketTests
{
    [Fact]
    public void TheSocketIsFrontendsSock_InsideTheRunDirectory()
    {
        var saved = Environment.GetEnvironmentVariable(CoreSocket.RunDirectoryVariable);
        try
        {
            Environment.SetEnvironmentVariable(CoreSocket.RunDirectoryVariable, "/tmp/mud-run-test");
            Assert.Equal("/tmp/mud-run-test/frontends.sock", CoreSocket.ResolvePath());
        }
        finally { Environment.SetEnvironmentVariable(CoreSocket.RunDirectoryVariable, saved); }
    }
}

public class NoMongoForAccountsTests
{
    [Fact]
    public void TheAccountTypes_NoLongerReferenceMongo()
    {
        var web = typeof(IAccountService).Assembly;
        Assert.Null(web.GetType("MUD.Web.Services.MongoAccountStore"));
        Assert.Null(web.GetType("MUD.Web.Services.IAccountStore"));
        Assert.Null(web.GetType("MUD.Web.Services.AccountService"));
    }
}

// The real client over a real Unix socket against a small stand-in for core's Admin service (the real service is tested in MUD.Tests).
public class GrpcAdminClientSocketTests : IDisposable
{
    private sealed class StandInAdmin : Admin.AdminBase
    {
        public override Task<WebAuthReply> WebAuthenticate(WebAuthRequest request, Grpc.Core.ServerCallContext context) =>
            Task.FromResult(request.Password == "right"
                ? new WebAuthReply { Result = AuthResult.AuthOk, Token = "tok", PlayerName = request.Name, Role = AdminRole.Creator }
                : new WebAuthReply { Result = AuthResult.AuthBadCredentials });

        public override Task<SessionInfoReply> GetSession(TokenRequest request, Grpc.Core.ServerCallContext context) =>
            request.Token == "tok"
                ? Task.FromResult(new SessionInfoReply { PlayerName = "Ann", Role = AdminRole.Creator })
                : throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unauthenticated, "no"));

        public override Task<Ack> Logout(TokenRequest request, Grpc.Core.ServerCallContext context) => Task.FromResult(new Ack());

        public override Task<ChangePasswordReply> ChangePassword(ChangePasswordRequest request, Grpc.Core.ServerCallContext context) =>
            Task.FromResult(new ChangePasswordReply { Outcome = PasswordChangeOutcome.Changed });

        private static void RequireToken(string token)
        {
            if (token == "stale") { throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unauthenticated, "no")); }
            if (token == "low") { throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.PermissionDenied, "no")); }
            if (token == "busy") { throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "no")); }
        }

        public override Task<ListRoomsReply> ListRooms(TokenRequest request, Grpc.Core.ServerCallContext context)
        {
            RequireToken(request.Token);
            var reply = new ListRoomsReply();
            reply.Rooms.Add(new RoomDetail { Key = "t.hall", Area = "t", X = 0 });
            return Task.FromResult(reply);
        }

        public override Task<GetRoomReply> GetRoom(GetRoomRequest request, Grpc.Core.ServerCallContext context)
        {
            RequireToken(request.Token);
            return Task.FromResult(request.Key == "t.hall"
                ? new GetRoomReply { Found = true, Room = HallWithBehaviors() }
                : new GetRoomReply { Found = false });
        }

        private static RoomDetail HallWithBehaviors()
        {
            var hall = new RoomDetail { Key = "t.hall", Area = "t" };
            hall.Behaviors.Add(new RoomBehavior { Hook = "room.entered", Module = "m", Type = "T", Priority = 2, ConfigJson = "{\"a\":1}" });
            hall.Searches.Add(new RoomSearchEntry { Id = "table", Target = "table", RefreshMs = 500, RewardShort = "a coin" });
            return hall;
        }

        public override Task<RoomEditReply> CreateRoom(RoomEditRequest request, Grpc.Core.ServerCallContext context)
        {
            RequireToken(request.Token);
            return Task.FromResult(new RoomEditReply { Result = AdminResult.Ok, Room = new RoomDetail { Key = request.Key } });
        }

        public override Task<RoomEditReply> UpdateRoom(RoomEditRequest request, Grpc.Core.ServerCallContext context)
        {
            RequireToken(request.Token);
            return Task.FromResult(new RoomEditReply { Result = AdminResult.NoChange });
        }

        public override Task<RoomEditReply> DeleteRoom(DeleteRoomRequest request, Grpc.Core.ServerCallContext context)
        {
            RequireToken(request.Token);
            return Task.FromResult(new RoomEditReply { Result = AdminResult.Refused, Message = "fixed" });
        }
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wc" + Guid.NewGuid().ToString("N").Substring(0, 8));
    private WebApplication? _app;

    private async Task<GrpcAdminClient> Start()
    {
        Directory.CreateDirectory(_directory);
        var socket = Path.Combine(_directory, "f.sock");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.ListenUnixSocket(socket, o => o.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2));
        builder.Services.AddGrpc();
        _app = builder.Build();
        _app.MapGrpcService<StandInAdmin>();
        await _app.StartAsync();
        return new GrpcAdminClient(new Admin.AdminClient(CoreSocket.OpenChannel(socket)));
    }

    public void Dispose()
    {
        _app?.StopAsync().GetAwaiter().GetResult();
        try { Directory.Delete(_directory, true); } catch (Exception) { }
    }

    [Fact]
    public async Task SignIn_SessionLookup_AndLogout_WorkOverTheSocket()
    {
        var client = await Start();
        var reply = await client.WebAuthenticateAsync("Ann", "right", "1.2.3.4");
        Assert.Equal(AuthResult.AuthOk, reply.Result);
        Assert.Equal("tok", reply.Token);
        Assert.Equal(AdminRole.Creator, (await client.GetSessionAsync("tok"))!.Role);
        Assert.Equal(AuthResult.AuthBadCredentials, (await client.WebAuthenticateAsync("Ann", "wrong", "1.2.3.4")).Result);
        await client.LogoutAsync("tok");
        Assert.Equal(PasswordChangeOutcome.Changed, (await client.ChangePasswordAsync("tok", "a", "b", "b")).Outcome);
    }

    [Fact]
    public async Task RoomCalls_WorkOverTheSocket_AndCarryTheTokenAndKey()
    {
        var client = await Start();
        var rooms = await client.ListRoomsAsync("tok");
        Assert.Equal("t.hall", Assert.Single(rooms).Key);
        var hall = (await client.GetRoomAsync("tok", "t.hall"))!;
        Assert.Equal("t.hall", hall.Key);
        Assert.Equal(("room.entered", "m", "T", 2, "{\"a\":1}"), (hall.Behaviors[0].Hook, hall.Behaviors[0].Module, hall.Behaviors[0].Type, hall.Behaviors[0].Priority, hall.Behaviors[0].ConfigJson));
        Assert.Equal(("table", "table", 500, "a coin"), (hall.Searches[0].Id, hall.Searches[0].Target, hall.Searches[0].RefreshMs, hall.Searches[0].RewardShort));
        Assert.Null(await client.GetRoomAsync("tok", "t.nope"));
        var created = await client.CreateRoomAsync("tok", new RoomEditRequest { Key = "t.new" });
        Assert.Equal((AdminResult.Ok, "t.new"), (created.Result, created.Room.Key));
        Assert.Equal(AdminResult.NoChange, (await client.UpdateRoomAsync("tok", new RoomEditRequest { Key = "t.hall" })).Result);
        var deleted = await client.DeleteRoomAsync("tok", "t.hall");
        Assert.Equal((AdminResult.Refused, "fixed"), (deleted.Result, deleted.Message));
    }

    [Fact]
    public async Task RoomCalls_MapCoresStatusesOntoTheTwoExceptions()
    {
        var client = await Start();
        var ended = await Assert.ThrowsAsync<CoreRefusedException>(() => client.ListRoomsAsync("stale"));
        Assert.True(ended.SessionEnded);
        var tooLow = await Assert.ThrowsAsync<CoreRefusedException>(() => client.GetRoomAsync("low", "t.hall"));
        Assert.False(tooLow.SessionEnded);
        await Assert.ThrowsAsync<CoreUnavailableException>(() => client.CreateRoomAsync("busy", new RoomEditRequest { Key = "t.x" }));
        await Assert.ThrowsAsync<CoreUnavailableException>(() => client.DeleteRoomAsync("busy", "t.x"));
    }

    [Fact]
    public async Task RoomCalls_WhenNothingListens_ReportCoreUnavailable()
    {
        Directory.CreateDirectory(_directory);
        var client = new GrpcAdminClient(new Admin.AdminClient(CoreSocket.OpenChannel(Path.Combine(_directory, "none.sock"))));
        await Assert.ThrowsAsync<CoreUnavailableException>(() => client.ListRoomsAsync("tok"));
    }

    [Fact]
    public async Task AnUnauthenticatedStatus_IsNoSession_NotAnError()
    {
        var client = await Start();
        Assert.Null(await client.GetSessionAsync("stale"));
        Assert.Null(await client.GetSessionAsync(""));
    }

    [Fact]
    public async Task WhenNothingListens_TheCallsReportCoreUnavailable_AndLogoutIsQuiet()
    {
        Directory.CreateDirectory(_directory);
        var client = new GrpcAdminClient(new Admin.AdminClient(CoreSocket.OpenChannel(Path.Combine(_directory, "none.sock"))));
        await Assert.ThrowsAsync<CoreUnavailableException>(() => client.WebAuthenticateAsync("Ann", "x", "k"));
        await Assert.ThrowsAsync<CoreUnavailableException>(() => client.GetSessionAsync("tok"));
        await client.LogoutAsync("tok");
    }
}
