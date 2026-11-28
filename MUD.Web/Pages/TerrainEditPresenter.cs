using MUD.Contracts;
using MUD.Web.Services;

namespace MUD.Web.Pages;

public enum TerrainEditOutcome { Ok, NoChange, Invalid, Failed, Missing }
public enum TerrainEditMissing { None, NotFound, Unavailable, Forbidden }

// The terrain library edit logic (moved from TerrainEditModel), free of HttpContext, ModelState, TempData and IActionResult
// (WEB_BLAZOR.md 4.1). The kind and the original id come from the address only and have no setter, so a form can never change which
// entity is written; an edit always writes the original id, never NewId. Core validates every field and owns the feature version.
// The terrain is one terrain type id chosen from a dropdown (ListTerrainTypes across all areas); core checks it exists.
public sealed class TerrainEditPresenter
{
    private const string GoneText = "That library entry does not exist.";

    private readonly IAdminClient _core;
    private readonly string _token;

    public TerrainEditPresenter(IAdminClient core, string token, string? kind, string? id)
    {
        _core = core;
        _token = token;
        Kind = kind;
        OriginalId = string.IsNullOrEmpty(id) ? null : id;
    }

    public string? Kind { get; }
    public string? OriginalId { get; }
    public bool IsNew => OriginalId == null;

    // The id typed for a NEW entry. Ignored when editing.
    public string? NewId { get; set; }
    public string? Terrain { get; set; }
    public string? Slot { get; set; }
    public string? Text { get; set; }
    public int Weight { get; set; } = 1;
    public string? Tags { get; set; }
    public string? Short { get; set; }
    public string? Long { get; set; }
    public string? RoomLine { get; set; }
    public string? Detail { get; set; }
    public string? Handles { get; set; }
    public string? Adjectives { get; set; }
    public double Chance { get; set; } = 1.0;
    public string? NeighborTerrain { get; set; }
    public bool Merge { get; set; }

    public int Version { get; private set; }
    public IReadOnlyList<string> TerrainChoices { get; private set; } = Array.Empty<string>();
    public string? Error { get; private set; }
    public string? Notice { get; set; }
    public TerrainEditMissing Missing { get; private set; }
    public bool IsMissing => Missing != TerrainEditMissing.None;

    // Core's field messages after an Invalid reply; the user's input stays in the form.
    public List<string> Problems { get; } = new();

    public string VersionHint => IsNew
        ? "A new template starts at version 1."
        : "Saving a change to the short, long, room line, detail, handles or adjectives raises the version from " + Version + " to " + (Version + 1) + ".";

    public string KindLabel => TerrainLibrary.KindLabel(Kind ?? string.Empty);

    public bool HasPreview => Kind == TerrainLibrary.Phrase || Kind == TerrainLibrary.Feature;

    // Preview of the saved values (core offers no sample-room render yet).
    public string PreviewText => Kind == TerrainLibrary.Phrase ? TerrainLibrary.PhrasePreview(Text) : (string.IsNullOrWhiteSpace(RoomLine) ? Short ?? string.Empty : RoomLine);

    public async Task<TerrainEditOutcome> LoadAsync(CancellationToken cancellation = default)
    {
        Error = null;
        Problems.Clear();
        Missing = TerrainEditMissing.None;
        if (!TerrainLibrary.IsKind(Kind)) { return Gone(TerrainEditMissing.NotFound, GoneText); }
        try
        {
            await LoadChoices(cancellation);
            if (IsNew) { return TerrainEditOutcome.Ok; }
            if (!await FillFromCore(cancellation)) { return Gone(TerrainEditMissing.NotFound, GoneText); }
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
                        Detail = Detail ?? string.Empty, Weight = Weight, Chance = Chance,
                    };
                    t.Terrain = terrain; t.Handles.AddRange(TerrainLibrary.Split(Handles)); t.Adjectives.AddRange(TerrainLibrary.Split(Adjectives));
                    t.Tags.AddRange(TerrainLibrary.Split(Tags));
                    reply = IsNew ? await _core.CreateFeatureTemplateAsync(_token, t, cancellation) : await _core.UpdateFeatureTemplateAsync(_token, t, cancellation);
                    break;
                default:
                    var p = new NeighborPhraseRecord
                    {
                        Id = id, NeighborTerrain = (NeighborTerrain ?? string.Empty).Trim(), Text = Text ?? string.Empty, Weight = Weight, Merge = Merge,
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
                    Notice = "Nothing was changed."; await TryLoadChoices(cancellation); return TerrainEditOutcome.NoChange;
                case AdminResult.Invalid:
                    foreach (var e in reply.Errors) { Problems.Add(TerrainLibrary.ErrorText(e)); }
                    await TryLoadChoices(cancellation);
                    return TerrainEditOutcome.Invalid;
                case AdminResult.AlreadyExists:
                    Error = "An entry with that id already exists."; await TryLoadChoices(cancellation); return TerrainEditOutcome.Failed;
                case AdminResult.NotFound:
                    return Gone(TerrainEditMissing.NotFound, GoneText);
                case AdminResult.Refused: Error = TerrainLibrary.RefusedError; await TryLoadChoices(cancellation); return TerrainEditOutcome.Failed;
                case AdminResult.AuditFailed: Error = TerrainLibrary.AuditFailedError; await TryLoadChoices(cancellation); return TerrainEditOutcome.Failed;
                case AdminResult.Forbidden: Error = TerrainLibrary.ForbiddenError; await TryLoadChoices(cancellation); return TerrainEditOutcome.Failed;
                default: Error = TerrainLibrary.FailedError; await TryLoadChoices(cancellation); return TerrainEditOutcome.Failed;
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
                Terrain = t.Terrain; Short = t.Short; Long = t.Long; RoomLine = t.RoomLine; Detail = t.Detail;
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

    private async Task TryLoadChoices(CancellationToken ct)
    {
        try { await LoadChoices(ct); } catch (Exception) { TerrainChoices = string.IsNullOrEmpty(Terrain) ? Array.Empty<string>() : new[] { Terrain }; }
    }

    // The terrain types of every area (the next card groups them by area), plus the one already chosen on this form.
    private async Task LoadChoices(CancellationToken ct)
    {
        var known = (await _core.ListTerrainTypesAsync(_token, null, ct)).Select(t => t.Id);
        TerrainChoices = known.Concat(string.IsNullOrEmpty(Terrain) ? Array.Empty<string>() : new[] { Terrain }).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList();
    }
}
