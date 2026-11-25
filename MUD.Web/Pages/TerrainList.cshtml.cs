using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MUD.Contracts;
using MUD.Web.Services;

namespace MUD.Web.Pages;

// /creator/terrain: the terrain library (description fragments, feature templates, neighbour phrases) with tabs and a terrain/tag filter,
// plus a Delete button per row. The "Unassigned" tab shows every entity whose terrains list is empty (used by no generator). Everything goes
// through IAdminClient; core enforces the role and validates every write. Messages are fixed strings or core's own wording; Razor encodes values.
[Authorize(Policy = WebPolicies.CreatorOrAdmin)]
public class TerrainListModel : PageModel
{
    public sealed record Row(string Kind, string Id, IReadOnlyList<string> Terrains, int Weight, string Details);

    private readonly IAdminClient _core;

    public TerrainListModel(IAdminClient core) { _core = core; }

    public IReadOnlyList<FragmentRecord> Fragments { get; private set; } = Array.Empty<FragmentRecord>();
    public IReadOnlyList<FeatureTemplateRecord> Features { get; private set; } = Array.Empty<FeatureTemplateRecord>();
    public IReadOnlyList<NeighborPhraseRecord> Phrases { get; private set; } = Array.Empty<NeighborPhraseRecord>();
    public IReadOnlyList<Row> Rows { get; private set; } = Array.Empty<Row>();
    public IReadOnlyList<string> KnownTerrains { get; private set; } = Array.Empty<string>();
    public string Tab { get; private set; } = TerrainLibrary.Fragment;
    public string? TerrainFilter { get; private set; }
    public string? TagFilter { get; private set; }
    public string? Error { get; private set; }
    public string? Notice { get; private set; }

    public int UnassignedCount => Fragments.Count(f => f.Terrains.Count == 0) + Features.Count(f => f.Terrains.Count == 0) + Phrases.Count(f => f.Terrains.Count == 0);

    public string TabLabel => Tab switch
    {
        TerrainLibrary.Feature => "Feature templates",
        TerrainLibrary.Phrase => "Neighbour phrases",
        TerrainLibrary.Unassigned => "Unassigned (not used by any generator)",
        _ => "Description fragments",
    };

    private string Token => SessionAuthentication.TokenFrom(Request);

    public static string EditUrl(string kind, string id) => "/creator/terrain/edit?kind=" + Uri.EscapeDataString(kind) + "&id=" + Uri.EscapeDataString(id);

    public async Task<IActionResult> OnGetAsync(string? tab, string? terrain, string? tag)
    {
        if (TempData?["Notice"] is string carried) { Notice = carried; }
        Tab = NormaliseTab(tab);
        TerrainFilter = string.IsNullOrWhiteSpace(terrain) ? null : terrain.Trim();
        TagFilter = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();
        return await Load();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string? kind, string? id, string? tab)
    {
        Tab = NormaliseTab(tab);
        if (!TerrainLibrary.IsKind(kind)) { return await Fail(TerrainLibrary.FailedError, StatusCodes.Status400BadRequest); }
        try
        {
            var ct = HttpContext.RequestAborted;
            var token = Token;
            var reply = kind switch
            {
                TerrainLibrary.Fragment => await _core.DeleteFragmentAsync(token, id ?? string.Empty, ct),
                TerrainLibrary.Feature => await _core.DeleteFeatureTemplateAsync(token, id ?? string.Empty, ct),
                _ => await _core.DeleteNeighborPhraseAsync(token, id ?? string.Empty, ct),
            };
            var message = string.IsNullOrWhiteSpace(reply.Message) ? null : reply.Message;
            string notice;
            switch (reply.Result)
            {
                case AdminResult.Ok: notice = "Deleted."; break;
                case AdminResult.NoChange: notice = message ?? "Nothing was changed."; break;
                case AdminResult.NotFound: notice = "That entry does not exist."; break;
                case AdminResult.Refused: notice = TerrainLibrary.RefusedError; break;
                case AdminResult.AuditFailed: notice = TerrainLibrary.AuditFailedError; break;
                case AdminResult.Forbidden: notice = TerrainLibrary.ForbiddenError; break;
                default: notice = TerrainLibrary.FailedError; break;
            }
            TempData["Notice"] = notice;
            return RedirectToPage(new { tab = Tab });
        }
        catch (CoreUnavailableException) { return await Fail(TerrainLibrary.UnavailableError, StatusCodes.Status503ServiceUnavailable); }
        catch (CoreRefusedException) { return await Fail(TerrainLibrary.ForbiddenError, StatusCodes.Status403Forbidden); }
        catch (Exception) { return await Fail(TerrainLibrary.FailedError, StatusCodes.Status500InternalServerError); }
    }

    private static string NormaliseTab(string? tab) =>
        tab == TerrainLibrary.Feature || tab == TerrainLibrary.Phrase || tab == TerrainLibrary.Unassigned ? tab : TerrainLibrary.Fragment;

    private Task<IActionResult> Fail(string error, int status)
    {
        Error = error;
        Response.StatusCode = status;
        return Task.FromResult<IActionResult>(Page());
    }

    private async Task<IActionResult> Load()
    {
        try
        {
            var ct = HttpContext.RequestAborted;
            var token = Token;
            Fragments = await _core.ListFragmentsAsync(token, ct);
            Features = await _core.ListFeatureTemplatesAsync(token, ct);
            Phrases = await _core.ListNeighborPhrasesAsync(token, ct);
            KnownTerrains = TerrainLibrary.KnownTerrains(Fragments, Features, Phrases);
            Rows = BuildRows();
            return Page();
        }
        catch (CoreUnavailableException) { return await Fail(TerrainLibrary.UnavailableError, StatusCodes.Status503ServiceUnavailable); }
        catch (CoreRefusedException) { return await Fail(TerrainLibrary.ForbiddenError, StatusCodes.Status403Forbidden); }
        catch (Exception) { return await Fail(TerrainLibrary.FailedError, StatusCodes.Status500InternalServerError); }
    }

    private IReadOnlyList<Row> BuildRows()
    {
        var all = new List<(Row Row, IReadOnlyList<string> Tags)>();
        if (Tab == TerrainLibrary.Fragment || Tab == TerrainLibrary.Unassigned)
        {
            all.AddRange(Fragments.Select(f => (new Row(TerrainLibrary.Fragment, f.Id, f.Terrains.ToList(), f.Weight, "slot " + f.Slot + ": " + Shorten(f.Text)), (IReadOnlyList<string>)f.Tags.ToList())));
        }
        if (Tab == TerrainLibrary.Feature || Tab == TerrainLibrary.Unassigned)
        {
            all.AddRange(Features.Select(f => (new Row(TerrainLibrary.Feature, f.Id, f.Terrains.ToList(), f.Weight, "version " + f.Version + ", chance " + f.Chance + ": " + Shorten(f.Short)), (IReadOnlyList<string>)f.Tags.ToList())));
        }
        if (Tab == TerrainLibrary.Phrase || Tab == TerrainLibrary.Unassigned)
        {
            all.AddRange(Phrases.Select(f => (new Row(TerrainLibrary.Phrase, f.Id, f.Terrains.ToList(), f.Weight, "next to " + f.NeighborTerrain + ": " + Shorten(TerrainLibrary.PhrasePreview(f.Text))), (IReadOnlyList<string>)Array.Empty<string>())));
        }
        IEnumerable<(Row Row, IReadOnlyList<string> Tags)> q = all;
        if (Tab == TerrainLibrary.Unassigned) { q = q.Where(x => x.Row.Terrains.Count == 0); }
        if (TerrainFilter != null) { q = q.Where(x => x.Row.Terrains.Contains(TerrainFilter, StringComparer.Ordinal)); }
        if (TagFilter != null) { q = q.Where(x => x.Tags.Contains(TagFilter, StringComparer.Ordinal)); }
        return q.Select(x => x.Row).OrderBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
    }

    private static string Shorten(string? s)
    {
        s ??= string.Empty;
        return s.Length <= 80 ? s : s.Substring(0, 77) + "...";
    }
}
