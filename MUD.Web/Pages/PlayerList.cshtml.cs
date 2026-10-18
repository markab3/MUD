using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MUD.Web.Services;

namespace MUD.Web.Pages;

// /creator/players: read-only, sortable by name, name filter, 50 per page. Reads go through an inclusion projection in
// the store listing only display fields. Nothing from the query string is echoed except through Razor encoding.
[Authorize(Policy = WebPolicies.CreatorOrAdmin)]
public class PlayerListModel : PageModel
{
    public const int PageSize = 50;
    public const int MaxFilterLength = 64;
    public const string FailedError = "The player list could not be loaded. Please try again later.";

    private readonly IPlayerEditStore _store;

    public PlayerListModel(IPlayerEditStore store) { _store = store; }

    [BindProperty(SupportsGet = true, Name = "q")] public string? Filter { get; set; }
    [BindProperty(SupportsGet = true, Name = "sort")] public string? Sort { get; set; }
    [BindProperty(SupportsGet = true, Name = "page")] public int PageNumber { get; set; } = 1;

    public PlayerPage Result { get; private set; } = new(Array.Empty<PlayerSummary>(), 0, 1, PageSize);
    public bool Descending => string.Equals(Sort, "desc", StringComparison.OrdinalIgnoreCase);
    public string? Error { get; private set; }

    public static string EditUrl(string nameKey) => "/creator/players/" + Uri.EscapeDataString(nameKey);

    // The "online" column. The website is a separate process with no link to the game, and the spec defines none, so the
    // honest answer is always "unknown".
    public static string OnlineText(PlayerSummary _) => "unknown";

    public static string RoleText(AccountRole role) => role.ToString();

    public string PageUrl(int page, string? sort = null) =>
        "/creator/players?q=" + Uri.EscapeDataString(Filter ?? string.Empty) + "&sort=" + (sort ?? (Descending ? "desc" : "asc")) + "&page=" + page;

    public void OnGet()
    {
        Filter = (Filter ?? string.Empty).Trim();
        if (Filter.Length > MaxFilterLength) { Filter = Filter.Substring(0, MaxFilterLength); }
        if (PageNumber < 1) { PageNumber = 1; }
        try
        {
            Result = _store.List(Filter, Descending, PageNumber, PageSize);
            if (PageNumber > Result.PageCount)
            {
                PageNumber = Result.PageCount;
                Result = _store.List(Filter, Descending, PageNumber, PageSize);
            }
        }
        catch (Exception)
        {
            Error = FailedError;
        }
    }
}
