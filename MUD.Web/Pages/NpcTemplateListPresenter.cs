using MUD.Contracts;
using MUD.Web.Services;

namespace MUD.Web.Pages;

// One collapsible group of the NPC template list: an area with its templates.
public sealed record NpcTemplateGroup(string AreaId, string Name, string Description, IReadOnlyList<NpcTemplateRecord> Items);

// The NPC template list logic (/creator/npcs). A read-only placeholder: templates grouped by area, nothing to create, edit or delete yet
// (specs/NPC_TEMPLATES.md). Free of HttpContext and IActionResult (WEB_BLAZOR.md 4.1). Everything goes through IAdminClient.
public sealed class NpcTemplateListPresenter
{
    public const string UnavailableError = "The game is not reachable right now. Please try again later.";
    public const string ForbiddenError = "You are not allowed to do that.";
    public const string FailedError = "The NPC template request could not be completed. Please try again later.";
    public const string Notice = "NPC templates are not available yet. This page is a placeholder.";
    public const string Empty = "There are no NPC templates yet.";

    private readonly IAdminClient _core;
    private readonly string _token;
    private IReadOnlyList<AreaRecord> _areas = Array.Empty<AreaRecord>();
    private IReadOnlyList<NpcTemplateRecord> _items = Array.Empty<NpcTemplateRecord>();

    public NpcTemplateListPresenter(IAdminClient core, string token)
    {
        _core = core;
        _token = token;
    }

    public string? Error { get; private set; }
    public int Total => _items.Count;

    // Areas in id order; every area is listed (an empty one says so). A template whose area has no record gets a group named by its id.
    public IReadOnlyList<NpcTemplateGroup> Groups
    {
        get
        {
            var groups = new List<NpcTemplateGroup>();
            foreach (var area in _areas)
            {
                var items = _items.Where(i => i.Area == area.Id).ToList();
                groups.Add(new NpcTemplateGroup(area.Id, string.IsNullOrWhiteSpace(area.Name) ? area.Id : area.Name, area.Description, items));
            }
            var known = _areas.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var orphan in _items.Where(i => !known.Contains(i.Area)).GroupBy(i => i.Area, StringComparer.Ordinal))
            {
                groups.Add(new NpcTemplateGroup(orphan.Key, orphan.Key, string.Empty, orphan.ToList()));
            }
            return groups.OrderBy(g => g.AreaId, StringComparer.Ordinal).ToList();
        }
    }

    // True when the list loaded; false sets Error.
    public async Task<bool> LoadAsync(CancellationToken cancellation = default)
    {
        Error = null;
        try
        {
            _areas = (await _core.ListAreasAsync(_token, cancellation)).OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
            _items = (await _core.ListNpcTemplatesAsync(_token, null, cancellation)).OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
            return true;
        }
        catch (CoreUnavailableException) { Error = UnavailableError; }
        catch (CoreRefusedException) { Error = ForbiddenError; }
        catch (Exception) { Error = FailedError; }
        _areas = Array.Empty<AreaRecord>();
        _items = Array.Empty<NpcTemplateRecord>();
        return false;
    }
}
