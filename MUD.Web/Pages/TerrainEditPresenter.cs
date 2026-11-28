using MUD.Contracts;
using MUD.Web.Services;

namespace MUD.Web.Pages;

public enum TerrainEditOutcome { Ok, NoChange, Invalid, Failed, Missing }
public enum TerrainEditMissing { None, NotFound, Unavailable, Forbidden }

// The terrain library edit logic, free of HttpContext, ModelState, TempData and IActionResult (WEB_BLAZOR.md 4.1). The kind and the original id
// come from the address only and have no setter, so a form can never change which entity is written; an edit always writes the original id,
// never NewId. Core validates every field and owns the feature version.
// The entry belongs to ONE terrain type of ONE area: two dropdowns (area, then the terrain types of that area). A phrase's neighbour terrain is a
// dropdown of the SAME area's terrain types. Core checks that every id exists and is in the right area.
public sealed class TerrainEditPresenter
{
    private const string GoneText = "That library entry does not exist.";
    public const string TerrainRequired = "Choose a terrain type of an existing area.";
    public const string NeighborRequired = "Choose a neighbour terrain from the same area's terrain types.";

    private readonly IAdminClient _core;
    private readonly string _token;
    private readonly string? _prefillArea;
    private readonly string? _prefillTerrain;
    private IReadOnlyList<TerrainTypeRecord> _types = Array.Empty<TerrainTypeRecord>();

    // area and terrain only pre-fill a NEW entry (the list page's choice); an existing entry shows where it is stored.
    public TerrainEditPresenter(IAdminClient core, string token, string? kind, string? id, string? area = null, string? terrain = null)
    {
        _core = core;
        _token = token;
        Kind = kind;
        OriginalId = string.IsNullOrEmpty(id) ? null : id;
        _prefillArea = string.IsNullOrWhiteSpace(area) ? null : area.Trim();
        _prefillTerrain = string.IsNullOrWhiteSpace(terrain) ? null : terrain.Trim();
    }

    public string? Kind { get; }
    public string? OriginalId { get; }
    public bool IsNew => OriginalId == null;

    // The id typed for a NEW entry. Ignored when editing.
    public string? NewId { get; set; }
    public string? Area { get; private set; }
    public string? Terrain { get; set; }
    public string? Slot { get; set; }
    public string? Text { get; set; }
    public int Weight { get; set; } = 1;
    public string? Tags { get; set; }
    public string? Short { get; set; }
    public string? Long { get; set; }
    public string? RoomLine { get; set; }
    public string? Handles { get; set; }
    public string? Adjectives { get; set; }
    public double Chance { get; set; } = 1.0;
    public string? NeighborTerrain { get; set; }
    public bool Merge { get; set; }

    public int Version { get; private set; }
    public IReadOnlyList<string> AreaChoices { get; private set; } = Array.Empty<string>();

    // The terrain types of the chosen area (the values of the terrain and neighbour dropdowns). A stored terrain that no longer exists is not in here.
    public IReadOnlyList<TerrainTypeRecord> TerrainChoices => _types.Where(t => Area != null && t.Area == Area).OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
    public IReadOnlyList<TerrainTypeRecord> NeighborChoices => TerrainChoices;

    // True while the chosen terrain (or a phrase's neighbour) names a terrain type that is not among the choices. The dropdown shows it as
    // "(missing: <id>)" and saving requires a valid choice.
    public bool TerrainIsMissing => !string.IsNullOrEmpty(Terrain) && !TerrainChoices.Any(t => t.Id == Terrain);
    public bool NeighborIsMissing => !string.IsNullOrEmpty(NeighborTerrain) && !NeighborChoices.Any(t => t.Id == NeighborTerrain);

    public static string MissingLabel(string id) => "(missing: " + id + ")";

    public string? Error { get; private set; }
    public string? Notice { get; set; }
    public TerrainEditMissing Missing { get; private set; }
    public bool IsMissing => Missing != TerrainEditMissing.None;

    // Core's field messages after an Invalid reply (or the two choose-a-terrain sentences); the user's input stays in the form.
    public List<string> Problems { get; } = new();

    public string VersionHint => IsNew
        ? "A new template starts at version 1."
        : "Saving a change to the short, long, room line, handles or adjectives raises the version from " + Version + " to " + (Version + 1) + ".";

    public string KindLabel => TerrainLibrary.KindLabel(Kind ?? string.Empty);

    public bool HasPreview => Kind == TerrainLibrary.Phrase || Kind == TerrainLibrary.Feature;

    // Preview of the saved values (core offers no sample-room render yet).
    public string PreviewText => Kind == TerrainLibrary.Phrase ? TerrainLibrary.PhrasePreview(Text) : (string.IsNullOrWhiteSpace(RoomLine) ? Short ?? string.Empty : RoomLine);

    // The list address that goes with this entry's area and terrain (for "Back to the library").
    public string BackUrl(string? tab = null) => TerrainListPresenter.ListUrl(Area, Terrain, tab ?? Kind);

    // A new area was chosen: the entry moves to the first terrain type of that area (or none), and a neighbour outside the area is dropped.
    public void ChooseArea(string? area)
    {
        Area = string.IsNullOrWhiteSpace(area) ? null : area;
        Terrain = TerrainChoices.FirstOrDefault()?.Id;
        if (!string.IsNullOrEmpty(NeighborTerrain) && !NeighborChoices.Any(t => t.Id == NeighborTerrain)) { NeighborTerrain = null; }
    }

    public void ChooseTerrain(string? terrain) => Terrain = string.IsNullOrWhiteSpace(terrain) ? null : terrain;

    public async Task<TerrainEditOutcome> LoadAsync(CancellationToken cancellation = default)
    {
        Error = null;
        Problems.Clear();
        Missing = TerrainEditMissing.None;
        if (!TerrainLibrary.IsKind(Kind)) { return Gone(TerrainEditMissing.NotFound, GoneText); }
        try
        {
            if (!IsNew && !await FillFromCore(cancellation)) { return Gone(TerrainEditMissing.NotFound, GoneText); }
            await LoadChoices(cancellation);
            return TerrainEditOutcome.Ok;
        }
        catch (CoreUnavailableException) { return Gone(TerrainEditMissing.Unavailable, TerrainLibrary.UnavailableError); }
        catch (CoreRefusedException) { return Gone(TerrainEditMissing.Forbidden, TerrainLibrary.ForbiddenError); }
        catch (Exception) { return Gone(TerrainEditMissing.Unavailable, TerrainLibrary.FailedError); }
    }

    public async Task<TerrainEditOutcome> SaveAsync(CancellationToken cancellation = default)
    {
        Error = null;
        Notice = null;
        Problems.Clear();
        if (!TerrainLibrary.IsKind(Kind)) { return Gone(TerrainEditMissing.NotFound, GoneText); }
        var id = ((IsNew ? NewId : OriginalId) ?? string.Empty).Trim();
        var terrain = (Terrain ?? string.Empty).Trim();
        var neighbour = (NeighborTerrain ?? string.Empty).Trim();
        // A terrain type that does not exist (a stored entry whose terrain type is gone) is never sent: choose a valid one first.
        if (terrain.Length == 0 || !TerrainChoices.Any(t => t.Id == terrain)) { Problems.Add(TerrainRequired); }
        if (Kind == TerrainLibrary.Phrase && (neighbour.Length == 0 || !NeighborChoices.Any(t => t.Id == neighbour))) { Problems.Add(NeighborRequired); }
        if (Problems.Count > 0) { return TerrainEditOutcome.Invalid; }
        try
        {
            LibraryEditReply reply;
            switch (Kind)
            {
                case TerrainLibrary.Fragment:
                    var f = new FragmentRecord { Id = id, Slot = (Slot ?? string.Empty).Trim(), Text = Text ?? string.Empty, Weight = Weight };
                    f.Terrain = terrain; f.Tags.AddRange(TerrainLibrary.Split(Tags));
                    reply = IsNew ? await _core.CreateFragmentAsync(_token, f, cancellation) : await _core.UpdateFragmentAsync(_token, f, cancellation);
                    break;
                case TerrainLibrary.Feature:
                    var t = new FeatureTemplateRecord
                    {
                        Id = id, Short = Short ?? string.Empty, Long = Long ?? string.Empty, RoomLine = RoomLine ?? string.Empty,
                        Weight = Weight, Chance = Chance,
                    };
                    t.Terrain = terrain; t.Handles.AddRange(TerrainLibrary.Split(Handles)); t.Adjectives.AddRange(TerrainLibrary.Split(Adjectives));
                    t.Tags.AddRange(TerrainLibrary.Split(Tags));
                    reply = IsNew ? await _core.CreateFeatureTemplateAsync(_token, t, cancellation) : await _core.UpdateFeatureTemplateAsync(_token, t, cancellation);
                    break;
                default:
                    var p = new NeighborPhraseRecord
                    {
                        Id = id, NeighborTerrain = neighbour, Text = Text ?? string.Empty, Weight = Weight, Merge = Merge,
                    };
                    p.Terrain = terrain;
                    reply = IsNew ? await _core.CreateNeighborPhraseAsync(_token, p, cancellation) : await _core.UpdateNeighborPhraseAsync(_token, p, cancellation);
                    break;
            }
            Terrain = terrain;
            switch (reply.Result)
            {
                case AdminResult.Ok:
                    Notice = "Saved.";
                    return TerrainEditOutcome.Ok;
                case AdminResult.NoChange:
                    Notice = "Nothing was changed."; return TerrainEditOutcome.NoChange;
                case AdminResult.Invalid:
                    foreach (var e in reply.Errors) { Problems.Add(TerrainLibrary.ErrorText(e)); }
                    return TerrainEditOutcome.Invalid;
                case AdminResult.AlreadyExists:
                    Error = "An entry with that id already exists."; return TerrainEditOutcome.Failed;
                case AdminResult.NotFound:
                    return Gone(TerrainEditMissing.NotFound, GoneText);
                case AdminResult.Refused: Error = TerrainLibrary.RefusedError; return TerrainEditOutcome.Failed;
                case AdminResult.AuditFailed: Error = TerrainLibrary.AuditFailedError; return TerrainEditOutcome.Failed;
                case AdminResult.Forbidden: Error = TerrainLibrary.ForbiddenError; return TerrainEditOutcome.Failed;
                default: Error = TerrainLibrary.FailedError; return TerrainEditOutcome.Failed;
            }
        }
        catch (CoreUnavailableException) { return Gone(TerrainEditMissing.Unavailable, TerrainLibrary.UnavailableError); }
        catch (CoreRefusedException) { return Gone(TerrainEditMissing.Forbidden, TerrainLibrary.ForbiddenError); }
        catch (Exception) { return Gone(TerrainEditMissing.Unavailable, TerrainLibrary.FailedError); }
    }

    private TerrainEditOutcome Gone(TerrainEditMissing why, string error)
    {
        Missing = why;
        Error = error;
        return TerrainEditOutcome.Missing;
    }

    private async Task<bool> FillFromCore(CancellationToken ct)
    {
        var id = OriginalId!;
        switch (Kind)
        {
            case TerrainLibrary.Fragment:
                var f = await _core.GetFragmentAsync(_token, id, ct);
                if (f == null) { return false; }
                Terrain = f.Terrain; Slot = f.Slot; Text = f.Text; Weight = f.Weight; Tags = TerrainLibrary.Join(f.Tags);
                return true;
            case TerrainLibrary.Feature:
                var t = await _core.GetFeatureTemplateAsync(_token, id, ct);
                if (t == null) { return false; }
                Terrain = t.Terrain; Short = t.Short; Long = t.Long; RoomLine = t.RoomLine;
                Handles = TerrainLibrary.Join(t.Handles); Adjectives = TerrainLibrary.Join(t.Adjectives); Weight = t.Weight; Chance = t.Chance;
                Tags = TerrainLibrary.Join(t.Tags); Version = t.Version;
                return true;
            default:
                var p = await _core.GetNeighborPhraseAsync(_token, id, ct);
                if (p == null) { return false; }
                Terrain = p.Terrain; NeighborTerrain = p.NeighborTerrain; Text = p.Text; Weight = p.Weight; Merge = p.Merge;
                return true;
        }
    }

    // Every terrain type of every area, then the area of the entry: the one the stored terrain belongs to (or, when that terrain type is gone, the
    // part of its id before the last dot); for a new entry the pre-filled area and terrain of the list page.
    private async Task LoadChoices(CancellationToken ct)
    {
        _types = await _core.ListTerrainTypesAsync(_token, null, ct);
        AreaChoices = (await _core.ListAreasAsync(_token, ct)).Select(a => a.Id).Concat(_types.Select(t => t.Area)).Where(a => !string.IsNullOrEmpty(a))
            .Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal).ToList();
        if (IsNew)
        {
            if (Area == null && _prefillArea != null && AreaChoices.Contains(_prefillArea)) { Area = _prefillArea; }
            if (Terrain == null && Area != null)
            {
                Terrain = _prefillTerrain != null && TerrainChoices.Any(t => t.Id == _prefillTerrain) ? _prefillTerrain : TerrainChoices.FirstOrDefault()?.Id;
            }
            return;
        }
        var stored = _types.FirstOrDefault(t => t.Id == Terrain)?.Area;
        if (stored != null) { Area = stored; }
        else if (!string.IsNullOrEmpty(Terrain) && Terrain.Contains('.'))
        {
            var guess = Terrain.Substring(0, Terrain.LastIndexOf('.'));
            if (AreaChoices.Contains(guess)) { Area = guess; }
        }
    }
}
