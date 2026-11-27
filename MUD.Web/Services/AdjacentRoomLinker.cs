namespace MUD.Web.Services;

/// <summary>A direction skipped because two or more rooms share the neighbouring coordinates.</summary>
public sealed record AdjacentConflict(string Direction, IReadOnlyList<string> Keys);

/// <summary>What "Link Adjacent Rooms" would add to the form: new exits, and directions skipped because of duplicate coordinates.</summary>
public sealed record AdjacentPlan(IReadOnlyList<RoomExitRecord> Add, IReadOnlyList<AdjacentConflict> Conflicts);

// The rule behind the room edit page's "Link Adjacent Rooms" button. Pure: rooms and the form's current exit names in, the exits to add out.
// Only the 8 compass directions at the same Z (up and down are left out on purpose). North is +Y and east is +X, as in RoomMapLayout.
// It never changes the neighbours and never replaces an exit the form already has.
public static class AdjacentRoomLinker
{
    public static readonly string[] Directions =
        { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" };

    public static AdjacentPlan Plan(string roomKey, string? area, int? x, int? y, int? z,
        IEnumerable<RoomRecord> rooms, IEnumerable<string?> existingDirections)
    {
        var add = new List<RoomExitRecord>();
        var conflicts = new List<AdjacentConflict>();
        // An unplaced room has no neighbours.
        if (!x.HasValue || !y.HasValue) { return new AdjacentPlan(add, conflicts); }

        // A direction counts as present under any spelling the map understands (north, n, ...), so "n" blocks "north".
        var have = new HashSet<(int, int)>();
        foreach (var d in existingDirections)
        {
            if (RoomMapLayout.TryCompass(d ?? string.Empty, out var v)) { have.Add(v); }
        }

        var level = z ?? 0;
        var areaName = area ?? string.Empty;
        var candidates = rooms
            .Where(r => r.Key != roomKey && RoomMapLayout.AreaOf(r) == areaName && RoomMapLayout.IsPlaced(r) && RoomMapLayout.LevelOf(r) == level)
            .ToList();

        foreach (var direction in Directions)
        {
            if (!RoomMapLayout.TryCompass(direction, out var v) || have.Contains(v)) { continue; }
            var found = candidates
                .Where(r => r.X!.Value == x.Value + v.Dx && r.Y!.Value == y.Value + v.Dy)
                .Select(r => r.Key)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();
            if (found.Count == 1) { add.Add(new RoomExitRecord(direction, found[0])); }
            else if (found.Count > 1) { conflicts.Add(new AdjacentConflict(direction, found)); }
        }
        return new AdjacentPlan(add, conflicts);
    }
}
