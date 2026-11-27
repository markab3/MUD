namespace MUD.Web.Services;

public record MapRect(string Key, string Short, double X, double Y, double W, double H, int GridX, int GridY,
    string? GenMap = null, string? GenTerrain = null);

// Points are in SVG coordinates. ArrowStart/ArrowEnd mark arrowheads at the first/last point.
// Warning = coordinates disagree with the exit direction (drawn dashed in a warning colour).
public record MapLine(string FromKey, string ToKey, IReadOnlyList<(double X, double Y)> Points,
    bool ArrowStart, bool ArrowEnd, bool Warning);

// Off-grid compass exit: short line leaving the square with the target key as label.
public record MapStub(string FromKey, string Direction, string Target, bool Missing,
    IReadOnlyList<(double X, double Y)> Points, double LabelX, double LabelY);

// Named non-compass exit that cannot be placed on the grid. Text is the exit name. (Up and down are MapArrows now.)
public record MapBadge(string RoomKey, string Text, string Target, bool Missing, double X, double Y);

public record MapMismatch(string FromKey, string Direction, string ToKey);

public record MapUnplaced(string Key, string Short);

// Expansion arrows around a room.
//   Go    black arrow: the room has an exit that way. Href is the target (edit page on this level, else the map of its level);
//         null when the target room does not exist.
//   Add   gray arrow: no exit and no room that way. Href opens Create room prefilled (area, CreateX/Y/Z); no exit is prefilled.
//   Wire  black x: no exit, but a room is there. Clicking it adds that one exit (Direction -> TargetKey). Href is null.
//   Level up/down inside the box. Always a map switch (Href), never a write. Black when the room has such an exit.
public enum MapArrowKind { Go, Add, Wire, Level }

public record MapArrow(string RoomKey, string Direction, MapArrowKind Kind, bool Black, string Glyph, double X, double Y, string Label,
    string? Href, string? TargetKey = null, int? CreateX = null, int? CreateY = null, int? CreateZ = null);

// Dashed placeholder at the origin's X,Y on a level that has no room there. Display only, never stored.
public record MapGhost(double X, double Y, double W, double H, int GridX, int GridY, int Z, string Area, string CreateHref);

public record RoomMap(IReadOnlyList<MapRect> Rects, IReadOnlyList<MapLine> Lines, IReadOnlyList<MapStub> Stubs,
    IReadOnlyList<MapBadge> Badges, IReadOnlyList<MapMismatch> Mismatches, IReadOnlyList<MapUnplaced> Unplaced,
    double Width, double Height)
{
    public IReadOnlyList<MapArrow> Arrows { get; init; } = Array.Empty<MapArrow>();
    public MapGhost? Ghost { get; init; }
}

// Pure layout: rooms in, primitive shapes out. NORTH IS UP: screen y = -Y (+x east, +z is the level selector).
public class RoomMapLayout
{
    public const double Size = 120;
    public const double Pitch = 190;
    public const double Margin = 70;
    public const double StubLength = 45;
    public const string Up = "\u2191";
    public const string Down = "\u2193";
    public const string WireGlyph = "\u00d7";
    public const string LevelUpGlyph = "\u21d1";
    public const string LevelDownGlyph = "\u21d3";
    public const string GhostText = "No room here";
    public const string GhostLink = "Create room here";

    private static readonly Dictionary<string, (int Dx, int Dy)> Compass = new(StringComparer.OrdinalIgnoreCase)
    {
        ["north"] = (0, 1), ["n"] = (0, 1),
        ["northeast"] = (1, 1), ["ne"] = (1, 1),
        ["east"] = (1, 0), ["e"] = (1, 0),
        ["southeast"] = (1, -1), ["se"] = (1, -1),
        ["south"] = (0, -1), ["s"] = (0, -1),
        ["southwest"] = (-1, -1), ["sw"] = (-1, -1),
        ["west"] = (-1, 0), ["w"] = (-1, 0),
        ["northwest"] = (-1, 1), ["nw"] = (-1, 1),
    };

    // The eight directions: the full-word exit name written when an exit is wired, the offset and the arrow glyph.
    public static readonly IReadOnlyList<(string Name, int Dx, int Dy, string Glyph)> Directions = new[]
    {
        ("north", 0, 1, "\u2191"), ("northeast", 1, 1, "\u2197"), ("east", 1, 0, "\u2192"), ("southeast", 1, -1, "\u2198"),
        ("south", 0, -1, "\u2193"), ("southwest", -1, -1, "\u2199"), ("west", -1, 0, "\u2190"), ("northwest", -1, 1, "\u2196"),
    };

    public static bool TryCompass(string name, out (int Dx, int Dy) v) => Compass.TryGetValue(name.Trim(), out v);

    public static bool IsUp(string n) => n.Trim().Equals("up", StringComparison.OrdinalIgnoreCase) || n.Trim().Equals("u", StringComparison.OrdinalIgnoreCase);
    public static bool IsDown(string n) => n.Trim().Equals("down", StringComparison.OrdinalIgnoreCase) || n.Trim().Equals("d", StringComparison.OrdinalIgnoreCase);

    public static string AreaOf(RoomRecord r) => r.Area ?? string.Empty;
    public static int LevelOf(RoomRecord r) => r.Z ?? 0;
    public static bool IsPlaced(RoomRecord r) => r.X.HasValue && r.Y.HasValue;

    // ghostAt: when the level has no room at that X,Y a dashed "No room here" square is added there (and the map makes room for it).
    public RoomMap Build(IReadOnlyList<RoomRecord> all, string area, int z, (int X, int Y)? ghostAt = null)
    {
        var byKey = new Dictionary<string, RoomRecord>(StringComparer.Ordinal);
        foreach (var r in all) { byKey[r.Key] = r; }

        var inArea = all.Where(r => AreaOf(r) == area).OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
        var unplaced = inArea.Where(r => !IsPlaced(r)).Select(r => new MapUnplaced(r.Key, r.Short)).ToList();
        var level = inArea.Where(r => IsPlaced(r) && LevelOf(r) == z).ToList();
        var onGrid = level.ToDictionary(r => r.Key, StringComparer.Ordinal);

        var showGhost = ghostAt.HasValue && !level.Any(r => r.X!.Value == ghostAt.Value.X && r.Y!.Value == ghostAt.Value.Y);
        var points = level.Select(r => (X: r.X!.Value, Y: r.Y!.Value)).ToList();
        if (showGhost) { points.Add(ghostAt!.Value); }

        if (points.Count == 0)
        {
            return new RoomMap(new List<MapRect>(), new List<MapLine>(), new List<MapStub>(), new List<MapBadge>(),
                new List<MapMismatch>(), unplaced, 2 * Margin, 2 * Margin);
        }

        var minX = points.Min(p => p.X);
        var maxY = points.Max(p => p.Y);
        var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y);

        // screen_y = -Y: the larger Y, the nearer the top.
        double Left(RoomRecord r) => Margin + (r.X!.Value - minX) * Pitch;
        double Top(RoomRecord r) => Margin + (maxY - r.Y!.Value) * Pitch;

        (double X, double Y) Anchor(RoomRecord r, int dx, int dy) =>
            (Left(r) + Size / 2 + dx * Size / 2, Top(r) + Size / 2 - dy * Size / 2);

        var rects = level.Select(r => new MapRect(r.Key, r.Short, Left(r), Top(r), Size, Size, r.X!.Value, r.Y!.Value, r.GenMap, r.GenTerrain)).ToList();
        var lines = new List<MapLine>();
        var stubs = new List<MapStub>();
        var badges = new List<MapBadge>();
        var mismatches = new List<MapMismatch>();
        var consumed = new HashSet<(string Room, int Index)>();

        foreach (var a in level)
        {
            var badgeIndex = 0;
            for (var i = 0; i < a.Exits.Count; i++)
            {
                if (consumed.Contains((a.Key, i))) { continue; }
                var exit = a.Exits[i];
                byKey.TryGetValue(exit.To, out var dest);
                var missing = dest == null;

                if (!TryCompass(exit.Name, out var dir))
                {
                    // Up and down are the arrows inside the square (BuildArrows); only named exits keep a badge.
                    if (IsUp(exit.Name) || IsDown(exit.Name)) { continue; }
                    badges.Add(new MapBadge(a.Key, exit.Name, exit.To, missing, Left(a), Top(a) + 62 + 15 * badgeIndex++));
                    continue;
                }

                if (dest == null || !onGrid.ContainsKey(dest.Key))
                {
                    // Missing room, other area, other level or unplaced: stub with the target key.
                    var s = Anchor(a, dir.Dx, dir.Dy);
                    var len = dir.Dx != 0 && dir.Dy != 0 ? StubLength * 0.7071 : StubLength;
                    var e = (X: s.X + dir.Dx * len, Y: s.Y - dir.Dy * len);
                    stubs.Add(new MapStub(a.Key, exit.Name, exit.To, missing, new[] { s, e }, e.X + dir.Dx * 4, e.Y - dir.Dy * 4));
                    continue;
                }

                var actualDx = Math.Sign(dest.X!.Value - a.X!.Value);
                var actualDy = Math.Sign(dest.Y!.Value - a.Y!.Value);
                var mismatch = actualDx != dir.Dx || actualDy != dir.Dy;
                if (mismatch) { mismatches.Add(new MapMismatch(a.Key, exit.Name, dest.Key)); }

                // Reverse exit: dest has a compass exit back to a in the opposite direction.
                var reverseIndex = -1;
                for (var j = 0; j < dest.Exits.Count; j++)
                {
                    if (consumed.Contains((dest.Key, j)) || dest.Exits[j].To != a.Key) { continue; }
                    if (TryCompass(dest.Exits[j].Name, out var rd) && rd.Dx == -dir.Dx && rd.Dy == -dir.Dy) { reverseIndex = j; break; }
                }
                if (reverseIndex >= 0 && ReferenceEquals(a, dest)) { reverseIndex = -1; }
                if (reverseIndex >= 0)
                {
                    consumed.Add((dest.Key, reverseIndex));
                    var rmis = Math.Sign(a.X!.Value - dest.X!.Value) != -dir.Dx || Math.Sign(a.Y!.Value - dest.Y!.Value) != -dir.Dy;
                    if (rmis) { mismatches.Add(new MapMismatch(dest.Key, dest.Exits[reverseIndex].Name, a.Key)); }
                    mismatch |= rmis;
                }

                // Start leaves a on the exit's side; end lands on the side of dest facing a (opposite of the
                // exit direction when coordinates agree, otherwise where the coordinates actually put a).
                var start = Anchor(a, dir.Dx, dir.Dy);
                var end = mismatch && (actualDx != 0 || actualDy != 0)
                    ? Anchor(dest, -actualDx, -actualDy)
                    : Anchor(dest, -dir.Dx, -dir.Dy);
                lines.Add(new MapLine(a.Key, dest.Key, new[] { start, end }, reverseIndex >= 0, true, mismatch));
            }
        }

        var width = Margin * 2 + (maxX - minX) * Pitch + Size;
        var height = Margin * 2 + (maxY - minY) * Pitch + Size;

        MapGhost? ghost = null;
        if (showGhost)
        {
            var g = ghostAt!.Value;
            ghost = new MapGhost(Margin + (g.X - minX) * Pitch, Margin + (maxY - g.Y) * Pitch, Size, Size, g.X, g.Y, z, area,
                WebRoutes.CreatorAt(area, g.X, g.Y, z));
        }
        var arrows = BuildArrows(all, byKey, level, onGrid, area, z, Left, Top);
        return new RoomMap(rects, lines, stubs, badges, mismatches, unplaced, width, height) { Arrows = arrows, Ghost = ghost };
    }

    // Eight compass arrows around every room plus up and down inside its box. A compass glyph sits in the gap just outside its side (or
    // corner), moved sideways along the edge so the line or stub that leaves the middle of the side stays visible.
    private static List<MapArrow> BuildArrows(IReadOnlyList<RoomRecord> all, Dictionary<string, RoomRecord> byKey, List<RoomRecord> level,
        Dictionary<string, RoomRecord> onGrid, string area, int z, Func<RoomRecord, double> left, Func<RoomRecord, double> top)
    {
        // Rule (b): rooms with a compass exit to a room. Key (target, dx, dy) = the direction the TARGET would use to point back.
        var incoming = new Dictionary<(string, int, int), RoomRecord>();
        foreach (var b in all.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            foreach (var e in b.Exits)
            {
                if (TryCompass(e.Name, out var v) && e.To != b.Key) { incoming.TryAdd((e.To, -v.Dx, -v.Dy), b); }
            }
        }
        // Rule (a): placed rooms of the same area and level at the adjacent coordinates.
        var atCoordinate = level.ToDictionary(r => (r.X!.Value, r.Y!.Value));
        var arrows = new List<MapArrow>();
        foreach (var a in level)
        {
            var cx = left(a) + Size / 2;
            var cy = top(a) + Size / 2;
            foreach (var (name, dx, dy, glyph) in Directions)
            {
                var diagonal = dx != 0 && dy != 0;
                var reach = Size / 2 + (diagonal ? 11 : 13);
                var shift = diagonal ? 0 : 24;
                // Sideways = along the edge, away from the middle where the line or stub leaves.
                var px = cx + dx * reach + (dy != 0 && !diagonal ? shift : 0);
                var py = cy - dy * reach + (dx != 0 && !diagonal ? shift : 0);
                var exit = a.Exits.FirstOrDefault(e => TryCompass(e.Name, out var v) && v.Dx == dx && v.Dy == dy);
                if (exit != null)
                {
                    byKey.TryGetValue(exit.To, out var target);
                    arrows.Add(new MapArrow(a.Key, name, MapArrowKind.Go, true, glyph, px, py,
                        "Go to " + (target?.Short ?? exit.To) + " (" + name + ")", GoHref(target, onGrid), exit.To));
                    continue;
                }
                RoomRecord? linked = atCoordinate.TryGetValue((a.X!.Value + dx, a.Y!.Value + dy), out var adjacent) && adjacent.Key != a.Key ? adjacent : null;
                if (linked == null && incoming.TryGetValue((a.Key, dx, dy), out var pointsHere)) { linked = pointsHere; }
                if (linked != null)
                {
                    arrows.Add(new MapArrow(a.Key, name, MapArrowKind.Wire, true, WireGlyph, px, py,
                        "Connect " + name + " of " + a.Short + " to " + linked.Short, null, linked.Key));
                    continue;
                }
                var nx = a.X!.Value + dx;
                var ny = a.Y!.Value + dy;
                arrows.Add(new MapArrow(a.Key, name, MapArrowKind.Add, false, glyph, px, py,
                    "Add room " + name + " of " + a.Short, WebRoutes.CreatorAt(AreaOf(a), nx, ny, z), null, nx, ny, z));
            }
            arrows.Add(LevelArrow(a, byKey, true, left(a) + 14, top(a) + 14, area, z));
            arrows.Add(LevelArrow(a, byKey, false, left(a) + Size - 14, top(a) + Size - 14, area, z));
        }
        return arrows;
    }

    // Black: the level of the exit's target room (its area too). Gray: Z+1 / Z-1 of the same area. Always a map switch, never a write.
    private static MapArrow LevelArrow(RoomRecord a, Dictionary<string, RoomRecord> byKey, bool up, double x, double y, string area, int z)
    {
        var exit = a.Exits.FirstOrDefault(e => up ? IsUp(e.Name) : IsDown(e.Name));
        var name = up ? "up" : "down";
        RoomRecord? target = null;
        if (exit != null) { byKey.TryGetValue(exit.To, out target); }
        var goesToTarget = target != null && IsPlaced(target);
        var destArea = goesToTarget ? AreaOf(target!) : area;
        var destZ = goesToTarget ? LevelOf(target!) : z + (up ? 1 : -1);
        var label = exit != null
            ? "Go " + name + " to level " + destZ + " from " + a.Short
            : "Show level " + destZ + " (" + name + ") from " + a.Short;
        return new MapArrow(a.Key, name, MapArrowKind.Level, exit != null, up ? LevelUpGlyph : LevelDownGlyph, x, y, label,
            WebRoutes.MapFor(destArea, destZ, a.X, a.Y), exit?.To);
    }

    // A black arrow leads to the target's edit page when it is on this map level (or cannot be placed on any), else to the map of its level.
    private static string? GoHref(RoomRecord? target, Dictionary<string, RoomRecord> onGrid)
    {
        if (target == null) { return null; }
        if (onGrid.ContainsKey(target.Key) || !IsPlaced(target)) { return WebRoutes.RoomEdit(target.Key); }
        return WebRoutes.MapFor(AreaOf(target), LevelOf(target), null, null, target.Key);
    }
}
