using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MUD.Contracts;
using MUD.Web.Services;

namespace MUD.Web.Pages;

// /creator/players: read-only, sortable by name, name filter, 50 per page. Core filters, sorts and pages the list through its Admin
// service and sends display fields only. Nothing from the query string is echoed except through Razor encoding.
[Authorize(Policy = WebPolicies.CreatorOrAdmin)]
public class PlayerListModel : PageModel
{
    public const int PageSize = 50;
    public const int MaxFilterLength = 64;
    public const string FailedError = "The player list could not be loaded. Please try again later.";
    public const string UnavailableError = "The game is not reachable right now. Please try again later.";
    public const string ForbiddenError = "You are not allowed to list players.";

    private readonly IAdminClient _core;

    public PlayerListModel(IAdminClient core) { _core = core; }

    [BindProperty(SupportsGet = true, Name = "q")] public string? Filter { get; set; }
    [BindProperty(SupportsGet = true, Name = "sort")] public string? Sort { get; set; }
    [BindProperty(SupportsGet = true, Name = "page")] public int PageNumber { get; set; } = 1;

    public IReadOnlyList<PlayerSummary> Items { get; private set; } = Array.Empty<PlayerSummary>();
    public int Total { get; private set; }
    public int Page { get; private set; } = 1;
    public int PageCount => PageCountFor(Total);
    public bool Descending => string.Equals(Sort, "desc", StringComparison.OrdinalIgnoreCase);
    public string? Error { get; private set; }

    private string Token => SessionAuthentication.TokenFrom(Request);

    public static string EditUrl(string nameKey) => "/creator/players/" + Uri.EscapeDataString(nameKey);

    // The "online" column. Core's player list carries no online flag, so the honest answer is always "unknown".
    public static string OnlineText(PlayerSummary _) => "unknown";

    public static string RoleText(AdminRole role) => SessionAuthentication.RoleFrom(role).ToString();

    public string PageUrl(int page, string? sort = null) =>
        "/creator/players?q=" + Uri.EscapeDataString(Filter ?? string.Empty) + "&sort=" + (sort ?? (Descending ? "desc" : "asc")) + "&page=" + page;

    public async Task OnGetAsync()
    {
        Filter = (Filter ?? string.Empty).Trim();
        if (Filter.Length > MaxFilterLength) { Filter = Filter.Substring(0, MaxFilterLength); }
        if (PageNumber < 1) { PageNumber = 1; }
        try
        {
            var reply = await _core.ListPlayersAsync(Token, Filter, Descending, PageNumber, PageSize, HttpContext.RequestAborted);
            if (PageNumber > PageCountFor(reply.Total))
            {
                // A page past the end shows the last page.
                PageNumber = PageCountFor(reply.Total);
                reply = await _core.ListPlayersAsync(Token, Filter, Descending, PageNumber, PageSize, HttpContext.RequestAborted);
            }
            Items = reply.Items.ToList();
            Total = reply.Total;
            Page = PageNumber;
        }
        catch (CoreUnavailableException) { Error = UnavailableError; }
        catch (CoreRefusedException) { Error = ForbiddenError; }
        catch (Exception) { Error = FailedError; }
    }

    private static int PageCountFor(int total) => Math.Max(1, (total + PageSize - 1) / PageSize);
}
