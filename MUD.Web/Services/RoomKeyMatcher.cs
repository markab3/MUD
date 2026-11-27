namespace MUD.Web.Services;

// Matching and ranking behind the room key autocomplete (WEB_BLAZOR.md 4.4). Plain functions over the room list the pages already load.
public static class RoomKeyMatcher
{
    public const int MaxResults = 8;

    // Rooms whose key (any part) or short description contains the query, case-insensitive, at most max, best first:
    // 0 the key starts with the query, 1 the key contains it, 2 only the short description contains it; ties by shorter key, then key.
    // A blank query matches nothing. With a scope (a key prefix such as "area."), only rooms under it are candidates and the
    // query is matched against the part of the key after the prefix (the create form types only that part).
    public static IReadOnlyList<RoomRecord> Match(IEnumerable<RoomRecord>? rooms, string? query, int max = MaxResults, string? scope = null)
    {
        var q = (query ?? string.Empty).Trim();
        if (rooms == null || q.Length == 0 || max <= 0) { return Array.Empty<RoomRecord>(); }
        var ranked = new List<(int Rank, RoomRecord Room)>();
        foreach (var room in rooms)
        {
            if (room == null || string.IsNullOrEmpty(room.Key)) { continue; }
            var key = room.Key;
            if (!string.IsNullOrEmpty(scope))
            {
                if (!key.StartsWith(scope, StringComparison.Ordinal)) { continue; }
                key = key.Substring(scope.Length);
            }
            int rank;
            if (key.StartsWith(q, StringComparison.OrdinalIgnoreCase)) { rank = 0; }
            else if (key.Contains(q, StringComparison.OrdinalIgnoreCase)) { rank = 1; }
            else if ((room.Short ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase)) { rank = 2; }
            else { continue; }
            ranked.Add((rank, room));
        }
        return ranked
            .OrderBy(r => r.Rank).ThenBy(r => r.Room.Key.Length).ThenBy(r => r.Room.Key, StringComparer.Ordinal)
            .Take(max).Select(r => r.Room).ToList();
    }

    // True when a room has exactly this key (after trimming).
    public static bool Exists(IEnumerable<RoomRecord>? rooms, string? key)
    {
        var k = (key ?? string.Empty).Trim();
        return k.Length > 0 && rooms != null && rooms.Any(r => r != null && string.Equals(r.Key, k, StringComparison.Ordinal));
    }

    // The area shown beside a result: the room's Area, else the key prefix before the last dot.
    public static string AreaOf(RoomRecord room)
    {
        if (!string.IsNullOrEmpty(room.Area)) { return room.Area!; }
        var dot = room.Key.LastIndexOf('.');
        return dot > 0 ? room.Key.Substring(0, dot) : string.Empty;
    }
}
