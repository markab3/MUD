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

    public IReadOnlyList<string> Areas { get; private set; } = Array.Empty<string>();

    // "prefix - Name" for an area with a record, the bare prefix otherwise; "(no area)" for rooms without one.
    public string LabelFor(string area) =>
        area.Length == 0 ? "(no area)" : _names.TryGetValue(area, out var name) && !string.IsNullOrWhiteSpace(name) ? area + " - " + name : area;
    public IReadOnlyList<int> Levels { get; private set; } = Array.Empty<int>();
    public string? Area { get; private set; }
    public int Z { get; private set; }
    public RoomMap? Map { get; private set; }
    public string? Error { get; private set; }

    public async Task<MapOutcome> LoadAsync(string? area, int? z, CancellationToken cancellation = default)
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
        var recordIds = new List<string>();
        try
        {
            foreach (var a in await _core.ListAreasAsync(_token, cancellation))
            {
                recordIds.Add(a.Id);
                _names[a.Id] = a.Name;
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
        Map = new RoomMapLayout().Build(all, Area, Z);
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
