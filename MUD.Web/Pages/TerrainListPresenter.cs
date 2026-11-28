using MUD.Contracts;
using MUD.Web.Services;

namespace MUD.Web.Pages;

public enum TerrainListOutcome { Ok, Failed }

// The terrain library list logic, free of HttpContext, TempData and IActionResult (WEB_BLAZOR.md 4.1). The page is organised as the data is:
// an AREA owns terrain types, a TERRAIN TYPE owns fragments, feature templates and neighbour phrases. The query string is the only state:
// ?area=&terrain=&tab=&q=&tag=. Choosing another area resets the terrain to the first terrain type of that area (or to none).
// Everything goes through IAdminClient; core enforces the role and validates every write. Messages are fixed strings or core's own wording.
public sealed class TerrainListPresenter
{
    public sealed record Row(string Kind, string Id, string Terrain, int Weight, string Details);

    private readonly IAdminClient _core;
    private readonly string _token;

    public TerrainListPresenter(IAdminClient core, string token)
    {
        _core = core;
        _token = token;
    }

    public IReadOnlyList<AreaRecord> Areas { get; private set; } = Array.Empty<AreaRecord>();
    // The terrain types of the chosen area, in id order.
    public IReadOnlyList<TerrainTypeRecord> Types { get; private set; } = Array.Empty<TerrainTypeRecord>();
    public IReadOnlyList<FragmentRecord> Fragments { get; private set; } = Array.Empty<FragmentRecord>();
    public IReadOnlyList<FeatureTemplateRecord> Features { get; private set; } = Array.Empty<FeatureTemplateRecord>();
    public IReadOnlyList<NeighborPhraseRecord> Phrases { get; private set; } = Array.Empty<NeighborPhraseRecord>();
    public IReadOnlyList<Row> Rows { get; private set; } = Array.Empty<Row>();
    // Local names found in the area's map legend that have no terrain type yet (a suggestion list for the create form).
    public IReadOnlyList<string> Suggestions { get; private set; } = Array.Empty<string>();
    public string? Area { get; private set; }
    public string? Terrain { get; private set; }
    // The section to open on load (a kind), or null: every entry section starts closed. The page is one list of collapsible sections.
    public string? Tab { get; private set; }
    public string? TextFilter { get; private set; }
    public string? TagFilter { get; private set; }
    public string? Error { get; private set; }
    public string? Notice { get; set; }

    public TerrainTypeRecord? CurrentType => Terrain == null ? null : Types.FirstOrDefault(t => t.Id == Terrain);

    public bool FilterActive => TextFilter != null || TagFilter != null;
    public IEnumerable<Row> RowsOf(string kind) => Rows.Where(r => r.Kind == kind);
    public int TotalOf(string kind) => kind switch
    {
        TerrainLibrary.Fragment => Fragments.Count,
        TerrainLibrary.Feature => Features.Count,
        _ => Phrases.Count,
    };
    // The count in a section heading: "3" or, while a text or tag filter is active, "3 of 13" (shown of total).
    public string CountLabel(string kind) => FilterActive ? RowsOf(kind).Count() + " of " + TotalOf(kind) : TotalOf(kind).ToString();

    // The form of the CURRENT terrain type (name and description; the id is read-only). Not-yet-saved edits live here until the page is left.
    public string? TypeName { get; set; }
    public string? TypeDescription { get; set; }
    public List<string> TypeProblems { get; } = new();

    // The form of a NEW terrain type: the local part is typed, the area is the chosen one.
    public string? FormLocal { get; set; }
    public string? FormName { get; set; }
    public string? FormDescription { get; set; }
    public List<string> Problems { get; } = new();
    // The id of a terrain type just created, so the page can select it.
    public string? CreatedId { get; private set; }

    public static string EditUrl(string kind, string id) => WebRoutes.TerrainEdit(kind, id);

    // The list address; the query string is the only state of the page.
    public static string ListUrl(string? area, string? terrain, string? tab, string? text = null, string? tag = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(area)) { parts.Add("area=" + Uri.EscapeDataString(area.Trim())); }
        if (!string.IsNullOrWhiteSpace(terrain)) { parts.Add("terrain=" + Uri.EscapeDataString(terrain.Trim())); }
        var open = NormaliseTab(tab);
        if (open != null) { parts.Add("tab=" + Uri.EscapeDataString(open)); }
        if (!string.IsNullOrWhiteSpace(text)) { parts.Add("q=" + Uri.EscapeDataString(text.Trim())); }
        if (!string.IsNullOrWhiteSpace(tag)) { parts.Add("tag=" + Uri.EscapeDataString(tag.Trim())); }
        return WebRoutes.Terrain + (parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts));
    }

    // A known kind opens its section on load; anything else (or nothing) opens none.
    public static string? NormaliseTab(string? tab) => TerrainLibrary.IsKind(tab) ? tab : null;

    // The address a choice of area stands for: the terrain is dropped (the next load picks the first terrain type of the area).
    public static string AreaUrl(string? area, string? tab) => ListUrl(area, null, tab);

    public async Task<TerrainListOutcome> LoadAsync(string? area, string? terrain, string? tab, string? text, string? tag, CancellationToken cancellation = default)
    {
        Error = null;
        Tab = NormaliseTab(tab);
        TextFilter = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        TagFilter = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();
        try
        {
            Areas = (await _core.ListAreasAsync(_token, cancellation)).OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
            Area = Areas.Any(a => a.Id == area) ? area : null;
            Types = Area == null ? Array.Empty<TerrainTypeRecord>()
                : (await _core.ListTerrainTypesAsync(_token, Area, cancellation)).Where(t => t.Area == Area).OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
            Terrain = Types.Any(t => t.Id == terrain) ? terrain : Types.FirstOrDefault()?.Id;
            Fragments = Array.Empty<FragmentRecord>(); Features = Array.Empty<FeatureTemplateRecord>(); Phrases = Array.Empty<NeighborPhraseRecord>();
            if (Terrain != null)
            {
                Fragments = await _core.ListFragmentsAsync(_token, Terrain, null, cancellation);
                Features = await _core.ListFeatureTemplatesAsync(_token, Terrain, null, cancellation);
                Phrases = await _core.ListNeighborPhrasesAsync(_token, Terrain, null, cancellation);
            }
            Rows = BuildRows();
            TypeName = CurrentType?.Name; TypeDescription = CurrentType?.Description;
            TypeProblems.Clear();
            await LoadSuggestionsAsync(cancellation);
            return TerrainListOutcome.Ok;
        }
        catch (CoreUnavailableException) { Error = TerrainLibrary.UnavailableError; }
        catch (CoreRefusedException) { Error = TerrainLibrary.ForbiddenError; }
        catch (Exception) { Error = TerrainLibrary.FailedError; }
        Rows = Array.Empty<Row>();
        return TerrainListOutcome.Failed;
    }

    // Optional help for the create form: the legend template names of the map file whose name is the area prefix (ListTerrainColors).
    // Any failure just means no suggestions.
    private async Task LoadSuggestionsAsync(CancellationToken cancellation)
    {
        Suggestions = Array.Empty<string>();
        if (Area == null) { return; }
        try
        {
            var have = Types.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            Suggestions = (await _core.ListTerrainColorsAsync(_token, cancellation))
                .Where(c => c.Map == Area).Select(c => c.Terrain).Where(n => !string.IsNullOrEmpty(n) && !have.Contains(Area + "." + n))
                .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
        }
        catch (Exception) { Suggestions = Array.Empty<string>(); }
    }

    // Narrows the already loaded lists by the two text filters. No call to core: the live filter boxes use this on every settled value.
    public void ApplyFilters(string? text, string? tag)
    {
        TextFilter = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        TagFilter = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();
        Rows = BuildRows();
    }

    // The state a list address stands for; see PlayerListPresenter.StateKey.
    public static string StateKey(string? area, string? terrain, string? tab, string? text, string? tag) =>
        ListUrl(area, terrain, tab, LiveFilterRunner.Clean(text, 100), LiveFilterRunner.Clean(tag, 100));

    public string CurrentKey => StateKey(Area, Terrain, Tab, TextFilter, TagFilter);

    // ---- entries ----

    // Deletes one entry. Sets Notice (fixed sentence or core's message) on any reply, Error when core cannot be reached or refuses the
    // session. The caller reloads the list afterwards. An unknown kind never reaches core.
    public async Task<TerrainListOutcome> DeleteAsync(string? kind, string? id, CancellationToken cancellation = default)
    {
        Error = null;
        Notice = null;
        if (!TerrainLibrary.IsKind(kind)) { Error = TerrainLibrary.FailedError; return TerrainListOutcome.Failed; }
        try
        {
            var reply = kind switch
            {
                TerrainLibrary.Fragment => await _core.DeleteFragmentAsync(_token, id ?? string.Empty, cancellation),
                TerrainLibrary.Feature => await _core.DeleteFeatureTemplateAsync(_token, id ?? string.Empty, cancellation),
                _ => await _core.DeleteNeighborPhraseAsync(_token, id ?? string.Empty, cancellation),
            };
            var message = string.IsNullOrWhiteSpace(reply.Message) ? null : reply.Message;
            switch (reply.Result)
            {
                case AdminResult.Ok: Notice = "Deleted."; break;
                case AdminResult.NoChange: Notice = message ?? "Nothing was changed."; break;
                case AdminResult.NotFound: Notice = "That entry does not exist."; break;
                case AdminResult.Refused: Notice = TerrainLibrary.RefusedError; break;
                case AdminResult.AuditFailed: Notice = TerrainLibrary.AuditFailedError; break;
                case AdminResult.Forbidden: Notice = TerrainLibrary.ForbiddenError; break;
                default: Notice = TerrainLibrary.FailedError; break;
            }
            return TerrainListOutcome.Ok;
        }
        catch (CoreUnavailableException) { Error = TerrainLibrary.UnavailableError; }
        catch (CoreRefusedException) { Error = TerrainLibrary.ForbiddenError; }
        catch (Exception) { Error = TerrainLibrary.FailedError; }
        return TerrainListOutcome.Failed;
    }

    // ---- terrain types of the chosen area ----

    public void ResetNewTypeForm()
    {
        FormLocal = null; FormName = null; FormDescription = null;
        Problems.Clear();
    }

    public static string LocalPart(string id) => id.Contains('.') ? id.Substring(id.LastIndexOf('.') + 1) : id;

    // Creates a terrain type: sends "<area>.<local>". On Ok the form is emptied. Core's refusal or message is shown as core worded it.
    public async Task<TerrainListOutcome> SaveTypeAsync(CancellationToken cancellation = default)
    {
        Error = null;
        Notice = null;
        Problems.Clear();
        CreatedId = null;
        if (Area == null) { Error = TerrainLibrary.FailedError; return TerrainListOutcome.Failed; }
        var record = new TerrainTypeRecord { Id = Area + "." + (FormLocal ?? string.Empty).Trim(), Name = FormName ?? string.Empty, Description = FormDescription ?? string.Empty };
        var outcome = await WriteTypeAsync(record, true, Problems, cancellation);
        if (outcome == TerrainListOutcome.Ok) { ResetNewTypeForm(); }
        return outcome;
    }

    // Saves the name and description of the CURRENT terrain type. It always writes CurrentType.Id, so the form can never change which
    // terrain type is written (the id is read-only).
    public async Task<TerrainListOutcome> SaveCurrentTypeAsync(CancellationToken cancellation = default)
    {
        Error = null;
        Notice = null;
        TypeProblems.Clear();
        CreatedId = null;
        var current = CurrentType;
        if (current == null) { Error = TerrainLibrary.FailedError; return TerrainListOutcome.Failed; }
        var record = new TerrainTypeRecord { Id = current.Id, Area = current.Area, Name = TypeName ?? string.Empty, Description = TypeDescription ?? string.Empty };
        return await WriteTypeAsync(record, false, TypeProblems, cancellation);
    }

    private async Task<TerrainListOutcome> WriteTypeAsync(TerrainTypeRecord record, bool creating, List<string> problems, CancellationToken cancellation)
    {
        try
        {
            var reply = creating ? await _core.CreateTerrainTypeAsync(_token, record, cancellation) : await _core.UpdateTerrainTypeAsync(_token, record, cancellation);
            var message = string.IsNullOrWhiteSpace(reply.Message) ? null : reply.Message;
            switch (reply.Result)
            {
                case AdminResult.Ok:
                    Notice = creating ? "Terrain type created." : "Terrain type saved.";
                    if (creating) { CreatedId = record.Id; }
                    return TerrainListOutcome.Ok;
                case AdminResult.NoChange:
                    Notice = message ?? "Nothing was changed.";
                    return TerrainListOutcome.Ok;
                case AdminResult.Invalid:
                    foreach (var e in reply.Errors) { problems.Add(TerrainLibrary.ErrorText(e)); }
                    if (problems.Count == 0) { Error = message ?? TerrainLibrary.FailedError; }
                    break;
                case AdminResult.AlreadyExists: Error = message ?? "A terrain type with that id already exists."; break;
                case AdminResult.NotFound: Error = message ?? "That terrain type does not exist."; break;
                case AdminResult.Refused: Error = message ?? TerrainLibrary.RefusedError; break;
                case AdminResult.AuditFailed: Error = TerrainLibrary.AuditFailedError; break;
                case AdminResult.Forbidden: Error = TerrainLibrary.ForbiddenError; break;
                default: Error = TerrainLibrary.FailedError; break;
            }
        }
        catch (CoreUnavailableException) { Error = TerrainLibrary.UnavailableError; }
        catch (CoreRefusedException) { Error = TerrainLibrary.ForbiddenError; }
        catch (Exception) { Error = TerrainLibrary.FailedError; }
        return TerrainListOutcome.Failed;
    }

    // Deletes one terrain type. Core refuses while entries still use it; its message (it names the counts) is shown verbatim.
    public async Task<TerrainListOutcome> DeleteTypeAsync(string? id, CancellationToken cancellation = default)
    {
        Error = null;
        Notice = null;
        try
        {
            var reply = await _core.DeleteTerrainTypeAsync(_token, id ?? string.Empty, cancellation);
            var message = string.IsNullOrWhiteSpace(reply.Message) ? null : reply.Message;
            switch (reply.Result)
            {
                case AdminResult.Ok: Notice = "Terrain type deleted."; return TerrainListOutcome.Ok;
                case AdminResult.NotFound: Notice = "That terrain type does not exist."; return TerrainListOutcome.Ok;
                case AdminResult.Refused: Error = message ?? TerrainLibrary.RefusedError; break;
                case AdminResult.AuditFailed: Error = TerrainLibrary.AuditFailedError; break;
                case AdminResult.Forbidden: Error = TerrainLibrary.ForbiddenError; break;
                default: Error = TerrainLibrary.FailedError; break;
            }
        }
        catch (CoreUnavailableException) { Error = TerrainLibrary.UnavailableError; }
        catch (CoreRefusedException) { Error = TerrainLibrary.ForbiddenError; }
        catch (Exception) { Error = TerrainLibrary.FailedError; }
        return TerrainListOutcome.Failed;
    }

    private IReadOnlyList<Row> BuildRows()
    {
        var all = new List<(Row Row, IReadOnlyList<string> Tags, string Text)>();
        all.AddRange(Fragments.Select(f => (new Row(TerrainLibrary.Fragment, f.Id, f.Terrain, f.Weight, "slot " + f.Slot + ": " + Shorten(f.Text)), (IReadOnlyList<string>)f.Tags.ToList(), f.Slot + " " + f.Text)));
        all.AddRange(Features.Select(f => (new Row(TerrainLibrary.Feature, f.Id, f.Terrain, f.Weight, "version " + f.Version + ", chance " + f.Chance + ": " + Shorten(f.Short)), (IReadOnlyList<string>)f.Tags.ToList(), f.Short + " " + f.Long + " " + f.RoomLine)));
        all.AddRange(Phrases.Select(f => (new Row(TerrainLibrary.Phrase, f.Id, f.Terrain, f.Weight, "next to " + f.NeighborTerrain + ": " + Shorten(TerrainLibrary.PhrasePreview(f.Text))), (IReadOnlyList<string>)Array.Empty<string>(), f.NeighborTerrain + " " + f.Text)));
        IEnumerable<(Row Row, IReadOnlyList<string> Tags, string Text)> q = all;
        // The text filter matches the id and the entry's own words, case-insensitively. It applies to all three kinds at once.
        if (TextFilter != null) { q = q.Where(x => x.Row.Id.Contains(TextFilter, StringComparison.OrdinalIgnoreCase) || x.Text.Contains(TextFilter, StringComparison.OrdinalIgnoreCase)); }
        if (TagFilter != null) { q = q.Where(x => x.Tags.Contains(TagFilter, StringComparer.Ordinal)); }
        return q.Select(x => x.Row).OrderBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
    }

    private static string Shorten(string? s)
    {
        s ??= string.Empty;
        return s.Length <= 80 ? s : s.Substring(0, 77) + "...";
    }
}
