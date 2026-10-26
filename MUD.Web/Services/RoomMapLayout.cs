namespace MUD.Web.Services;

public record MapRect(string Key, string Short, double X, double Y, double W, double H, int GridX, int GridY);

// Points are in SVG coordinates. ArrowStart/ArrowEnd mark arrowheads at the first/last point.
// Warning = coordinates disagree with the exit direction (drawn dashed in a warning colour).
public record MapLine(string FromKey, string ToKey, IReadOnlyList<(double X, double Y)> Points,
    bool ArrowStart, bool ArrowEnd, bool Warning);

// Off-grid compass exit: short line leaving the square with the target key as label.
public record MapStub(string FromKey, string Direction, string Target, bool Missing,
    IReadOnlyList<(double X, double Y)> Points, double LabelX, double LabelY);

// Exit that cannot be placed on the grid (up/down/named). Text is an arrow glyph or the exit name.
public record MapBadge(string RoomKey, string Text, string Target, bool Missing, double X, double Y);

public record MapMismatch(string FromKey, string Direction, string ToKey);

public record MapUnplaced(string Key, string Short);

public record RoomMap(IReadOnlyList<MapRect> Rects, IReadOnlyList<MapLine> Lines, IReadOnlyList<MapStub> Stubs,
    IReadOnlyList<MapBadge> Badges, IReadOnlyList<MapMismatch> Mismatches, IReadOnlyList<MapUnplaced> Unplaced,
    double Width, double Height);

// Pure layout: rooms in, primitive shapes out. NORTH IS UP: screen y = -Y (+x east, +z is the level selector).
public class RoomMapLayout
{
    public const double Size = 120;
    public const double Pitch = 190;
    public const double Margin = 70;
    public const double StubLength = 45;
    public const string Up = "\u2191";
    public const string Down = "\u2193";

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

    public static bool TryCompass(string name, out (int Dx, int Dy) v) => Compass.TryGetValue(name.Trim(), out v);

    public static string AreaOf(RoomRecord r) => r.Area ?? string.Empty;
    public static int LevelOf(RoomRecord r) => r.Z ?? 0;
    public static bool IsPlaced(RoomRecord r) => r.X.HasValue && r.Y.HasValue;

    public RoomMap Build(IReadOnlyList<RoomRecord> all, string area, int z)
    {
        var byKey = new Dictionary<string, RoomRecord>(StringComparer.Ordinal);
        foreach (var r in all) { byKey[r.Key] = r; }

        var inArea = all.Where(r => AreaOf(r) == area).OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
        var unplaced = inArea.Where(r => !IsPlaced(r)).Select(r => new MapUnplaced(r.Key, r.Short)).ToList();
        var level = inArea.Where(r => IsPlaced(r) && LevelOf(r) == z).ToList();
        var onGrid = level.ToDictionary(r => r.Key, StringComparer.Ordinal);

        if (level.Count == 0)
        {
            return new RoomMap(new List<MapRect>(), new List<MapLine>(), new List<MapStub>(), new List<MapBadge>(),
                new List<MapMismatch>(), unplaced, 2 * Margin, 2 * Margin);
        }

        var minX = level.Min(r => r.X!.Value);
        var maxY = level.Max(r => r.Y!.Value);
        var maxX = level.Max(r => r.X!.Value);
        var minY = level.Min(r => r.Y!.Value);

        // screen_y = -Y: the larger Y, the nearer the top.
        double Left(RoomRecord r) => Margin + (r.X!.Value - minX) * Pitch;
        double Top(RoomRecord r) => Margin + (maxY - r.Y!.Value) * Pitch;

        (double X, double Y) Anchor(RoomRecord r, int dx, int dy) =>
            (Left(r) + Size / 2 + dx * Size / 2, Top(r) + Size / 2 - dy * Size / 2);

        var rects = level.Select(r => new MapRect(r.Key, r.Short, Left(r), Top(r), Size, Size, r.X!.Value, r.Y!.Value)).ToList();
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
                    var n = exit.Name.Trim();
                    var text = n.Equals("up", StringComparison.OrdinalIgnoreCase) || n.Equals("u", StringComparison.OrdinalIgnoreCase) ? Up
                        : n.Equals("down", StringComparison.OrdinalIgnoreCase) || n.Equals("d", StringComparison.OrdinalIgnoreCase) ? Down
                        : exit.Name;
                    badges.Add(new MapBadge(a.Key, text, exit.To, missing, Left(a), Top(a) + 62 + 15 * badgeIndex++));
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
        return new RoomMap(rects, lines, stubs, badges, mismatches, unplaced, width, height);
    }
}
