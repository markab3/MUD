using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MUD.Web.Services;

namespace MUD.Web.Pages;

// Read-only room map. Nothing here writes to the database.
[Authorize(Policy = WebPolicies.CreatorOrAdmin)]
public class CreatorMapModel : PageModel
{
    private readonly IRoomReader _rooms;

    public CreatorMapModel(IRoomReader rooms) => _rooms = rooms;

    public IReadOnlyList<string> Areas { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<int> Levels { get; private set; } = Array.Empty<int>();
    public string? Area { get; private set; }
    public int Z { get; private set; }
    public RoomMap? Map { get; private set; }

    public void OnGet(string? area, int? z)
    {
        var all = _rooms.GetAll();
        Areas = all.Select(RoomMapLayout.AreaOf).Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal).ToList();
        if (Areas.Count == 0) { return; }

        Area = area != null && Areas.Contains(area) ? area : Areas[0];
        Levels = all.Where(r => RoomMapLayout.AreaOf(r) == Area && RoomMapLayout.IsPlaced(r))
            .Select(RoomMapLayout.LevelOf).Distinct().OrderBy(l => l).ToList();
        Z = z.HasValue ? z.Value : Levels.Count == 0 || Levels.Contains(0) ? 0 : Levels[0];
        Map = new RoomMapLayout().Build(all, Area, Z);
    }

    public static string RoomUrl(string key) => "/creator/rooms/" + Uri.EscapeDataString(key);
}
