using MUD.Contracts;
using MUD.Web.Pages;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

// Expansion arrows on the room map: arrow set, black/gray, the x rule, offsets, Create room prefill, redirect after create, level switching, wiring.
public class MapArrowLayoutTests
{
    private static RoomRecord Room(string key, int? x, int? y, int? z = 0, string area = "a", params (string Name, string To)[] exits) =>
        new(key, area, "short " + key, x, y, z, exits.Select(e => new RoomExitRecord(e.Name, e.To)).ToList());

    private static RoomMap Build(int z, params RoomRecord[] rooms) => new RoomMapLayout().Build(rooms, "a", z);

    private static MapArrow Arrow(RoomMap m, string room, string dir) => m.Arrows.Single(a => a.RoomKey == room && a.Direction == dir);

    [Fact]
    public void EveryRoom_HasTheEightCompassArrowsPlusUpAndDown()
    {
        var map = Build(0, Room("a.o", 0, 0));
        Assert.Equal(10, map.Arrows.Count);
        Assert.Equal(new[] { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest", "up", "down" }, map.Arrows.Select(a => a.Direction));
        Assert.Equal(8, map.Arrows.Count(a => a.Kind == MapArrowKind.Add));
        Assert.Equal(2, map.Arrows.Count(a => a.Kind == MapArrowKind.Level));
    }

    [Fact]
    public void ArrowsArePerRoom()
    {
        var map = Build(0, Room("a.o", 0, 0), Room("a.p", 5, 5));
        Assert.Equal(20, map.Arrows.Count);
        Assert.Equal(10, map.Arrows.Count(a => a.RoomKey == "a.p"));
    }

    [Theory]
    [InlineData("north", 0, 1)] [InlineData("northeast", 1, 1)] [InlineData("east", 1, 0)] [InlineData("southeast", 1, -1)]
    [InlineData("south", 0, -1)] [InlineData("southwest", -1, -1)] [InlineData("west", -1, 0)] [InlineData("northwest", -1, 1)]
    public void GrayArrow_OffsetsTheCoordinates_NorthIsPlusY_SameZ(string dir, int dx, int dy)
    {
        var map = Build(3, Room("a.o", 10, 20, 3));
        var a = Arrow(map, "a.o", dir);
        Assert.Equal(MapArrowKind.Add, a.Kind);
        Assert.False(a.Black);
        Assert.Equal((10 + dx, 20 + dy, 3), (a.CreateX!.Value, a.CreateY!.Value, a.CreateZ!.Value));
        Assert.Equal($"/creator?area=a&x={10 + dx}&y={20 + dy}&z=3", a.Href);
        Assert.Equal("Add room " + dir + " of short a.o", a.Label);
        // The arrow sits on the screen side of its direction (east is right, north is up).
        var rect = map.Rects.Single();
        if (dx != 0) { Assert.Equal(dx, Math.Sign(a.X - (rect.X + rect.W / 2))); }
        if (dy != 0) { Assert.Equal(-dy, Math.Sign(a.Y - (rect.Y + rect.H / 2))); }
    }

    [Fact]
    public void CompassArrowsLeaveRoomForTheLine_AndSitOutsideTheSquare()
    {
        var map = Build(0, Room("a.o", 0, 0));
        var rect = map.Rects.Single();
        foreach (var a in map.Arrows.Where(a => a.Kind == MapArrowKind.Add))
        {
            var inside = a.X > rect.X && a.X < rect.X + rect.W && a.Y > rect.Y && a.Y < rect.Y + rect.H;
            Assert.False(inside, a.Direction);
        }
        // The middle of each side stays free for the line or stub that leaves there: the side arrows are shifted along the edge.
        Assert.NotEqual(rect.X + rect.W / 2, Arrow(map, "a.o", "north").X);
        Assert.NotEqual(rect.X + rect.W / 2, Arrow(map, "a.o", "south").X);
        Assert.NotEqual(rect.Y + rect.H / 2, Arrow(map, "a.o", "east").Y);
        Assert.NotEqual(rect.Y + rect.H / 2, Arrow(map, "a.o", "west").Y);
    }

    [Fact]
    public void ExitToADrawnLine_HasNoGoArrow_StubExitsKeepTheirs_OtherDirectionsStayGray()
    {
        var map = Build(0, Room("a.o", 0, 0, 0, "a", ("north", "a.n"), ("e", "gone.room")), Room("a.n", 0, 1));
        Assert.Single(map.Lines);
        Assert.DoesNotContain(map.Arrows, a => a.RoomKey == "a.o" && a.Direction == "north"); // the line's arrowheads are the only arrows
        Assert.Equal(MapArrowKind.Link, Arrow(map, "a.n", "south").Kind); // no exit back: the gray arrow offers it (the plain line end does too)
        var e = Arrow(map, "a.o", "east"); // short name works as well as the full word; a missing target is a stub
        Assert.Equal((MapArrowKind.Go, true), (e.Kind, e.Black));
        Assert.Equal("Go to gone.room (east)", e.Label);
        Assert.Null(e.Href); // the target room does not exist: nothing to follow
        Assert.Equal(MapArrowKind.Add, Arrow(map, "a.o", "south").Kind);
        Assert.False(Arrow(map, "a.o", "south").Black);
    }

    [Fact]
    public void TwoWayAndOneWayLines_DrawNoGoArrowOnEitherRoom()
    {
        var map = Build(0, Room("a.o", 0, 0, 0, "a", ("east", "a.e")), Room("a.e", 1, 0, 0, "a", ("west", "a.o")), Room("a.n", 0, 1, 0, "a", ("south", "a.o")));
        Assert.DoesNotContain(map.Arrows, a => a.Kind == MapArrowKind.Go);
        Assert.Equal(2, map.Lines.Count);
    }

    [Fact]
    public void MismatchLine_AlsoDrawsNoGoArrow()
    {
        var map = Build(0, Room("a.o", 0, 0, 0, "a", ("north", "a.e")), Room("a.e", 1, 0));
        Assert.True(map.Lines.Single().Warning);
        Assert.DoesNotContain(map.Arrows, a => a.RoomKey == "a.o" && a.Direction == "north");
    }

    [Fact]
    public void StubTargets_OffGridOtherLevelOtherAreaMissing_KeepTheBlackGoArrow()
    {
        var map = Build(0, Room("a.o", 0, 0, 0, "a", ("north", "a.up"), ("east", "b.far"), ("south", "gone.room"), ("west", "a.u")),
            Room("a.up", 0, 1, 2), Room("b.far", 9, 9, 0, "b"), Room("a.u", null, null, null));
        Assert.Equal(4, map.Stubs.Count);
        Assert.Empty(map.Lines);
        foreach (var d in new[] { "north", "east", "south", "west" }) { Assert.Equal(MapArrowKind.Go, Arrow(map, "a.o", d).Kind); }
    }

    [Fact]
    public void BlackArrow_ToAnUnplacedRoom_GoesToItsEditPage()
    {
        var map = Build(0, Room("a.o", 0, 0, 0, "a", ("north", "a.u")), Room("a.u", null, null, null));
        Assert.Equal("/creator/rooms/a.u", Arrow(map, "a.o", "north").Href);
    }

    [Fact]
    public void BlackArrow_GoesToTheMapOfItsLevel_WhenTheTargetIsElsewhere()
    {
        var map = Build(0, Room("a.o", 0, 0, 0, "a", ("north", "a.up"), ("east", "b.far")),
            Room("a.up", 0, 1, 2), Room("b.far", 9, 9, 4, "b"));
        Assert.Equal("/creator/map?area=a&z=2&center=a.up", Arrow(map, "a.o", "north").Href);
        Assert.Equal("/creator/map?area=b&z=4&center=b.far", Arrow(map, "a.o", "east").Href);
    }

    // Gray arrow next to an existing room: link, not create.
    [Fact]
    public void GrayArrow_NextToAPlacedRoomOfTheSameAreaAndLevel_IsALink_OneDirection()
    {
        var map = Build(0, Room("a.o", 0, 0), Room("a.e", 1, 0), Room("a.ne", 1, 1));
        var x = Arrow(map, "a.o", "east");
        Assert.Equal((MapArrowKind.Link, false, "a.e"), (x.Kind, x.Black, x.TargetKey));
        Assert.Null(x.Href);
        Assert.Equal("Link east to short a.e", x.Label);
        Assert.Equal(new MapExitAction(false, "a.o", "east", "a.e", x.Label), x.Action);
        Assert.Equal("a.ne", Arrow(map, "a.o", "northeast").TargetKey);
        Assert.Equal(MapArrowKind.Add, Arrow(map, "a.o", "west").Kind);
        Assert.Equal("Add room west of short a.o", Arrow(map, "a.o", "west").Label);
        Assert.Null(Arrow(map, "a.o", "west").Action);
        Assert.Equal("a.o", Arrow(map, "a.e", "west").TargetKey); // both ends offer their own direction
    }

    [Theory]
    [InlineData("north", 0, 1)] [InlineData("northeast", 1, 1)] [InlineData("east", 1, 0)] [InlineData("southeast", 1, -1)]
    [InlineData("south", 0, -1)] [InlineData("southwest", -1, -1)] [InlineData("west", -1, 0)] [InlineData("northwest", -1, 1)]
    public void LinkAction_UsesTheArrowDirection_AndTheRoomAtThoseCoordinates_InAll8Directions(string dir, int dx, int dy)
    {
        var map = Build(1, Room("a.o", 5, 5, 1), Room("a.t", 5 + dx, 5 + dy, 1), Room("a.other", 5 + dx, 5 + dy, 2));
        var act = Arrow(map, "a.o", dir).Action!;
        Assert.Equal(new MapExitAction(false, "a.o", dir, "a.t", "Link " + dir + " to short a.t"), act);
    }

    [Fact]
    public void GrayArrow_NotALink_ForARoomOfAnotherAreaOrLevelAtTheAdjacentCoordinates_ItStillCreates()
    {
        var map = Build(0, Room("a.o", 0, 0), Room("a.up", 1, 0, 1), Room("b.x", 0, 1, 0, "b"));
        var e = Arrow(map, "a.o", "east");
        Assert.Equal((MapArrowKind.Add, "/creator?area=a&x=1&y=0&z=0"), (e.Kind, e.Href));
        Assert.Equal(MapArrowKind.Add, Arrow(map, "a.o", "north").Kind);
    }

    [Fact]
    public void RoomElsewhereWithAOneWayExitHere_OffersNothingOnThisSide()
    {
        // b.far is off the grid: its exit into a.o is a stub on b.far's own map; a.o gets no x any more, only the plain gray arrow.
        var map = Build(0, Room("a.o", 0, 0), Room("b.far", 9, 9, 0, "b", ("east", "a.o")));
        Assert.Equal(MapArrowKind.Add, Arrow(map, "a.o", "west").Kind);
        Assert.DoesNotContain(map.Arrows, a => a.Kind == MapArrowKind.Link);
    }

    [Fact]
    public void LinkArrow_OnceLinked_BecomesALineEnd_AndTheArrowDisappears()
    {
        var map = Build(0, Room("a.o", 0, 0, 0, "a", ("east", "a.e")), Room("a.e", 1, 0));
        Assert.DoesNotContain(map.Arrows, a => a.RoomKey == "a.o" && a.Direction == "east");
        Assert.Equal(MapArrowKind.Link, Arrow(map, "a.e", "west").Kind); // the way back is still missing: gray arrow and plain line end both offer it
    }

    // Line ends.
    private static MapExitAction[] EndActions(RoomMap m) => m.Lines.Single().Ends.Select(e => e.Action).ToArray();

    [Fact]
    public void TwoWayLine_BothEndsRemove_TheExitLeadingToTheRoomAtThatEnd()
    {
        var map = Build(0, Room("a.study", 0, 1, 0, "a", ("south", "a.hall")), Room("a.hall", 0, 0, 0, "a", ("north", "a.study")));
        var line = map.Lines.Single();
        Assert.True(line.ArrowStart && line.ArrowEnd);
        // Both ends remove: the zone at each room removes the exit that leads to that room.
        Assert.Equal(new[]
        {
            new MapExitAction(true, "a.study", "south", "a.hall", "Remove exit south from short a.study to short a.hall"),
            new MapExitAction(true, "a.hall", "north", "a.study", "Remove exit north from short a.hall to short a.study"),
        }.OrderBy(a => a.TargetKey), EndActions(map).OrderBy(a => a.TargetKey));
    }

    [Fact]
    public void TwoWayLine_ReversedExitOrder_GivesTheSameActions_AtTheSameEnds()
    {
        var a = Build(0, Room("a.study", 0, 1, 0, "a", ("south", "a.hall")), Room("a.hall", 0, 0, 0, "a", ("north", "a.study")));
        var b = Build(0, Room("a.hall", 0, 0, 0, "a", ("north", "a.study")), Room("a.study", 0, 1, 0, "a", ("south", "a.hall")));
        // Whichever room the line is drawn from, the zone at the study removes hall -> study and the zone at the hall removes study -> hall.
        foreach (var m in new[] { a, b })
        {
            var line = m.Lines.Single();
            var study = m.Rects.Single(r => r.Key == "a.study");
            var atStudy = line.Ends.Single(e => Math.Abs(e.Zone[0].Y - study.Y) < 1 || Math.Abs(e.Zone[0].Y - (study.Y + study.H)) < 1).Action;
            Assert.Equal(("a.hall", "north", "a.study", true), (atStudy.OwnerKey, atStudy.Direction, atStudy.TargetKey, atStudy.Remove));
            var other = line.Ends.Single(e => e.Action != atStudy).Action;
            Assert.Equal(("a.study", "south", "a.hall", true), (other.OwnerKey, other.Direction, other.TargetKey, other.Remove));
        }
    }

    [Fact]
    public void OneWayLine_ArrowheadEndRemoves_PlainEndAdds_TheOppositeDirection_FromTheOtherRoom()
    {
        var map = Build(0, Room("a.study", 0, 1), Room("a.hall", 0, 0, 0, "a", ("north", "a.study")));
        var line = map.Lines.Single();
        Assert.False(line.ArrowStart);
        Assert.True(line.ArrowEnd);
        // Line drawn from the hall (start) to the study (end, arrowhead): the arrowhead end removes hall -> study, the plain end adds study -> hall.
        Assert.Equal(new[]
        {
            new MapExitAction(false, "a.study", "south", "a.hall", "Add exit south from short a.study to short a.hall"),
            new MapExitAction(true, "a.hall", "north", "a.study", "Remove exit north from short a.hall to short a.study"),
        }, EndActions(map));
    }

    [Fact]
    public void OneWayLine_OtherOrientation_AddNameIsTheOppositeOfTheExistingDirection()
    {
        var map = Build(0, Room("a.study", 0, 1, 0, "a", ("south", "a.hall")), Room("a.hall", 0, 0));
        Assert.Equal(new[]
        {
            new MapExitAction(false, "a.hall", "north", "a.study", "Add exit north from short a.hall to short a.study"),
            new MapExitAction(true, "a.study", "south", "a.hall", "Remove exit south from short a.study to short a.hall"),
        }, EndActions(map));
    }

    [Theory]
    [InlineData("north", 0, 1, "south")] [InlineData("northeast", 1, 1, "southwest")] [InlineData("east", 1, 0, "west")] [InlineData("southeast", 1, -1, "northwest")]
    [InlineData("south", 0, -1, "north")] [InlineData("southwest", -1, -1, "northeast")] [InlineData("west", -1, 0, "east")] [InlineData("northwest", -1, 1, "southeast")]
    public void OneWayLine_AddedExitName_IsTheFullWordOpposite_InAll8Directions(string dir, int dx, int dy, string opposite)
    {
        // Short aliases in the data still produce full-word names for the exit that gets added.
        var short1 = new Dictionary<string, string> { ["north"] = "n", ["east"] = "e", ["south"] = "s", ["west"] = "w", ["northeast"] = "ne", ["northwest"] = "nw", ["southeast"] = "se", ["southwest"] = "sw" };
        var map = Build(0, Room("a.o", 0, 0, 0, "a", (short1[dir], "a.t")), Room("a.t", dx, dy));
        var add = map.Lines.Single().Ends.Select(e => e.Action).Single(a => !a.Remove);
        Assert.Equal(("a.t", opposite, "a.o"), (add.OwnerKey, add.Direction, add.TargetKey));
        var remove = map.Lines.Single().Ends.Select(e => e.Action).Single(a => a.Remove);
        Assert.Equal(("a.o", short1[dir], "a.t"), (remove.OwnerKey, remove.Direction, remove.TargetKey)); // removal matches the stored name
    }

    [Fact]
    public void MismatchLine_HasNoEndZones()
    {
        var map = Build(0, Room("a.o", 0, 0, 0, "a", ("north", "a.e")), Room("a.e", 1, 0));
        Assert.True(map.Lines.Single().Warning);
        Assert.Empty(map.Lines.Single().Ends);
    }

    [Fact]
    public void EndZones_AreTheTwoHalvesOfTheLine_SplitAtTheMidpoint_AndNeverOverlap()
    {
        var map = Build(0, Room("a.o", 0, 0, 0, "a", ("east", "a.e")), Room("a.e", 1, 0));
        var line = map.Lines.Single();
        var (z0, z1) = (line.Ends[0].Zone, line.Ends[1].Zone);
        Assert.Equal(line.Points[0], z0[0]);
        Assert.Equal(line.Points[1], z1[0]);
        Assert.Equal(z0[1], z1[1]); // they meet at the midpoint
        Assert.Equal((line.Points[0].X + line.Points[1].X) / 2, z0[1].X, 6);
    }

    [Fact]
    public void RemovingBothEnds_LeavesNoLine_AndGrayArrowsReturnOnBothRooms()
    {
        var map = Build(0, Room("a.o", 0, 0), Room("a.e", 1, 0));
        Assert.Empty(map.Lines);
        Assert.Equal(MapArrowKind.Link, Arrow(map, "a.o", "east").Kind);
        Assert.Equal(MapArrowKind.Link, Arrow(map, "a.e", "west").Kind);
    }

    // Sizes and drawing.
    [Fact]
    public void GrayAndBlackArrows_AreAsBigAsTheLineArrowhead()
    {
        // The marker is viewBox 10 scaled to markerWidth stroke widths of the line; the arrows are the same size in SVG units.
        Assert.Equal(RoomMapLayout.ArrowSize, RoomMapLayout.ArrowSize / RoomMapLayout.LineStroke * RoomMapLayout.LineStroke);
        var xs = System.Text.RegularExpressions.Regex.Matches(RoomMapLayout.ArrowPath, @"-?\d+(\.\d+)?,-?\d+(\.\d+)?")
            .Select(m => m.Value.Split(',').Select(double.Parse).ToArray()).ToList();
        Assert.Equal(RoomMapLayout.ArrowSize, xs.Max(p => p[0]) - xs.Min(p => p[0]));
        Assert.Equal(RoomMapLayout.ArrowSize, xs.Max(p => p[1]) - xs.Min(p => p[1]));
    }

    [Theory]
    [InlineData("north", -90)] [InlineData("northeast", -45)] [InlineData("east", 0)] [InlineData("southeast", 45)]
    [InlineData("south", 90)] [InlineData("southwest", 135)] [InlineData("west", 180)] [InlineData("northwest", -135)]
    public void CompassArrows_AreRotatedTriangles_OneAnglePerDirection(string dir, double angle)
    {
        Assert.Equal(angle, Arrow(Build(0, Room("a.o", 0, 0)), "a.o", dir).Angle);
    }

    [Fact]
    public void CompassArrows_StayOutsideTheirOwnBox_AndClearOfNeighbours190Apart()
    {
        // Neighbours at the pitch: every arrow's triangle stays inside the gap, off the room squares, in all 8 directions.
        var rooms = new List<RoomRecord>();
        for (var x = -1; x <= 1; x++) { for (var y = -1; y <= 1; y++) { rooms.Add(Room("a." + (x + 1) + (y + 1), x, y)); } }
        var map = Build(0, rooms.ToArray());
        var half = RoomMapLayout.ArrowSize / 2 * 1.42;
        foreach (var a in map.Arrows.Where(a => a.Kind == MapArrowKind.Link || a.Kind == MapArrowKind.Add || a.Kind == MapArrowKind.Go))
        {
            foreach (var r in map.Rects)
            {
                var overlaps = a.X + half > r.X && a.X - half < r.X + r.W && a.Y + half > r.Y && a.Y - half < r.Y + r.H;
                Assert.False(overlaps, a.RoomKey + " " + a.Direction + " covers " + r.Key);
            }
        }
    }

    [Fact]
    public void CompassArrows_NeverSitOnTheMiddleOfASide_WhereTheLineAndItsEndZonesRun()
    {
        var map = Build(0, Room("a.o", 0, 0));
        var r = map.Rects.Single();
        foreach (var d in new[] { "north", "south" }) { Assert.True(Math.Abs(Arrow(map, "a.o", d).X - (r.X + r.W / 2)) >= RoomMapLayout.ArrowSize); }
        foreach (var d in new[] { "east", "west" }) { Assert.True(Math.Abs(Arrow(map, "a.o", d).Y - (r.Y + r.H / 2)) >= RoomMapLayout.ArrowSize); }
    }

    // Up and down.
    [Fact]
    public void UpDown_GrayArrows_SwitchToTheNextLevelOfTheSameArea_AndNeverCreate()
    {
        var map = Build(2, Room("a.o", 4, 7, 2));
        var up = Arrow(map, "a.o", "up");
        var down = Arrow(map, "a.o", "down");
        Assert.Equal((MapArrowKind.Level, false), (up.Kind, up.Black));
        Assert.Equal("/creator/map?area=a&z=3&gx=4&gy=7", up.Href);
        Assert.Equal("/creator/map?area=a&z=1&gx=4&gy=7", down.Href);
        Assert.DoesNotContain("/creator?", up.Href);
        Assert.Null(up.TargetKey);
    }

    [Fact]
    public void UpDown_BlackArrows_SwitchToTheLevelAndAreaOfTheTarget()
    {
        var map = Build(0, Room("a.o", 4, 7, 0, "a", ("up", "b.roof"), ("down", "a.cellar")),
            Room("b.roof", 4, 7, 5, "b"), Room("a.cellar", 0, 0, -3));
        var up = Arrow(map, "a.o", "up");
        var down = Arrow(map, "a.o", "down");
        Assert.True(up.Black); Assert.True(down.Black);
        Assert.Equal("/creator/map?area=b&z=5&gx=4&gy=7", up.Href);
        Assert.Equal("/creator/map?area=a&z=-3&gx=4&gy=7", down.Href);
    }

    [Fact]
    public void UpDown_BlackArrow_WithAMissingTarget_StillOnlySwitchesLevel()
    {
        var map = Build(0, Room("a.o", 0, 0, 0, "a", ("up", "gone.room")));
        var up = Arrow(map, "a.o", "up");
        Assert.True(up.Black);
        Assert.Equal("/creator/map?area=a&z=1&gx=0&gy=0", up.Href);
    }

    [Fact]
    public void UpDown_AreInsideTheBox_UpTopLeft_DownBottomRight()
    {
        var map = Build(0, Room("a.o", 0, 0));
        var r = map.Rects.Single();
        var up = Arrow(map, "a.o", "up");
        var down = Arrow(map, "a.o", "down");
        Assert.True(up.X > r.X && up.X < r.X + r.W / 2 && up.Y > r.Y && up.Y < r.Y + r.H / 2);
        Assert.True(down.X > r.X + r.W / 2 && down.X < r.X + r.W && down.Y > r.Y + r.H / 2 && down.Y < r.Y + r.H);
    }

    [Fact]
    public void Ghost_ShownAtTheOriginXY_WhenTheLevelHasNoRoomThere_WithAPrefilledLink()
    {
        var map = new RoomMapLayout().Build(new[] { Room("a.o", 0, 0, 0), Room("a.p", 3, 2, 1) }, "a", 1, (0, 0));
        Assert.NotNull(map.Ghost);
        Assert.Equal((0, 0, 1), (map.Ghost!.GridX, map.Ghost.GridY, map.Ghost.Z));
        Assert.Equal("/creator?area=a&x=0&y=0&z=1", map.Ghost.CreateHref);
        Assert.Equal("No room here", RoomMapLayout.GhostText);
        Assert.Equal("Create room here", RoomMapLayout.GhostLink);
        // The ghost is not a stored room: it has no rect and no arrows, but the canvas includes it.
        Assert.DoesNotContain(map.Rects, r => r.GridX == 0 && r.GridY == 0);
        Assert.DoesNotContain(map.Arrows, a => a.RoomKey == "a.o");
        var p = map.Rects.Single();
        Assert.True(map.Ghost.X < p.X && map.Width >= p.X + p.W);
    }

    [Fact]
    public void Ghost_OnAnEmptyLevel_IsTheOnlyThingDrawn()
    {
        var map = new RoomMapLayout().Build(new[] { Room("a.o", 5, 6, 0) }, "a", 1, (5, 6));
        Assert.Empty(map.Rects);
        Assert.Equal("/creator?area=a&x=5&y=6&z=1", map.Ghost!.CreateHref);
        Assert.True(map.Width >= RoomMapLayout.Size);
        Assert.True(map.Height >= RoomMapLayout.Size);
        Assert.True(map.ShowMap);
    }

    [Fact]
    public void ShowMap_IsFalse_OnlyWhenNoRoomsAndNoGhost()
    {
        Assert.False(new RoomMapLayout().Build(new[] { Room("a.o", 5, 6, 0) }, "a", 1).ShowMap);
        Assert.True(new RoomMapLayout().Build(new[] { Room("a.o", 5, 6, 0) }, "a", 0).ShowMap);
    }

    [Fact]
    public void NoGhost_WhenTheLevelHasARoomAtThatXY_OrNoOriginIsGiven()
    {
        var rooms = new[] { Room("a.o", 0, 0, 0), Room("a.p", 0, 0, 1) };
        Assert.Null(new RoomMapLayout().Build(rooms, "a", 1, (0, 0)).Ghost);
        Assert.Null(new RoomMapLayout().Build(rooms, "a", 1).Ghost);
    }

    [Fact]
    public void ArrowAreaIsEscapedInTheCreateLink()
    {
        var map = new RoomMapLayout().Build(new[] { new RoomRecord("a b&c.o", "a b&c", "s", 0, 0, 0, Array.Empty<RoomExitRecord>()) }, "a b&c", 0);
        Assert.Equal("/creator?area=a%20b%26c&x=0&y=1&z=0", Arrow(map, "a b&c.o", "north").Href);
        Assert.DoesNotContain("exit", Arrow(map, "a b&c.o", "north").Href!);
    }

    [Fact]
    public void ExistingLayoutIsUnchanged_ArrowsDoNotAddLinesStubsOrRects()
    {
        var map = Build(0, Room("a.o", 0, 0, 0, "a", ("north", "a.n")), Room("a.n", 0, 1));
        Assert.Equal(2, map.Rects.Count);
        Assert.Single(map.Lines);
        Assert.Empty(map.Stubs);
    }
}

public class MapRoutesTests
{
    [Fact]
    public void CreatorAt_CarriesAreaAndCoordinatesOnly()
    {
        Assert.Equal("/creator?area=t&x=1&y=-2&z=0", WebRoutes.CreatorAt("t", 1, -2, 0));
        Assert.Equal("/creator", WebRoutes.CreatorAt(null, null, null, null));
        Assert.Equal("/creator?area=a%2Fb", WebRoutes.CreatorAt("a/b", null, null, null));
    }

    [Fact]
    public void MapFor_AddsCentreGhostAndCreatedOnlyWhenGiven()
    {
        Assert.Equal("/creator/map?area=a&z=1", WebRoutes.MapFor("a", 1));
        Assert.Equal("/creator/map?area=a", WebRoutes.MapFor("a", null));
        Assert.Equal("/creator/map?area=a&z=1&gx=2&gy=3&center=a.k%20x&created=1", WebRoutes.MapFor("a", 1, 2, 3, "a.k x", true));
    }
}

public class CreateRoomPrefillTests
{
    private static CreatorPresenter Make(FakeAdminClient core, string token = "t") => new(new RoomCreationService(core), token);

    private static FakeAdminClient CoreWithArea() => new FakeAdminClient().AddArea("tower", "Tower");

    [Fact]
    public async Task Prefill_SetsAreaAndCoordinates_AndNothingElse()
    {
        var p = Make(CoreWithArea());
        await p.LoadAreasAsync();
        p.Prefill("tower", 4, -1, 2);
        Assert.Equal("tower", p.Form.Area);
        Assert.Equal((4, -1, 2), (p.Form.X!.Value, p.Form.Y!.Value, p.Form.Z!.Value));
        Assert.Equal("tower.", p.KeyPrefix);
        Assert.Null(p.Form.Key);
        Assert.Null(p.Form.Short);
        Assert.All(p.Form.Exits, e => Assert.True(e.IsEmpty)); // no exit is prefilled, nothing is linked back
    }

    [Fact]
    public async Task Prefill_IgnoresAnUnknownArea_AndLeavesAbsentCoordinatesBlank()
    {
        var p = Make(CoreWithArea());
        await p.LoadAreasAsync();
        p.Prefill("nope", null, 3, null);
        Assert.Null(p.Form.Area);
        Assert.Null(p.Form.X);
        Assert.Equal(3, p.Form.Y);
        Assert.Null(p.Form.Z);
    }

    [Fact]
    public async Task Prefill_WritesNothingToCore()
    {
        var core = CoreWithArea();
        var p = Make(core);
        await p.LoadAreasAsync();
        p.Prefill("tower", 1, 1, 0);
        Assert.Empty(core.RoomCreates);
        Assert.Empty(core.RoomUpdates);
    }

    [Fact]
    public void CreatorPage_ReadsTheQueryString()
    {
        var src = File.ReadAllText(Path.Combine(FindRoot(), "MUD.Web/Components/Pages/Creator.razor"));
        foreach (var name in new[] { "area", "x", "y", "z" }) { Assert.Contains($"SupplyParameterFromQuery(Name = \"{name}\")", src); }
        Assert.Contains("presenter.Prefill(Area, X, Y, Z)", src);
        Assert.Contains("Navigation.NavigateTo(_presenter.MapAfterCreate())", src);
        Assert.DoesNotContain("WebRoutes.RoomEdit(_presenter.CreatedKey", src);
    }

    private static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "MUD.sln")) && !Directory.Exists(Path.Combine(dir, "specs"))) { dir = Path.GetDirectoryName(dir); }
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    private static async Task<CreatorPresenter> Created(int? x, int? y, int? z)
    {
        var p = Make(CoreWithArea());
        await p.LoadAreasAsync();
        p.Form.Area = "tower"; p.Form.Key = "hall"; p.Form.Short = "Hall"; p.Form.Long = "Long.";
        p.Form.X = x; p.Form.Y = y; p.Form.Z = z;
        Assert.Equal(CreatorOutcome.Created, await p.CreateAsync());
        return p;
    }

    [Fact]
    public async Task AfterCreate_RedirectsToTheMapOfItsAreaAndLevel_CentredOnTheNewRoom()
    {
        var p = await Created(2, 3, 4);
        Assert.Equal("/creator/map?area=tower&z=4&center=tower.hall&created=1", p.MapAfterCreate());
    }

    [Fact]
    public async Task AfterCreate_PlacedWithoutZ_UsesLevelZero()
    {
        var p = await Created(2, 3, null);
        Assert.Equal("/creator/map?area=tower&z=0&center=tower.hall&created=1", p.MapAfterCreate());
    }

    [Fact]
    public async Task AfterCreate_UnplacedRoom_GoesToTheMapOfItsAreaWithoutALevel()
    {
        var p = await Created(null, null, null);
        Assert.Equal("/creator/map?area=tower&center=tower.hall&created=1", p.MapAfterCreate());
        var p2 = await Created(5, null, 2); // X without Y is not on the grid either
        Assert.DoesNotContain("z=", p2.MapAfterCreate());
    }

    [Fact]
    public void MapPage_ShowsTheCreatedNotice_WithAnEditLink_AndSaysWhenUnplaced()
    {
        var src = File.ReadAllText(Path.Combine(FindRoot(), "MUD.Web/Components/Pages/CreatorMap.razor"));
        Assert.Contains("Created @Center.", src);
        Assert.Contains("WebRoutes.RoomEdit(Center)", src);
        Assert.Contains("Edit room", src);
        Assert.Contains("not on the grid", src);
        Assert.Contains("SupplyParameterFromQuery(Name = \"center\")", src);
    }
}

public class MapExitWriteTests
{
    private static FakeAdminClient Core() => new FakeAdminClient()
        .AddRoom("a.o", "a", 0, 0, 0, ("north", "a.n"))
        .AddRoom("a.n", "a", 0, 1, 0, ("south", "a.o"))
        .AddRoom("a.e", "a", 1, 0, 0);

    private static CreatorMapPresenter Make(FakeAdminClient core) => new(core, "tok");
    private static MapExitAction Add(string owner = "a.o", string dir = "east", string to = "a.e") => new(false, owner, dir, to, "x");
    private static MapExitAction Remove(string owner = "a.o", string dir = "north", string to = "a.n") => new(true, owner, dir, to, "x");

    [Fact]
    public async Task Add_AddsExactlyOneExit_FromTheOwner_ThroughUpdateRoom_AndNeverTheReverse()
    {
        var core = Core();
        Assert.True(await Make(core).ApplyAsync(Add()));
        var sent = Assert.Single(core.RoomUpdates);
        Assert.Equal("a.o", sent.Key);
        Assert.Equal(new[] { ("north", "a.n"), ("east", "a.e") }, sent.Exits.Select(e => (e.Name, e.To)));
        Assert.Empty(core.RoomCreates);
        Assert.Empty(core.Rooms["a.e"].Exits); // the room at the other end is not touched
        Assert.Equal(("south", "a.o"), Assert.Single(core.Rooms["a.n"].Exits.Select(e => (e.Name, e.To))));
    }

    [Fact]
    public async Task Remove_RemovesExactlyOneExit_FromTheOwner_AndTouchesNoOtherRoom()
    {
        var core = Core();
        Assert.True(await Make(core).ApplyAsync(Remove()));
        var sent = Assert.Single(core.RoomUpdates);
        Assert.Equal("a.o", sent.Key);
        Assert.Empty(sent.Exits);
        Assert.Empty(core.Rooms["a.o"].Exits);
        Assert.Equal(("south", "a.o"), Assert.Single(core.Rooms["a.n"].Exits.Select(e => (e.Name, e.To)))); // keeps its way back
    }

    [Fact]
    public async Task Remove_MatchesOnNameAndTarget_SoASecondExitToTheSameRoomStays()
    {
        var core = new FakeAdminClient().AddRoom("a.o", "a", 0, 0, 0, ("north", "a.n"), ("door", "a.n"), ("east", "a.e")).AddRoom("a.n", "a", 0, 1, 0).AddRoom("a.e", "a", 1, 0, 0);
        Assert.True(await Make(core).ApplyAsync(Remove()));
        Assert.Equal(new[] { ("door", "a.n"), ("east", "a.e") }, core.Rooms["a.o"].Exits.Select(e => (e.Name, e.To)));
    }

    [Fact]
    public async Task Remove_AShortStoredName_IsMatchedAsStored()
    {
        var core = new FakeAdminClient().AddRoom("a.o", "a", 0, 0, 0, ("n", "a.n")).AddRoom("a.n", "a", 0, 1, 0);
        Assert.True(await Make(core).ApplyAsync(Remove(dir: "n")));
        Assert.Empty(core.Rooms["a.o"].Exits);
    }

    [Fact]
    public async Task Ok_SendsTheWholeForm_SoCoreDiffsOnlyTheExit()
    {
        foreach (var action in new[] { Add(), Remove() })
        {
            var core = Core();
            core.Rooms["a.o"].ShortDescription = "Hall";
            core.Rooms["a.o"].LongDescription = "A hall.";
            core.Rooms["a.o"].Features.Add(new RoomFeature { Short = "lamp", Long = "A lamp." });
            Assert.True(await Make(core).ApplyAsync(action));
            var sent = core.RoomUpdates.Single();
            Assert.Equal("Hall", sent.ShortDescription);
            Assert.Equal("A hall.", sent.LongDescription);
            Assert.Equal("a", sent.Area);
            Assert.Equal((0, 0, 0), (sent.X, sent.Y, sent.Z));
            Assert.True(sent.FeaturesPresent);
            Assert.Equal("lamp", Assert.Single(sent.Features).Short);
        }
    }

    [Fact]
    public async Task Add_WritesTheFullWordDirectionName_EvenForAShortName()
    {
        var core = Core();
        await Make(core).ApplyAsync(Add(dir: "e"));
        Assert.Equal("east", core.RoomUpdates.Single().Exits.Last().Name);
    }

    [Fact]
    public async Task Ok_UsesTheFreshRoom_NotTheMap_AndTheCircuitToken()
    {
        var core = Core();
        core.Rooms["a.o"].ShortDescription = "Changed since the map was drawn";
        await Make(core).ApplyAsync(Add());
        Assert.Equal("Changed since the map was drawn", core.RoomUpdates.Single().ShortDescription);
        Assert.All(core.RoomTokens, t => Assert.Equal("tok", t));
        Assert.NotEmpty(core.RoomTokens);
    }

    [Fact]
    public async Task Ok_SetsTheFixedNotice_ForAddAndRemove()
    {
        var p = Make(Core());
        Assert.True(await p.ApplyAsync(Remove()));
        Assert.Equal("Removed exit north from a.o to a.n", p.WireNotice);
        Assert.Null(p.WireError);
        Assert.True(await p.ApplyAsync(Add()));
        Assert.Equal("Added exit east from a.o to a.e", p.WireNotice);
    }

    [Fact]
    public async Task Remove_TheExitChangedMeanwhile_IsRefusedWithoutWriting()
    {
        var core = Core();
        core.Rooms["a.o"].Exits.Clear(); // somebody removed it already
        var p = Make(core);
        Assert.False(await p.ApplyAsync(Remove()));
        Assert.Equal(Messages.MapExitChanged, p.WireError);
        Assert.Empty(core.RoomUpdates);
    }

    [Fact]
    public async Task Remove_TheExitNowPointsElsewhere_IsRefusedWithoutWriting()
    {
        var core = Core();
        core.Rooms["a.o"].Exits[0].To = "a.e";
        var p = Make(core);
        Assert.False(await p.ApplyAsync(Remove()));
        Assert.Equal(Messages.MapExitChanged, p.WireError);
        Assert.Empty(core.RoomUpdates);
    }

    [Fact]
    public async Task Add_TheDirectionIsNoLongerFree_IsRefusedWithoutOverwriting_EvenUnderAShortAlias()
    {
        var core = Core();
        core.AddRoom("a.o", "a", 0, 0, 0, ("north", "a.n"), ("e", "a.other"));
        var p = Make(core);
        Assert.False(await p.ApplyAsync(Add()));
        Assert.Equal(Messages.MapDirectionTaken, p.WireError);
        Assert.Empty(core.RoomUpdates);
        Assert.Equal(new[] { ("north", "a.n"), ("e", "a.other") }, core.Rooms["a.o"].Exits.Select(e => (e.Name, e.To)));
    }

    [Fact]
    public async Task TheRoomIsGone_IsRefusedWithoutWriting()
    {
        var core = Core();
        core.Rooms.Remove("a.o");
        var p = Make(core);
        Assert.False(await p.ApplyAsync(Add()));
        Assert.Equal(Messages.MapExitChanged, p.WireError);
        Assert.False(await p.ApplyAsync(Remove()));
        Assert.Empty(core.RoomUpdates);
    }

    [Theory]
    [InlineData(AdminResult.Refused)]
    [InlineData(AdminResult.NotFound)]
    [InlineData(AdminResult.NoChange)]
    public async Task CoreAnswers_RefusedNotFoundNoChange_ShowTheChangedMessage(AdminResult result)
    {
        foreach (var action in new[] { Add(), Remove() })
        {
            var core = Core();
            core.RoomEditResult = result;
            var p = Make(core);
            Assert.False(await p.ApplyAsync(action));
            Assert.Equal(Messages.MapExitChanged, p.WireError);
        }
    }

    [Fact]
    public async Task CoreInvalid_ShowsAFixedMessage_NotCoresText()
    {
        var core = Core();
        core.RoomEditResult = AdminResult.Invalid;
        core.RoomEditErrors.Add(new FieldError { Field = "Exits[1].To", Message = "secret detail <b>" });
        var p = Make(core);
        Assert.False(await p.ApplyAsync(Add()));
        Assert.Equal(Messages.MapExitInvalid, p.WireError);
        Assert.DoesNotContain("secret", p.WireError);
    }

    [Fact]
    public async Task Unavailable_ShowsTheFixedMessage()
    {
        var core = Core();
        core.Unavailable = true;
        var p = Make(core);
        Assert.False(await p.ApplyAsync(Remove()));
        Assert.Equal(Messages.Unavailable, p.WireError);
    }

    [Fact]
    public async Task RoleRefused_Exception_ShowsTheForbiddenMessage()
    {
        var core = Core();
        core.RefuseRoomCalls = true;
        var p = Make(core);
        Assert.False(await p.ApplyAsync(Remove()));
        Assert.Equal(Messages.RoomForbidden, p.WireError);
        Assert.Empty(core.RoomUpdates);
    }

    [Fact]
    public async Task RoleRefused_Reply_ShowsTheForbiddenMessage()
    {
        var core = Core();
        core.RoomEditResult = AdminResult.Forbidden;
        var p = Make(core);
        Assert.False(await p.ApplyAsync(Add()));
        Assert.Equal(Messages.RoomForbidden, p.WireError);
    }

    [Fact]
    public async Task AuditFailed_AndAnyOtherAnswer_ShowTheirFixedMessages()
    {
        var core = Core();
        core.RoomEditResult = AdminResult.AuditFailed;
        var p = Make(core);
        Assert.False(await p.ApplyAsync(Add()));
        Assert.Equal(Messages.RoomAuditFailed, p.WireError);
        core.RoomEditResult = AdminResult.AlreadyExists;
        Assert.False(await p.ApplyAsync(Add()));
        Assert.Equal(Messages.RoomSaveFailed, p.WireError);
    }

    [Fact]
    public async Task BadInput_NotACompassDirectionToAdd_OrNoTarget_IsRefusedBeforeCallingCore()
    {
        var core = Core();
        var p = Make(core);
        Assert.False(await p.ApplyAsync(Add(dir: "up")));
        Assert.False(await p.ApplyAsync(Add(to: " ")));
        Assert.Equal(Messages.MapExitChanged, p.WireError);
        Assert.Empty(core.RoomTokens);
    }

    [Fact]
    public async Task TheStudyAndHallExample_EndsToggleTheLine_ThroughTheRealLayoutActions()
    {
        var core = new FakeAdminClient().AddRoom("a.study", "a", 0, 1, 0, ("south", "a.hall")).AddRoom("a.hall", "a", 0, 0, 0, ("north", "a.study"));
        var first = Make(core);
        await first.LoadAsync("a", 0);
        var atStudy = first.Map!.Lines.Single().Ends.Single(e => e.Action.TargetKey == "a.study").Action; // the end at the study
        Assert.Equal(("a.hall", "north", "a.study", true), (atStudy.OwnerKey, atStudy.Direction, atStudy.TargetKey, atStudy.Remove));
        Assert.True(await Make(core).ApplyAsync(atStudy));
        Assert.Empty(core.Rooms["a.hall"].Exits);
        Assert.Single(core.Rooms["a.study"].Exits);

        var second = Make(core);
        await second.LoadAsync("a", 0);
        var line = second.Map!.Lines.Single();
        Assert.False(line.ArrowStart); // arrowhead only at the hall end now
        var plain = line.Ends.Select(e => e.Action).Single(a => !a.Remove);
        Assert.Equal(("a.hall", "north", "a.study"), (plain.OwnerKey, plain.Direction, plain.TargetKey));
        Assert.True(await Make(core).ApplyAsync(plain)); // restores hall -> study
        var third = Make(core);
        await third.LoadAsync("a", 0);
        Assert.True(third.Map!.Lines.Single().ArrowStart && third.Map.Lines.Single().ArrowEnd);

        // Both removed: the line is gone and the gray arrows are back on both rooms.
        foreach (var e in third.Map.Lines.Single().Ends.Select(e => e.Action).ToList()) { Assert.True(await Make(core).ApplyAsync(e)); }
        var fourth = Make(core);
        await fourth.LoadAsync("a", 0);
        Assert.Empty(fourth.Map!.Lines);
        Assert.Equal(MapArrowKind.Link, fourth.Map.Arrows.Single(a => a.RoomKey == "a.study" && a.Direction == "south").Kind);
        Assert.Equal(MapArrowKind.Link, fourth.Map.Arrows.Single(a => a.RoomKey == "a.hall" && a.Direction == "north").Kind);
    }

    [Fact]
    public async Task LinkArrow_AddsOneExit_ThenTheOtherEndIsAPlainLineEnd()
    {
        var core = new FakeAdminClient().AddRoom("a.o", "a", 0, 0, 0).AddRoom("a.e", "a", 1, 0, 0);
        var first = Make(core);
        await first.LoadAsync("a", 0);
        var link = first.Map!.Arrows.Single(a => a.RoomKey == "a.o" && a.Direction == "east").Action!;
        Assert.True(await Make(core).ApplyAsync(link));
        Assert.Equal(("east", "a.e"), Assert.Single(core.Rooms["a.o"].Exits.Select(e => (e.Name, e.To))));
        Assert.Empty(core.Rooms["a.e"].Exits);
        var second = Make(core);
        await second.LoadAsync("a", 0);
        var line = second.Map!.Lines.Single();
        Assert.False(line.ArrowStart);
        Assert.Contains(line.Ends, e => !e.Action.Remove && e.Action.OwnerKey == "a.e" && e.Action.Direction == "west");
    }

    [Fact]
    public void CarryWire_KeepsTheAnswerOnTheRedrawnPresenter()
    {
        var p = Make(Core());
        p.CarryWire("n", "e");
        Assert.Equal(("n", "e"), (p.WireNotice, p.WireError));
    }

    [Fact]
    public void MapPage_StaysUnderCreatorOrAdmin()
    {
        var page = typeof(MUD.Web.Components.Pages.CreatorMap);
        var auth = page.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true).Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Single();
        Assert.Equal(WebPolicies.CreatorOrAdmin, auth.Policy);
    }

    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "MUD.sln"))) { dir = Path.GetDirectoryName(dir); }
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void Page_BlocksASecondClickWhileAWriteRuns_AndPassesBusyToTheMap()
    {
        var page = File.ReadAllText(Path.Combine(Root(), "MUD.Web/Components/Pages/CreatorMap.razor"));
        Assert.Contains("Busy=\"_busy\"", page);
        Assert.Contains("if (_presenter == null || _busy) { return; }", page);
        Assert.Contains("finally { _busy = false; }", page);
        var map = File.ReadAllText(Path.Combine(Root(), "MUD.Web/Components/Shared/RoomMap.razor"));
        Assert.Contains("Busy ? Task.CompletedTask", map);
        Assert.Contains("aria-disabled", map);
    }

    [Fact]
    public void Help_DescribesTheNewControls_AndNoLongerMentionsTheX()
    {
        var page = File.ReadAllText(Path.Combine(Root(), "MUD.Web/Components/Pages/CreatorMap.razor"));
        Assert.DoesNotContain("&times;", page);
        Assert.DoesNotContain("black x", page);
        foreach (var s in new[] { "Click an arrowhead to remove that exit", "click a plain end to add", "cannot be changed here", "edit page", "opens Create room", "switch the map to another level" })
        {
            Assert.Contains(s, page);
        }
    }
}

public class MapLineEndRenderTests
{
    private sealed class NoJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new Microsoft.JSInterop.JSDisconnectedException("test");
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new Microsoft.JSInterop.JSDisconnectedException("test");
    }

    private static async Task<string> Render(RoomMap map, bool busy)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, new AssetVersions(new Microsoft.Extensions.FileProviders.NullFileProvider()));
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<Microsoft.JSInterop.IJSRuntime>(services, new NoJs());
        await using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        await using var renderer = new Microsoft.AspNetCore.Components.Web.HtmlRenderer(provider, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<MUD.Web.Components.Shared.RoomMap>(Microsoft.AspNetCore.Components.ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Map"] = map, ["Busy"] = busy, ["FillFor"] = (Func<MapRect, string>)(_ => "#eee"),
            }));
            return root.ToHtmlString();
        });
    }

    private static RoomRecord Room(string key, int x, int y, params (string, string)[] exits) =>
        new(key, "a", "short " + key, x, y, 0, exits.Select(e => new RoomExitRecord(e.Item1, e.Item2)).ToList());

    [Fact]
    public async Task EachLineEnd_IsAFocusableButton_WithAnAriaLabel_AndTheArrowsAreSvgPathsNotText()
    {
        var map = new RoomMapLayout().Build(new[] { Room("a.study", 0, 1, ("south", "a.hall")), Room("a.hall", 0, 0, ("north", "a.study")), Room("a.far", 5, 5) }, "a", 0);
        var html = await Render(map, false);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "class=\"mud-lineend\"").Count);
        Assert.Contains("role=\"button\" tabindex=\"0\"", html);
        Assert.Contains("aria-label=\"Remove exit south from short a.study to short a.hall\"", html);
        Assert.Contains("aria-label=\"Remove exit north from short a.hall to short a.study\"", html);
        Assert.Contains("markerWidth=\"8\" markerHeight=\"8\"", html);
        var arrowTags = System.Text.RegularExpressions.Regex.Matches(html, "class=\"mud-arrow [^\"]*\"").Count;
        Assert.Equal(map.Arrows.Count, arrowTags);
        Assert.Equal(map.Arrows.Count, System.Text.RegularExpressions.Regex.Matches(html, "<path d=\"" + System.Text.RegularExpressions.Regex.Escape(RoomMapLayout.ArrowPath) + "\"").Count);
    }

    [Fact]
    public async Task WhileBusy_ZonesAndArrowsAreDisabled()
    {
        var map = new RoomMapLayout().Build(new[] { Room("a.o", 0, 0, ("east", "a.e")), Room("a.e", 1, 0) }, "a", 0);
        var idle = await Render(map, false);
        var busy = await Render(map, true);
        Assert.DoesNotContain("mud-busy", idle);
        Assert.Contains("class=\"mud-lineend mud-busy\"", busy);
        Assert.Contains("aria-disabled=\"true\"", busy);
        Assert.DoesNotContain("tabindex=\"0\" aria-label=\"Remove", busy);
        Assert.Equal(map.Arrows.Count, System.Text.RegularExpressions.Regex.Matches(busy, "class=\"mud-arrow [^\"]*mud-busy[^\"]*\"").Count);
    }

    [Fact]
    public async Task MismatchLine_RendersNoZones()
    {
        var map = new RoomMapLayout().Build(new[] { Room("a.o", 0, 0, ("north", "a.e")), Room("a.e", 1, 0) }, "a", 0);
        Assert.DoesNotContain("mud-lineend", await Render(map, false));
    }

    [Fact]
    public async Task LinkArrow_IsAButton_AndCreateArrow_IsALink()
    {
        var map = new RoomMapLayout().Build(new[] { Room("a.o", 0, 0), Room("a.e", 1, 0) }, "a", 0);
        var html = await Render(map, false);
        Assert.Contains("aria-label=\"Link east to short a.e\"", html);
        Assert.Contains("aria-label=\"Add room north of short a.o\"", html);
        Assert.Matches("<a class=\"mud-arrow mud-arrow-gray\"[^>]*href=\"/creator\\?area=a&amp;x=0&amp;y=1&amp;z=0\"", html);
    }
}

public class MapLevelPresenterTests
{
    [Fact]
    public async Task ALevelReachedByAnArrow_IsInTheDropdown_EvenWithNoRooms_AndShowsTheGhost()
    {
        var core = new FakeAdminClient().AddRoom("a.o", "a", 3, 4, 0);
        var p = new CreatorMapPresenter(core, "t");
        await p.LoadAsync("a", 1, default, 3, 4);
        Assert.Equal(new[] { 0, 1 }, p.Levels);
        Assert.Equal(1, p.Z);
        Assert.Empty(p.Map!.Rects);
        Assert.Equal("/creator?area=a&x=3&y=4&z=1", p.Map.Ghost!.CreateHref);
    }

    [Fact]
    public async Task ALevelThatHasARoomThere_HasNoGhost_AndNoDuplicateLevel()
    {
        var core = new FakeAdminClient().AddRoom("a.o", "a", 3, 4, 0).AddRoom("a.p", "a", 3, 4, 1);
        var p = new CreatorMapPresenter(core, "t");
        await p.LoadAsync("a", 1, default, 3, 4);
        Assert.Equal(new[] { 0, 1 }, p.Levels);
        Assert.Null(p.Map!.Ghost);
    }

    [Fact]
    public async Task WithoutAnOrigin_TheLevelListIsAsBefore_AndThereIsNoGhost()
    {
        var core = new FakeAdminClient().AddRoom("a.o", "a", 3, 4, 0);
        var p = new CreatorMapPresenter(core, "t");
        await p.LoadAsync("a", 1);
        Assert.Equal(new[] { 0 }, p.Levels);
        Assert.Null(p.Map!.Ghost);
    }

    [Fact]
    public async Task SwitchingToTheLevelOfAnotherAreaFromABlackUpArrow_LoadsThatAreaAndLevel()
    {
        var core = new FakeAdminClient().AddRoom("a.o", "a", 0, 0, 0, ("up", "b.roof")).AddRoom("b.roof", "b", 7, 7, 5);
        var first = new CreatorMapPresenter(core, "t");
        await first.LoadAsync("a", 0);
        var href = first.Map!.Arrows.Single(a => a.RoomKey == "a.o" && a.Direction == "up").Href!;
        Assert.Equal("/creator/map?area=b&z=5&gx=0&gy=0", href);
        var second = new CreatorMapPresenter(core, "t");
        await second.LoadAsync("b", 5, default, 0, 0);
        Assert.Equal("b", second.Area);
        Assert.Equal(new[] { "b.roof" }, second.Map!.Rects.Select(r => r.Key));
        Assert.NotNull(second.Map.Ghost); // b.roof is at 7,7 and nothing at 0,0
    }

    [Fact]
    public async Task ClickingAnArrowNeverWrites()
    {
        var core = new FakeAdminClient().AddRoom("a.o", "a", 0, 0, 0);
        var p = new CreatorMapPresenter(core, "t");
        await p.LoadAsync("a", 1, default, 0, 0);
        Assert.Empty(core.RoomCreates);
        Assert.Empty(core.RoomUpdates);
    }
}
