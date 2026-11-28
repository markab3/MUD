using MUD.Web.Services;

namespace MUD.Web.Pages;

public enum MapOutcome { Ok, Unavailable, Denied }

// Read-only room map (moved from CreatorMapModel), laid out here from the rooms core lists (ListRooms). Nothing here writes anything.
public sealed class CreatorMapPresenter
{
    public const string PlainFill = "#f5f5f4";
    public const string GeneratedFallbackFill = "#d6d3d1";
    private static readonly System.Text.RegularExpressions.Regex ColorPattern = new("^#[0-9A-Fa-f]{6}$");

    private readonly IAdminClient _core;
    private readonly string _token;
    private readonly Dictionary<(string Map, string Terrain), string> _colors = new();

    public CreatorMapPresenter(IAdminClient core, string token)
    {
        _core = core;
        _token = token;
    }

    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _roomCounts = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Areas { get; private set; } = Array.Empty<string>();

    // "prefix - Name" for an area with a record, the bare prefix otherwise; "(no area)" for rooms without one.
    public string LabelFor(string area) =>
        area.Length == 0 ? "(no area)" : _names.TryGetValue(area, out var name) && !string.IsNullOrWhiteSpace(name) ? area + " - " + name : area;
    // Rooms of the area on every level, from its area record; null when the record list was not available.
    public int? RoomCountOf(string area) => _roomCounts.TryGetValue(area, out var n) ? n : null;
    public IReadOnlyList<int> Levels { get; private set; } = Array.Empty<int>();
    public string? Area { get; private set; }
    public int Z { get; private set; }
    public RoomMap? Map { get; private set; }
    public string? Error { get; private set; }

    // Clicking a line end or a gray link arrow: ONE exit is added to or removed from the room core has right now, through the audited
    // UpdateRoom. The whole form goes back so core diffs and audits only that exit. Never touches any other room. No confirmation, no undo.
    // Messages are fixed strings.
    public string? WireError { get; private set; }
    public string? WireNotice { get; private set; }

    // The page draws the map again with a fresh presenter after a write; this keeps the answer of the write on it.
    public void CarryWire(string? notice, string? error) { WireNotice = notice; WireError = error; }

    public async Task<bool> ApplyAsync(MapExitAction action, CancellationToken cancellation = default)
    {
        WireError = null;
        WireNotice = null;
        if (string.IsNullOrWhiteSpace(action.OwnerKey) || string.IsNullOrWhiteSpace(action.TargetKey) || string.IsNullOrWhiteSpace(action.Direction)
            || (!action.Remove && !RoomMapLayout.TryCompass(action.Direction, out _)))
        {
            WireError = Messages.MapExitChanged;
            return false;
        }
        var name = action.Direction;
        if (!action.Remove)
        {
            RoomMapLayout.TryCompass(action.Direction, out var v);
            name = RoomMapLayout.NameOf(v.Dx, v.Dy);
        }
        try
        {
            var room = await _core.GetRoomAsync(_token, action.OwnerKey, cancellation);
            if (room == null)
            {
                WireError = Messages.MapExitChanged;
                return false;
            }
            var exits = room.Exits.Select(e => new MUD.Contracts.RoomExit { Name = e.Name, To = e.To }).ToList();
            if (action.Remove)
            {
                // Exactly this exit: same name AND same target. Gone since the map was drawn: refuse, change nothing.
                var index = exits.FindIndex(e => string.Equals(e.Name, action.Direction, StringComparison.OrdinalIgnoreCase) && e.To == action.TargetKey);
                if (index < 0) { WireError = Messages.MapExitChanged; return false; }
                exits.RemoveAt(index);
            }
            else
            {
                // The direction got an exit (under any alias) since the map was drawn: never overwrite it.
                RoomMapLayout.TryCompass(name, out var want);
                if (exits.Any(e => RoomMapLayout.TryCompass(e.Name, out var v) && v.Dx == want.Dx && v.Dy == want.Dy))
                {
                    WireError = Messages.MapDirectionTaken;
                    return false;
                }
                exits.Add(new MUD.Contracts.RoomExit { Name = name, To = action.TargetKey });
            }
            var request = new MUD.Contracts.RoomEditRequest
            {
                Key = room.Key,
                ShortDescription = room.ShortDescription,
                LongDescription = room.LongDescription,
                Area = room.Area,
            };
            if (room.HasX) { request.X = room.X; }
            if (room.HasY) { request.Y = room.Y; }
            if (room.HasZ) { request.Z = room.Z; }
            request.Exits.AddRange(exits);
            request.FeaturesPresent = true;
            request.Features.AddRange(room.Features);
            var reply = await _core.UpdateRoomAsync(_token, request, cancellation);
            switch (reply.Result)
            {
                case MUD.Contracts.AdminResult.Ok:
                    WireNotice = (action.Remove ? "Removed exit " : "Added exit ") + name + " from " + action.OwnerKey + " to " + action.TargetKey;
                    return true;
                case MUD.Contracts.AdminResult.Invalid:
                    WireError = Messages.MapExitInvalid;
                    return false;
                case MUD.Contracts.AdminResult.NotFound:
                case MUD.Contracts.AdminResult.Refused:
                case MUD.Contracts.AdminResult.NoChange:
                    WireError = Messages.MapExitChanged;
                    return false;
                case MUD.Contracts.AdminResult.Forbidden:
                    WireError = Messages.RoomForbidden;
                    return false;
                case MUD.Contracts.AdminResult.AuditFailed:
                    WireError = Messages.RoomAuditFailed;
                    return false;
                default:
                    WireError = Messages.RoomSaveFailed;
                    return false;
            }
        }
        catch (CoreUnavailableException) { WireError = Messages.Unavailable; return false; }
        catch (CoreRefusedException) { WireError = Messages.RoomForbidden; return false; }
    }

    // gx/gy: a level switch started at that X,Y; when this level has no room there the map shows the dashed "No room here" square,
    // and the level is listed even if it has no rooms yet.
    public async Task<MapOutcome> LoadAsync(string? area, int? z, CancellationToken cancellation = default, int? gx = null, int? gy = null)
    {
        Error = null;
        Map = null;
        _colors.Clear();
        IReadOnlyList<RoomRecord> all;
        try
        {
            all = (await _core.ListRoomsAsync(_token, cancellation)).Select(RoomRecord.From).ToList();
        }
        catch (CoreUnavailableException)
        {
            Error = Messages.Unavailable;
            return MapOutcome.Unavailable;
        }
        catch (CoreRefusedException)
        {
            // The session ended or the role was lowered since the page was authorised (D31).
            return MapOutcome.Denied;
        }
        // The dropdown lists the area records (so an area with no rooms yet appears) plus any room area that has no record (legacy data).
        // When core cannot list the records the rooms alone give the list.
        _names.Clear();
        _roomCounts.Clear();
        var recordIds = new List<string>();
        try
        {
            foreach (var a in await _core.ListAreasAsync(_token, cancellation))
            {
                recordIds.Add(a.Id);
                _names[a.Id] = a.Name;
                _roomCounts[a.Id] = a.RoomCount;
            }
        }
        catch (CoreUnavailableException) { }
        catch (CoreRefusedException) { }
        Areas = all.Select(RoomMapLayout.AreaOf).Concat(recordIds).Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal).ToList();
        if (Areas.Count == 0) { return MapOutcome.Ok; }

        Area = area != null && Areas.Contains(area) ? area : Areas[0];
        Levels = all.Where(r => RoomMapLayout.AreaOf(r) == Area && RoomMapLayout.IsPlaced(r))
            .Select(RoomMapLayout.LevelOf).Distinct().OrderBy(l => l).ToList();
        Z = z.HasValue ? z.Value : Levels.Count == 0 || Levels.Contains(0) ? 0 : Levels[0];
        (int X, int Y)? ghostAt = gx.HasValue && gy.HasValue ? (gx.Value, gy.Value) : null;
        if (ghostAt.HasValue && !Levels.Contains(Z)) { Levels = Levels.Append(Z).OrderBy(l => l).ToList(); }
        Map = new RoomMapLayout().Build(all, Area, Z, ghostAt);
        if (Map.Rects.Any(r => r.GenTerrain != null))
        {
            // Colours are cosmetic: when core cannot give them, generated rooms get the neutral generated colour.
            try
            {
                foreach (var c in await _core.ListTerrainColorsAsync(_token, cancellation))
                {
                    if (ColorPattern.IsMatch(c.Color)) { _colors[(c.Map, c.Terrain)] = c.Color; }
                }
            }
            catch (CoreUnavailableException) { }
            catch (CoreRefusedException) { }
        }
        return MapOutcome.Ok;
    }

    // Rooms without provenance keep the plain fill. A generated room takes the legend colour of its terrain (from its map file), else a neutral grey.
    public string FillFor(MapRect rect)
    {
        if (rect.GenTerrain == null) { return PlainFill; }
        return _colors.TryGetValue((rect.GenMap ?? string.Empty, rect.GenTerrain), out var color) ? color : GeneratedFallbackFill;
    }
}
