using MUD.Contracts;
using MUD.Web.Services;

namespace MUD.Web.Pages;

public enum TerrainListOutcome { Ok, Failed }

// The terrain library list logic (moved from TerrainListModel), free of HttpContext, TempData and IActionResult (WEB_BLAZOR.md 4.1).
// The tab and filters come from the query string only. Everything goes through IAdminClient; core enforces the role and validates every
// write. Messages are fixed strings or core's own wording.
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

    public IReadOnlyList<FragmentRecord> Fragments { get; private set; } = Array.Empty<FragmentRecord>();
    public IReadOnlyList<FeatureTemplateRecord> Features { get; private set; } = Array.Empty<FeatureTemplateRecord>();
    public IReadOnlyList<NeighborPhraseRecord> Phrases { get; private set; } = Array.Empty<NeighborPhraseRecord>();
    public IReadOnlyList<Row> Rows { get; private set; } = Array.Empty<Row>();
    public IReadOnlyList<string> KnownTerrains { get; private set; } = Array.Empty<string>();
    public string Tab { get; private set; } = TerrainLibrary.Fragment;
    public string? TerrainFilter { get; private set; }
    public string? TagFilter { get; private set; }
    public string? Error { get; private set; }
    public string? Notice { get; set; }

    public string TabLabel => Tab switch
    {
        TerrainLibrary.Feature => "Feature templates",
        TerrainLibrary.Phrase => "Neighbour phrases",
        _ => "Description fragments",
    };

    public static string EditUrl(string kind, string id) => WebRoutes.TerrainEdit(kind, id);

    // The list address for a tab and filters; the query string is the only state of the page.
    public static string ListUrl(string? tab, string? terrain, string? tag)
    {
        var url = WebRoutes.Terrain + "?tab=" + Uri.EscapeDataString(NormaliseTab(tab));
        if (!string.IsNullOrWhiteSpace(terrain)) { url += "&terrain=" + Uri.EscapeDataString(terrain.Trim()); }
        if (!string.IsNullOrWhiteSpace(tag)) { url += "&tag=" + Uri.EscapeDataString(tag.Trim()); }
        return url;
    }

    public static string NormaliseTab(string? tab) =>
        tab == TerrainLibrary.Feature || tab == TerrainLibrary.Phrase ? tab : TerrainLibrary.Fragment;

    public async Task<TerrainListOutcome> LoadAsync(string? tab, string? terrain, string? tag, CancellationToken cancellation = default)
    {
        Error = null;
        Tab = NormaliseTab(tab);
        TerrainFilter = string.IsNullOrWhiteSpace(terrain) ? null : terrain.Trim();
        TagFilter = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();
        try
        {
            Fragments = await _core.ListFragmentsAsync(_token, null, null, cancellation);
            Features = await _core.ListFeatureTemplatesAsync(_token, null, null, cancellation);
            Phrases = await _core.ListNeighborPhrasesAsync(_token, null, null, cancellation);
            KnownTerrains = (await _core.ListTerrainTypesAsync(_token, null, cancellation)).Select(t => t.Id).OrderBy(t => t, StringComparer.Ordinal).ToList();
            Rows = BuildRows();
            return TerrainListOutcome.Ok;
        }
        catch (CoreUnavailableException) { Error = TerrainLibrary.UnavailableError; }
        catch (CoreRefusedException) { Error = TerrainLibrary.ForbiddenError; }
        catch (Exception) { Error = TerrainLibrary.FailedError; }
        Rows = Array.Empty<Row>();
        return TerrainListOutcome.Failed;
    }

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

    private IReadOnlyList<Row> BuildRows()
    {
        var all = new List<(Row Row, IReadOnlyList<string> Tags)>();
        if (Tab == TerrainLibrary.Fragment)
        {
            all.AddRange(Fragments.Select(f => (new Row(TerrainLibrary.Fragment, f.Id, f.Terrain, f.Weight, "slot " + f.Slot + ": " + Shorten(f.Text)), (IReadOnlyList<string>)f.Tags.ToList())));
        }
        if (Tab == TerrainLibrary.Feature)
        {
            all.AddRange(Features.Select(f => (new Row(TerrainLibrary.Feature, f.Id, f.Terrain, f.Weight, "version " + f.Version + ", chance " + f.Chance + ": " + Shorten(f.Short)), (IReadOnlyList<string>)f.Tags.ToList())));
        }
        if (Tab == TerrainLibrary.Phrase)
        {
            all.AddRange(Phrases.Select(f => (new Row(TerrainLibrary.Phrase, f.Id, f.Terrain, f.Weight, "next to " + f.NeighborTerrain + ": " + Shorten(TerrainLibrary.PhrasePreview(f.Text))), (IReadOnlyList<string>)Array.Empty<string>())));
        }
        IEnumerable<(Row Row, IReadOnlyList<string> Tags)> q = all;
        if (TerrainFilter != null) { q = q.Where(x => string.Equals(x.Row.Terrain, TerrainFilter, StringComparison.Ordinal)); }
        if (TagFilter != null) { q = q.Where(x => x.Tags.Contains(TagFilter, StringComparer.Ordinal)); }
        return q.Select(x => x.Row).OrderBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
    }

    private static string Shorten(string? s)
    {
        s ??= string.Empty;
        return s.Length <= 80 ? s : s.Substring(0, 77) + "...";
    }
}
