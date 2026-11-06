using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MUD.Web.Services;

namespace MUD.Web.Pages;

// Read-only room map, laid out here from the rooms core lists (ListRooms). Nothing here writes anything.
[Authorize(Policy = WebPolicies.CreatorOrAdmin)]
public class CreatorMapModel : PageModel
{
    public const string UnavailableError = "The game is not reachable right now. Please try again later.";

    private readonly IAdminClient _core;

    public CreatorMapModel(IAdminClient core) => _core = core;

    public IReadOnlyList<string> Areas { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<int> Levels { get; private set; } = Array.Empty<int>();
    public string? Area { get; private set; }
    public int Z { get; private set; }
    public RoomMap? Map { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? area, int? z)
    {
        IReadOnlyList<RoomRecord> all;
        try
        {
            all = (await _core.ListRoomsAsync(SessionAuthentication.TokenFrom(Request), HttpContext.RequestAborted)).Select(RoomRecord.From).ToList();
        }
        catch (CoreUnavailableException)
        {
            Error = UnavailableError;
            return Page();
        }
        catch (CoreRefusedException)
        {
            // The session ended or the role was lowered since the request was authorised (D31).
            return Redirect("/access-denied");
        }
        Areas = all.Select(RoomMapLayout.AreaOf).Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal).ToList();
        if (Areas.Count == 0) { return Page(); }

        Area = area != null && Areas.Contains(area) ? area : Areas[0];
        Levels = all.Where(r => RoomMapLayout.AreaOf(r) == Area && RoomMapLayout.IsPlaced(r))
            .Select(RoomMapLayout.LevelOf).Distinct().OrderBy(l => l).ToList();
        Z = z.HasValue ? z.Value : Levels.Count == 0 || Levels.Contains(0) ? 0 : Levels[0];
        Map = new RoomMapLayout().Build(all, Area, Z);
        return Page();
    }

    public static string RoomUrl(string key) => "/creator/rooms/" + Uri.EscapeDataString(key);
}
