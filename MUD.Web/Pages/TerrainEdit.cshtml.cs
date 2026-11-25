using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MUD.Contracts;
using MUD.Web.Services;

namespace MUD.Web.Pages;

// /creator/terrain/edit?kind=fragment|feature|phrase[&id=...]: create (no id) or edit one library entity. The kind and the original id come
// from the query string, never from the form (so a form cannot change which entity is written). Core validates every field and owns the
// feature version. Empty terrains is saved as is: the entity is then used by no generator, and the page says so.
[Authorize(Policy = WebPolicies.CreatorOrAdmin)]
public class TerrainEditModel : PageModel
{
    private readonly IAdminClient _core;

    public TerrainEditModel(IAdminClient core) { _core = core; }

    [BindProperty] public string? Id { get; set; }
    [BindProperty] public List<string> Terrains { get; set; } = new();
    [BindProperty] public string? ExtraTerrains { get; set; }
    [BindProperty] public string? Slot { get; set; }
    [BindProperty] public string? Text { get; set; }
    [BindProperty] public int Weight { get; set; } = 1;
    [BindProperty] public string? Tags { get; set; }
    [BindProperty] public string? Short { get; set; }
    [BindProperty] public string? Long { get; set; }
    [BindProperty] public string? RoomLine { get; set; }
    [BindProperty] public string? Detail { get; set; }
    [BindProperty] public string? Handles { get; set; }
    [BindProperty] public string? Adjectives { get; set; }
    [BindProperty] public double Chance { get; set; } = 1.0;
    [BindProperty] public string? NeighborTerrain { get; set; }
    [BindProperty] public bool Merge { get; set; }

    public string? Kind { get; private set; }
    public bool IsNew { get; private set; }
    public bool Missing { get; private set; }
    public int Version { get; private set; }
    public IReadOnlyList<string> TerrainChoices { get; private set; } = Array.Empty<string>();
    public string? Error { get; private set; }
    public string? Notice { get; private set; }

    // True when nothing is ticked and nothing typed: the entity would be used by no generator.
    public bool Unused => Terrains.Count == 0 && TerrainLibrary.Split(ExtraTerrains).Count == 0;

    public string VersionHint => IsNew
        ? "A new template starts at version 1."
        : "Saving a change to the short, long, room line, detail, handles or adjectives raises the version from " + Version + " to " + (Version + 1) + ".";

    private string Token => SessionAuthentication.TokenFrom(Request);

    public async Task<IActionResult> OnGetAsync(string? kind, string? id)
    {
        if (TempData?["Notice"] is string carried) { Notice = carried; }
        if (!Accept(kind)) { return NotFoundPage(); }
        IsNew = string.IsNullOrEmpty(id);
        try
        {
            await LoadChoices();
            if (IsNew) { return Page(); }
            var ct = HttpContext.RequestAborted;
            switch (Kind)
            {
                case TerrainLibrary.Fragment:
                    var f = await _core.GetFragmentAsync(Token, id!, ct);
                    if (f == null) { return NotFoundPage(); }
                    Id = f.Id; Terrains = f.Terrains.ToList(); Slot = f.Slot; Text = f.Text; Weight = f.Weight; Tags = TerrainLibrary.Join(f.Tags);
                    break;
                case TerrainLibrary.Feature:
                    var t = await _core.GetFeatureTemplateAsync(Token, id!, ct);
                    if (t == null) { return NotFoundPage(); }
                    Id = t.Id; Terrains = t.Terrains.ToList(); Short = t.Short; Long = t.Long; RoomLine = t.RoomLine; Detail = t.Detail;
                    Handles = TerrainLibrary.Join(t.Handles); Adjectives = TerrainLibrary.Join(t.Adjectives); Weight = t.Weight; Chance = t.Chance;
                    Tags = TerrainLibrary.Join(t.Tags); Version = t.Version;
                    break;
                default:
                    var p = await _core.GetNeighborPhraseAsync(Token, id!, ct);
                    if (p == null) { return NotFoundPage(); }
                    Id = p.Id; Terrains = p.Terrains.ToList(); NeighborTerrain = p.NeighborTerrain; Text = p.Text; Weight = p.Weight; Merge = p.Merge;
                    break;
            }
            await LoadChoices();
            return Page();
        }
        catch (CoreUnavailableException) { return Fail(TerrainLibrary.UnavailableError, StatusCodes.Status503ServiceUnavailable); }
        catch (CoreRefusedException) { return Fail(TerrainLibrary.ForbiddenError, StatusCodes.Status403Forbidden); }
        catch (Exception) { return Fail(TerrainLibrary.FailedError, StatusCodes.Status500InternalServerError); }
    }

    public async Task<IActionResult> OnPostSaveAsync(string? kind, string? id)
    {
        if (!Accept(kind)) { return NotFoundPage(); }
        IsNew = string.IsNullOrEmpty(id);
        // An edit always writes the id from the query, never one from the form.
        if (!IsNew) { Id = id; }
        Id = (Id ?? string.Empty).Trim();
        var terrains = Terrains.Select(t => t?.Trim() ?? string.Empty).Where(t => t.Length > 0)
            .Concat(TerrainLibrary.Split(ExtraTerrains)).Distinct(StringComparer.Ordinal).ToList();
        try
        {
            var ct = HttpContext.RequestAborted;
            var token = Token;
            LibraryEditReply reply;
            switch (Kind)
            {
                case TerrainLibrary.Fragment:
                    var f = new FragmentRecord { Id = Id, Slot = (Slot ?? string.Empty).Trim(), Text = Text ?? string.Empty, Weight = Weight };
                    f.Terrains.AddRange(terrains); f.Tags.AddRange(TerrainLibrary.Split(Tags));
                    reply = IsNew ? await _core.CreateFragmentAsync(token, f, ct) : await _core.UpdateFragmentAsync(token, f, ct);
                    break;
                case TerrainLibrary.Feature:
                    var t = new FeatureTemplateRecord
                    {
                        Id = Id, Short = Short ?? string.Empty, Long = Long ?? string.Empty, RoomLine = RoomLine ?? string.Empty,
                        Detail = Detail ?? string.Empty, Weight = Weight, Chance = Chance,
                    };
                    t.Terrains.AddRange(terrains); t.Handles.AddRange(TerrainLibrary.Split(Handles)); t.Adjectives.AddRange(TerrainLibrary.Split(Adjectives));
                    t.Tags.AddRange(TerrainLibrary.Split(Tags));
                    reply = IsNew ? await _core.CreateFeatureTemplateAsync(token, t, ct) : await _core.UpdateFeatureTemplateAsync(token, t, ct);
                    break;
                default:
                    var p = new NeighborPhraseRecord
                    {
                        Id = Id, NeighborTerrain = (NeighborTerrain ?? string.Empty).Trim(), Text = Text ?? string.Empty, Weight = Weight, Merge = Merge,
                    };
                    p.Terrains.AddRange(terrains);
                    reply = IsNew ? await _core.CreateNeighborPhraseAsync(token, p, ct) : await _core.UpdateNeighborPhraseAsync(token, p, ct);
                    break;
            }
            Terrains = terrains; ExtraTerrains = null;
            switch (reply.Result)
            {
                case AdminResult.Ok:
                    TempData["Notice"] = "Saved.";
                    return Redirect(TerrainListModel.EditUrl(Kind!, Id));
                case AdminResult.NoChange:
                    Notice = "Nothing was changed."; ModelState.Clear(); await TryLoadChoices(); return Page();
                case AdminResult.Invalid:
                    foreach (var e in reply.Errors) { ModelState.AddModelError(string.Empty, TerrainLibrary.ErrorText(e)); }
                    await TryLoadChoices();
                    return Page();
                case AdminResult.AlreadyExists:
                    Error = "An entry with that id already exists."; await TryLoadChoices(); return Page();
                case AdminResult.NotFound:
                    return NotFoundPage();
                case AdminResult.Refused: Error = TerrainLibrary.RefusedError; await TryLoadChoices(); return Page();
                case AdminResult.AuditFailed: Error = TerrainLibrary.AuditFailedError; await TryLoadChoices(); return Page();
                case AdminResult.Forbidden: Error = TerrainLibrary.ForbiddenError; await TryLoadChoices(); return Page();
                default: Error = TerrainLibrary.FailedError; await TryLoadChoices(); return Page();
            }
        }
        catch (CoreUnavailableException) { return Fail(TerrainLibrary.UnavailableError, StatusCodes.Status503ServiceUnavailable); }
        catch (CoreRefusedException) { return Fail(TerrainLibrary.ForbiddenError, StatusCodes.Status403Forbidden); }
        catch (Exception) { return Fail(TerrainLibrary.FailedError, StatusCodes.Status500InternalServerError); }
    }

    private bool Accept(string? kind)
    {
        if (!TerrainLibrary.IsKind(kind)) { return false; }
        Kind = kind;
        return true;
    }

    private IActionResult NotFoundPage()
    {
        Missing = true;
        Error = "That library entry does not exist.";
        Response.StatusCode = StatusCodes.Status404NotFound;
        return Page();
    }

    private IActionResult Fail(string error, int status)
    {
        Error = error;
        Response.StatusCode = status;
        return Page();
    }

    private async Task TryLoadChoices()
    {
        try { await LoadChoices(); } catch (Exception) { TerrainChoices = Terrains.OrderBy(t => t, StringComparer.Ordinal).ToList(); }
    }

    // Known terrains: every id the library already uses, plus any already ticked on this form.
    private async Task LoadChoices()
    {
        var ct = HttpContext.RequestAborted;
        var token = Token;
        var known = TerrainLibrary.KnownTerrains(await _core.ListFragmentsAsync(token, ct), await _core.ListFeatureTemplatesAsync(token, ct), await _core.ListNeighborPhrasesAsync(token, ct));
        TerrainChoices = known.Concat(Terrains).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList();
    }
}
