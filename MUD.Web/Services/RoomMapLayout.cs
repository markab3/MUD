namespace MUD.Web.Services;

public record MapRect(string Key, string Short, double X, double Y, double W, double H, int GridX, int GridY,
    string? GenMap = null, string? GenTerrain = null);

// Points are in SVG coordinates. ArrowStart/ArrowEnd mark arrowheads at the first/last point.
// Warning = coordinates disagree with the exit direction (drawn dashed in a warning colour).
public record MapLine(string FromKey, string ToKey, IReadOnlyList<(double X, double Y)> Points,
    bool ArrowStart, bool ArrowEnd, bool Warning)
{
    // The two click zones of the line: first the one at Points[0], then the one at the last point. Empty for a Warning line
    // (removing or adding there is ambiguous).
    public IReadOnlyList<MapLineEnd> Ends { get; init; } = Array.Empty<MapLineEnd>();
}

// One write the map can make. Remove: delete the exit named Direction (matched on name AND target) from OwnerKey.
// Otherwise add the exit Direction (a full-word compass name) from OwnerKey to TargetKey. Never touches any other room.
public record MapExitAction(bool Remove, string OwnerKey, string Direction, string TargetKey, string Label);

// Half of a line, from one end to the midpoint. The action is on the exit that leads TO the room at that end: with an arrowhead
// there the click removes it, without one it adds it (from the other room).
public record MapLineEnd(MapExitAction Action, IReadOnlyList<(double X, double Y)> Zone);

// Off-grid compass exit: short line leaving the square with the target key as label.
public record MapStub(string FromKey, string Direction, string Target, bool Missing,
    IReadOnlyList<(double X, double Y)> Points, double LabelX, double LabelY);

// Named non-compass exit that cannot be placed on the grid. Text is the exit name. (Up and down are MapArrows now.)
public record MapBadge(string RoomKey, string Text, string Target, bool Missing, double X, double Y);

public record MapMismatch(string FromKey, string Direction, string ToKey);

public record MapUnplaced(string Key, string Short);

// Expansion arrows around a room, drawn as SVG triangles (RoomMapLayout.ArrowPath rotated by Angle degrees) as big as a line's arrowhead.
//   Go    black arrow: the room has an exit that way which is NOT drawn as a line (a stub: target off the grid, on another level or
//         area, or missing). A drawn line shows its own arrowheads and gets no Go arrow. Href is the target (edit page on this level,
//         else the map of its level); null when the target room does not exist.
//   Add   gray arrow: no exit and no room that way. Href opens Create room prefilled (area, CreateX/Y/Z); no exit is prefilled.
//   Link  gray arrow: no exit, but a placed room of the same area and level is there (TargetKey). Clicking adds that one exit
//         (Direction -> TargetKey) from this room. Href is null.
//   Level up/down inside the box. Always a map switch (Href), never a write. Black when the room has such an exit.
public enum MapArrowKind { Go, Add, Link, Level }

public record MapArrow(string RoomKey, string Direction, MapArrowKind Kind, bool Black, double Angle, double X, double Y, string Label,
    string? Href, string? TargetKey = null, int? CreateX = null, int? CreateY = null, int? CreateZ = null)
{
    public MapExitAction? Action => Kind == MapArrowKind.Link && TargetKey != null ? new MapExitAction(false, RoomKey, Direction, TargetKey, Label) : null;
}

// Dashed placeholder at the origin's X,Y on a level that has no room there. Display only, never stored.
public record MapGhost(double X, double Y, double W, double H, int GridX, int GridY, int Z, string Area, string CreateHref);

public record RoomMap(IReadOnlyList<MapRect> Rects, IReadOnlyList<MapLine> Lines, IReadOnlyList<MapStub> Stubs,
    IReadOnlyList<MapBadge> Badges, IReadOnlyList<MapMismatch> Mismatches, IReadOnlyList<MapUnplaced> Unplaced,
    double Width, double Height)
{
    public IReadOnlyList<MapArrow> Arrows { get; init; } = Array.Empty<MapArrow>();
    public MapGhost? Ghost { get; init; }
    // The page draws the SVG when there is anything to show; an empty level with a ghost must still render.
    public bool ShowMap => Rects.Count > 0 || Ghost != null;
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
    // A line's arrowhead (marker #arrow: markerWidth = ArrowSize / LineStroke stroke widths) and the expansion arrows are the same size,
    // ArrowSize SVG units across. ArrowPath is that triangle centred on the origin, pointing east.
    public const double ArrowSize = 16;
    public const double LineStroke = 2;
    public static readonly string ArrowPath = FormattableString.Invariant($"M{ArrowSize / 2},0 L{-ArrowSize / 2},{-ArrowSize / 2} L{-ArrowSize / 2},{ArrowSize / 2} z");
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

    // The eight directions: the full-word exit name written when an exit is added, and the offset.
    public static readonly IReadOnlyList<(string Name, int Dx, int Dy)> Directions = new[]
    {
        ("north", 0, 1), ("northeast", 1, 1), ("east", 1, 0), ("southeast", 1, -1),
        ("south", 0, -1), ("southwest", -1, -1), ("west", -1, 0), ("northwest", -1, 1),
    };

    public static string NameOf(int dx, int dy) => Directions.First(d => d.Dx == dx && d.Dy == dy).Name;

    // Screen angle in degrees (clockwise from east; screen y points down) of the triangle for a direction.
    private static double AngleOf(int dx, int dy) => Math.Round(Math.Atan2(-dy, dx) * 180 / Math.PI, 2);

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
        var drawn = new HashSet<(string Room, int Index)>(); // exits shown as a line: the line's arrowheads are their only arrows

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
                drawn.Add((a.Key, i));
                if (reverseIndex >= 0) { drawn.Add((dest.Key, reverseIndex)); }
                var ends = new List<MapLineEnd>();
                if (!mismatch)
                {
                    var mid = ((start.X + end.X) / 2, (start.Y + end.Y) / 2);
                    var back = NameOf(-dir.Dx, -dir.Dy);
                    // Zone at a: the exit that leads to a is dest's reverse exit (remove it) or does not exist (add it, from dest).
                    ends.Add(new MapLineEnd(reverseIndex >= 0
                        ? new MapExitAction(true, dest.Key, dest.Exits[reverseIndex].Name, a.Key, "Remove exit " + back + " from " + dest.Short + " to " + a.Short)
                        : new MapExitAction(false, dest.Key, back, a.Key, "Add exit " + back + " from " + dest.Short + " to " + a.Short),
                        new[] { start, mid }));
                    // Zone at dest: the exit a -> dest exists (the line is drawn for it).
                    ends.Add(new MapLineEnd(new MapExitAction(true, a.Key, exit.Name, dest.Key,
                        "Remove exit " + NameOf(dir.Dx, dir.Dy) + " from " + a.Short + " to " + dest.Short), new[] { end, mid }));
                }
                lines.Add(new MapLine(a.Key, dest.Key, new[] { start, end }, reverseIndex >= 0, true, mismatch) { Ends = ends });
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
        var arrows = BuildArrows(byKey, level, onGrid, area, z, drawn, Left, Top);
        return new RoomMap(rects, lines, stubs, badges, mismatches, unplaced, width, height) { Arrows = arrows, Ghost = ghost };
    }

    // Eight compass arrows around every room plus up and down inside its box. A compass arrow sits in the gap just outside its side (or
    // corner), moved sideways along the edge so the line or stub that leaves the middle of the side stays visible. A direction whose
    // exit is drawn as a line gets no arrow at all (the line's arrowheads and end zones are the controls).
    private static List<MapArrow> BuildArrows(Dictionary<string, RoomRecord> byKey, List<RoomRecord> level,
        Dictionary<string, RoomRecord> onGrid, string area, int z, HashSet<(string Room, int Index)> drawn,
        Func<RoomRecord, double> left, Func<RoomRecord, double> top)
    {
        // Placed rooms of the same area and level, by coordinates.
        var atCoordinate = level.ToDictionary(r => (r.X!.Value, r.Y!.Value));
        var arrows = new List<MapArrow>();
        foreach (var a in level)
        {
            var cx = left(a) + Size / 2;
            var cy = top(a) + Size / 2;
            foreach (var (name, dx, dy) in Directions)
            {
                var diagonal = dx != 0 && dy != 0;
                var reach = Size / 2 + (diagonal ? 13 : 14);
                var shift = diagonal ? 0 : 26;
                // Sideways = along the edge, away from the middle where the line or stub leaves.
                var px = cx + dx * reach + (dy != 0 && !diagonal ? shift : 0);
                var py = cy - dy * reach + (dx != 0 && !diagonal ? shift : 0);
                var angle = AngleOf(dx, dy);
                var index = -1;
                for (var i = 0; i < a.Exits.Count; i++)
                {
                    if (TryCompass(a.Exits[i].Name, out var v) && v.Dx == dx && v.Dy == dy) { index = i; break; }
                }
                if (index >= 0)
                {
                    if (drawn.Contains((a.Key, index))) { continue; }
                    var exit = a.Exits[index];
                    byKey.TryGetValue(exit.To, out var target);
                    arrows.Add(new MapArrow(a.Key, name, MapArrowKind.Go, true, angle, px, py,
                        "Go to " + (target?.Short ?? exit.To) + " (" + name + ")", GoHref(target, onGrid), exit.To));
                    continue;
                }
                if (atCoordinate.TryGetValue((a.X!.Value + dx, a.Y!.Value + dy), out var adjacent) && adjacent.Key != a.Key)
                {
                    arrows.Add(new MapArrow(a.Key, name, MapArrowKind.Link, false, angle, px, py,
                        "Link " + name + " to " + adjacent.Short, null, adjacent.Key));
                    continue;
                }
                var nx = a.X!.Value + dx;
                var ny = a.Y!.Value + dy;
                arrows.Add(new MapArrow(a.Key, name, MapArrowKind.Add, false, angle, px, py,
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
        return new MapArrow(a.Key, name, MapArrowKind.Level, exit != null, up ? -90 : 90, x, y, label,
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
