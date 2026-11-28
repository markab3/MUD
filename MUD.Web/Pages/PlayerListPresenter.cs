using MUD.Contracts;
using MUD.Web.Services;

namespace MUD.Web.Pages;

public enum PlayerListOutcome { Ok, Failed }

// The player list logic (moved from PlayerListModel), free of HttpContext. Read-only, sortable by name, name filter, 50 per page.
// Core filters, sorts and pages the list through its Admin service and sends display fields only.
public sealed class PlayerListPresenter
{
    public const int PageSize = 50;
    public const int MaxFilterLength = 64;

    private readonly IAdminClient _core;
    private readonly string _token;

    public PlayerListPresenter(IAdminClient core, string token)
    {
        _core = core;
        _token = token;
    }

    public string Filter { get; private set; } = string.Empty;
    public bool Descending { get; private set; }
    public IReadOnlyList<PlayerSummary> Items { get; private set; } = Array.Empty<PlayerSummary>();
    public int Total { get; private set; }
    public int Page { get; private set; } = 1;
    public int PageCount => PageCountFor(Total);
    public string? Error { get; private set; }

    public static string EditUrl(string nameKey) => WebRoutes.PlayerEdit(nameKey);

    // The "online" column. Core's player list carries no online flag, so the honest answer is always "unknown".
    public static string OnlineText(PlayerSummary _) => "unknown";

    public static string RoleText(AdminRole role) => SessionAuthentication.RoleFrom(role).ToString();

    public static string StatusText(PlayerSummary p) => p.Retired ? "Retired" : "Active";

    // Same address as the page with the given paging; the query string is the only state of the page.
    public static string PageUrl(string? filter, bool descending, int page) =>
        WebRoutes.Players + "?q=" + Uri.EscapeDataString(filter ?? string.Empty) + "&sort=" + (descending ? "desc" : "asc") + "&page=" + page;

    // The state a list address stands for (cleaned filter, sort, page). The page compares it with the address a live filter just wrote, so that
    // address coming back as a navigation does not reload the list a second time.
    public static string StateKey(string? filter, string? sort, int? page) =>
        Clean(filter) + "|" + (string.Equals(sort, "desc", StringComparison.OrdinalIgnoreCase) ? "desc" : "asc") + "|" + Math.Max(1, page ?? 1);

    public static string Clean(string? filter) => LiveFilterRunner.Clean(filter, MaxFilterLength);

    public string UrlFor(int page, bool? descending = null) => PageUrl(Filter, descending ?? Descending, page);

    public async Task<PlayerListOutcome> LoadAsync(string? filter, string? sort, int page, CancellationToken cancellation = default)
    {
        Error = null;
        Filter = (filter ?? string.Empty).Trim();
        if (Filter.Length > MaxFilterLength) { Filter = Filter.Substring(0, MaxFilterLength); }
        Descending = string.Equals(sort, "desc", StringComparison.OrdinalIgnoreCase);
        if (page < 1) { page = 1; }
        try
        {
            var reply = await _core.ListPlayersAsync(_token, Filter, Descending, page, PageSize, cancellation);
            if (page > PageCountFor(reply.Total))
            {
                // A page past the end shows the last page.
                page = PageCountFor(reply.Total);
                reply = await _core.ListPlayersAsync(_token, Filter, Descending, page, PageSize, cancellation);
            }
            Items = reply.Items.ToList();
            Total = reply.Total;
            Page = page;
            return PlayerListOutcome.Ok;
        }
        catch (CoreUnavailableException) { Error = Messages.Unavailable; }
        catch (CoreRefusedException) { Error = Messages.PlayerListForbidden; }
        catch (Exception) { Error = Messages.PlayerListFailed; }
        Items = Array.Empty<PlayerSummary>();
        return PlayerListOutcome.Failed;
    }

    private static int PageCountFor(int total) => Math.Max(1, (total + PageSize - 1) / PageSize);
}
