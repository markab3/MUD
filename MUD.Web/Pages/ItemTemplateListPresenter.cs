using MUD.Contracts;
using MUD.Web.Services;

namespace MUD.Web.Pages;

public enum ItemTemplateOutcome { Ok, Failed }

// One collapsible group of the item template list: an area with its templates.
public sealed record ItemTemplateGroup(string AreaId, string Name, string Description, IReadOnlyList<ItemTemplateRecord> Items);

// The item template list logic (/creator/items): templates grouped by area, a text filter and delete. Free of HttpContext and IActionResult
// (WEB_BLAZOR.md 4.1). Everything goes through IAdminClient; core enforces the role and the delete rules.
public sealed class ItemTemplateListPresenter
{
    public const string UnavailableError = "The game is not reachable right now. Please try again later.";
    public const string ForbiddenError = "You are not allowed to do that.";
    public const string FailedError = "The item template request could not be completed. Please try again later.";
    public const string RefusedError = "The item template library is not available on the game right now.";
    public const string AuditFailedError = "The change was not saved because it could not be recorded. Please try again later.";
    public const string GoneError = "That item template does not exist.";
    public const string DeleteConfirm = "Delete this item template? Anything that copied it keeps its copy.";
    public const string Help = "Item templates are reusable item definitions, organised by area. The id is the area prefix and a local key, like a room key, and cannot be changed once the template exists.";

    private readonly IAdminClient _core;
    private readonly string _token;
    private IReadOnlyList<AreaRecord> _areas = Array.Empty<AreaRecord>();
    private IReadOnlyList<ItemTemplateRecord> _items = Array.Empty<ItemTemplateRecord>();

    public ItemTemplateListPresenter(IAdminClient core, string token)
    {
        _core = core;
        _token = token;
    }

    public string? Error { get; private set; }
    public string? Notice { get; set; }

    // Case-insensitive text matched against the id, short, handles and adjectives. Empty = no filter.
    public string Filter { get; set; } = string.Empty;
    public bool Filtering => !string.IsNullOrWhiteSpace(Filter);
    public int Total => _items.Count;

    // ?area=<prefix> (the areas hub links here): only that area's group is listed. Empty = every area.
    public string? AreaFilter { get; set; }
    public bool AreaFiltering => !string.IsNullOrWhiteSpace(AreaFilter);

    // Templates that match the filter now (the live result count).
    public int MatchCount => _items.Count(i => Matches(i) && (!AreaFiltering || i.Area == AreaFilter));

    // Areas in id order. Without a filter every area is listed (an empty one says so); with a filter only the groups that match. A template whose
    // area has no record gets a group named by its id.
    public IReadOnlyList<ItemTemplateGroup> Groups
    {
        get
        {
            var matching = _items.Where(Matches).ToList();
            var groups = new List<ItemTemplateGroup>();
            foreach (var area in _areas)
            {
                var items = matching.Where(i => i.Area == area.Id).ToList();
                if (items.Count > 0 || !Filtering) { groups.Add(new ItemTemplateGroup(area.Id, string.IsNullOrWhiteSpace(area.Name) ? area.Id : area.Name, area.Description, items)); }
            }
            var known = _areas.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var orphan in matching.Where(i => !known.Contains(i.Area)).GroupBy(i => i.Area, StringComparer.Ordinal))
            {
                groups.Add(new ItemTemplateGroup(orphan.Key, orphan.Key, string.Empty, orphan.ToList()));
            }
            return groups.Where(g => !AreaFiltering || g.AreaId == AreaFilter).OrderBy(g => g.AreaId, StringComparer.Ordinal).ToList();
        }
    }

    public async Task<ItemTemplateOutcome> LoadAsync(CancellationToken cancellation = default)
    {
        Error = null;
        try
        {
            _areas = (await _core.ListAreasAsync(_token, cancellation)).OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
            _items = (await _core.ListItemTemplatesAsync(_token, null, cancellation)).OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
            return ItemTemplateOutcome.Ok;
        }
        catch (CoreUnavailableException) { Error = UnavailableError; }
        catch (CoreRefusedException) { Error = ForbiddenError; }
        catch (Exception) { Error = FailedError; }
        _areas = Array.Empty<AreaRecord>();
        _items = Array.Empty<ItemTemplateRecord>();
        return ItemTemplateOutcome.Failed;
    }

    private bool Matches(ItemTemplateRecord item)
    {
        if (!Filtering) { return true; }
        var needle = Filter.Trim();
        return Contains(item.Id, needle) || Contains(item.Short, needle) || item.Handles.Any(h => Contains(h, needle)) || item.Adjectives.Any(a => Contains(a, needle));
    }

    private static bool Contains(string? text, string needle) => (text ?? string.Empty).Contains(needle, StringComparison.OrdinalIgnoreCase);

    // Deletes one template. The caller reloads afterwards. A template that is already gone counts as done.
    public async Task<ItemTemplateOutcome> DeleteAsync(string? id, CancellationToken cancellation = default)
    {
        Error = null;
        Notice = null;
        try
        {
            var reply = await _core.DeleteItemTemplateAsync(_token, id ?? string.Empty, cancellation);
            var message = string.IsNullOrWhiteSpace(reply.Message) ? null : reply.Message;
            switch (reply.Result)
            {
                case AdminResult.Ok: Notice = "Item template deleted."; return ItemTemplateOutcome.Ok;
                case AdminResult.NotFound: Notice = GoneError; return ItemTemplateOutcome.Ok;
                case AdminResult.Refused: Error = message ?? RefusedError; break;
                case AdminResult.AuditFailed: Error = AuditFailedError; break;
                case AdminResult.Forbidden: Error = ForbiddenError; break;
                default: Error = FailedError; break;
            }
        }
        catch (CoreUnavailableException) { Error = UnavailableError; }
        catch (CoreRefusedException) { Error = ForbiddenError; }
        catch (Exception) { Error = FailedError; }
        return ItemTemplateOutcome.Failed;
    }
}
