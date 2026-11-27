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
    public void ExitInAdirection_MakesThatArrowBlack_AndOthersStayGray()
    {
        var map = Build(0, Room("a.o", 0, 0, 0, "a", ("north", "a.n"), ("e", "gone.room")), Room("a.n", 0, 1));
        var n = Arrow(map, "a.o", "north");
        Assert.Equal((MapArrowKind.Go, true), (n.Kind, n.Black));
        Assert.Equal("Go to short a.n (north)", n.Label);
        var e = Arrow(map, "a.o", "east"); // short name works as well as the full word
        Assert.Equal((MapArrowKind.Go, true), (e.Kind, e.Black));
        Assert.Null(e.Href); // the target room does not exist: nothing to follow
        Assert.Equal(MapArrowKind.Add, Arrow(map, "a.o", "south").Kind);
        Assert.False(Arrow(map, "a.o", "south").Black);
    }

    [Fact]
    public void BlackArrow_GoesToTheEditPage_WhenTheTargetIsOnThisLevel()
    {
        var map = Build(0, Room("a.o", 0, 0, 0, "a", ("north", "a.n")), Room("a.n", 0, 1));
        Assert.Equal("/creator/rooms/a.n", Arrow(map, "a.o", "north").Href);
    }

    [Fact]
    public void BlackArrow_GoesToTheMapOfItsLevel_WhenTheTargetIsElsewhere()
    {
        var map = Build(0, Room("a.o", 0, 0, 0, "a", ("north", "a.up"), ("east", "b.far")),
            Room("a.up", 0, 1, 2), Room("b.far", 9, 9, 4, "b"));
        Assert.Equal("/creator/map?area=a&z=2&center=a.up", Arrow(map, "a.o", "north").Href);
        Assert.Equal("/creator/map?area=b&z=4&center=b.far", Arrow(map, "a.o", "east").Href);
    }

    [Fact]
    public void BlackArrow_ToAnUnplacedRoom_GoesToItsEditPage()
    {
        var map = Build(0, Room("a.o", 0, 0, 0, "a", ("north", "a.u")), Room("a.u", null, null, null));
        Assert.Equal("/creator/rooms/a.u", Arrow(map, "a.o", "north").Href);
    }

    // The x rule.
    [Fact]
    public void X_AdjacentCoordinateRoom_WithNoExit_IsAWireMarker()
    {
        var map = Build(0, Room("a.o", 0, 0), Room("a.e", 1, 0), Room("a.ne", 1, 1));
        var x = Arrow(map, "a.o", "east");
        Assert.Equal((MapArrowKind.Wire, true, "a.e", MapRoomGlyph), (x.Kind, x.Black, x.TargetKey, x.Glyph));
        Assert.Null(x.Href);
        Assert.Equal("Connect east of short a.o to short a.e", x.Label);
        Assert.Equal("a.ne", Arrow(map, "a.o", "northeast").TargetKey);
        Assert.Equal(MapArrowKind.Add, Arrow(map, "a.o", "west").Kind);
        // Both ends offer it: the one-way story is the user's to complete.
        Assert.Equal("a.o", Arrow(map, "a.e", "west").TargetKey);
    }

    private static string MapRoomGlyph => RoomMapLayout.WireGlyph;

    [Fact]
    public void X_NotDrawn_WhenTheRoomAlreadyHasAnExitThatWay()
    {
        var map = Build(0, Room("a.o", 0, 0, 0, "a", ("east", "a.e")), Room("a.e", 1, 0));
        Assert.Equal(MapArrowKind.Go, Arrow(map, "a.o", "east").Kind);
        Assert.Equal(MapArrowKind.Wire, Arrow(map, "a.e", "west").Kind); // the far end still lacks the way back
    }

    [Fact]
    public void X_NotDrawn_ForARoomOfAnotherAreaOrLevelAtTheAdjacentCoordinates()
    {
        var map = Build(0, Room("a.o", 0, 0), Room("a.up", 1, 0, 1), Room("b.x", 0, 1, 0, "b"));
        Assert.Equal(MapArrowKind.Add, Arrow(map, "a.o", "east").Kind);
        Assert.Equal(MapArrowKind.Add, Arrow(map, "a.o", "north").Kind);
    }

    [Fact]
    public void X_OneWayLink_FromARoomAtNonAdjacentCoordinates_PointsBackInTheOppositeDirection()
    {
        // b.far (other area) has an east exit into a.o: a.o's west arrow becomes an x to b.far.
        var map = Build(0, Room("a.o", 0, 0), Room("b.far", 9, 9, 0, "b", ("east", "a.o")));
        var x = Arrow(map, "a.o", "west");
        Assert.Equal((MapArrowKind.Wire, "b.far"), (x.Kind, x.TargetKey));
        Assert.Equal(MapArrowKind.Add, Arrow(map, "a.o", "east").Kind);
    }

    [Fact]
    public void X_OneWayLink_NotDrawn_WhenTheRoomAlreadyHasThatExit()
    {
        var map = Build(0, Room("a.o", 0, 0, 0, "a", ("west", "b.far")), Room("b.far", 9, 9, 0, "b", ("east", "a.o")));
        Assert.Equal(MapArrowKind.Go, Arrow(map, "a.o", "west").Kind);
    }

    [Fact]
    public void NoX_WhenThereIsNoRoomThatWay()
    {
        var map = Build(0, Room("a.o", 0, 0), Room("a.far", 5, 5));
        Assert.DoesNotContain(map.Arrows, a => a.Kind == MapArrowKind.Wire);
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

public class MapWireTests
{
    private static FakeAdminClient Core() => new FakeAdminClient()
        .AddRoom("a.o", "a", 0, 0, 0, ("north", "a.n"))
        .AddRoom("a.e", "a", 1, 0, 0);

    private static CreatorMapPresenter Make(FakeAdminClient core) => new(core, "tok");

    [Fact]
    public async Task Ok_AddsExactlyOneExit_FromTheClickedRoom_ThroughUpdateRoom_AndNeverTheReverse()
    {
        var core = Core();
        Assert.True(await Make(core).WireAsync("a.o", "east", "a.e"));
        var sent = Assert.Single(core.RoomUpdates);
        Assert.Equal("a.o", sent.Key);
        Assert.Equal(new[] { ("north", "a.n"), ("east", "a.e") }, sent.Exits.Select(e => (e.Name, e.To)));
        Assert.Empty(core.RoomCreates);
        Assert.Empty(core.Rooms["a.e"].Exits); // no reverse exit
        Assert.Equal(new[] { ("north", "a.n"), ("east", "a.e") }, core.Rooms["a.o"].Exits.Select(e => (e.Name, e.To)));
    }

    [Fact]
    public async Task Ok_SendsTheWholeForm_SoCoreDiffsOnlyTheExit()
    {
        var core = Core();
        core.Rooms["a.o"].ShortDescription = "Hall";
        core.Rooms["a.o"].LongDescription = "A hall.";
        core.Rooms["a.o"].Features.Add(new RoomFeature { Short = "lamp", Long = "A lamp." });
        Assert.True(await Make(core).WireAsync("a.o", "east", "a.e"));
        var sent = core.RoomUpdates.Single();
        Assert.Equal("Hall", sent.ShortDescription);
        Assert.Equal("A hall.", sent.LongDescription);
        Assert.Equal("a", sent.Area);
        Assert.Equal((0, 0, 0), (sent.X, sent.Y, sent.Z));
        Assert.True(sent.FeaturesPresent);
        Assert.Equal("lamp", Assert.Single(sent.Features).Short);
    }

    [Fact]
    public async Task Ok_WritesTheFullWordDirectionName_EvenForAShortName()
    {
        var core = Core();
        await Make(core).WireAsync("a.o", "e", "a.e");
        Assert.Equal("east", core.RoomUpdates.Single().Exits.Last().Name);
    }

    [Fact]
    public async Task Ok_UsesTheFreshRoom_NotTheMap_AndTheCircuitToken()
    {
        var core = Core();
        core.Rooms["a.o"].ShortDescription = "Changed since the map was drawn";
        await Make(core).WireAsync("a.o", "east", "a.e");
        Assert.Equal("Changed since the map was drawn", core.RoomUpdates.Single().ShortDescription);
        Assert.Contains("tok", core.RoomTokens);
        Assert.All(core.RoomTokens, t => Assert.Equal("tok", t));
    }

    [Fact]
    public async Task Ok_SetsTheNotice()
    {
        var p = Make(Core());
        Assert.True(await p.WireAsync("a.o", "east", "a.e"));
        Assert.Equal(Messages.MapWired, p.WireNotice);
        Assert.Null(p.WireError);
    }

    [Fact]
    public async Task Changed_TheDirectionIsNoLongerFree_IsRefusedWithoutWriting()
    {
        var core = Core();
        core.AddRoom("a.o", "a", 0, 0, 0, ("north", "a.n"), ("e", "a.other")); // someone added an east exit meanwhile (short alias)
        var p = Make(core);
        Assert.False(await p.WireAsync("a.o", "east", "a.e"));
        Assert.Equal(Messages.MapWireChanged, p.WireError);
        Assert.Empty(core.RoomUpdates);
    }

    [Fact]
    public async Task Changed_TheRoomIsGone_IsRefusedWithoutWriting()
    {
        var core = Core();
        core.Rooms.Remove("a.o");
        var p = Make(core);
        Assert.False(await p.WireAsync("a.o", "east", "a.e"));
        Assert.Equal(Messages.MapWireChanged, p.WireError);
        Assert.Empty(core.RoomUpdates);
    }

    [Theory]
    [InlineData(AdminResult.Refused)]
    [InlineData(AdminResult.NotFound)]
    [InlineData(AdminResult.NoChange)]
    public async Task CoreAnswers_RefusedNotFoundNoChange_ShowTheChangedMessage(AdminResult result)
    {
        var core = Core();
        core.RoomEditResult = result;
        var p = Make(core);
        Assert.False(await p.WireAsync("a.o", "east", "a.e"));
        Assert.Equal(Messages.MapWireChanged, p.WireError);
    }

    [Fact]
    public async Task KeyValidation_CoreInvalid_ShowsAFixedMessage_NotCoresText()
    {
        var core = Core();
        core.RoomEditResult = AdminResult.Invalid;
        core.RoomEditErrors.Add(new FieldError { Field = "Exits[1].To", Message = "secret detail <b>" });
        var p = Make(core);
        Assert.False(await p.WireAsync("a.o", "east", "a.e"));
        Assert.Equal(Messages.MapWireInvalid, p.WireError);
        Assert.DoesNotContain("secret", p.WireError);
    }

    [Fact]
    public async Task Unavailable_ShowsTheFixedMessage()
    {
        var core = Core();
        core.Unavailable = true;
        var p = Make(core);
        Assert.False(await p.WireAsync("a.o", "east", "a.e"));
        Assert.Equal(Messages.Unavailable, p.WireError);
    }

    [Fact]
    public async Task RoleRefused_Exception_ShowsTheForbiddenMessage()
    {
        var core = Core();
        core.RefuseRoomCalls = true;
        var p = Make(core);
        Assert.False(await p.WireAsync("a.o", "east", "a.e"));
        Assert.Equal(Messages.RoomForbidden, p.WireError);
        Assert.Empty(core.RoomUpdates);
    }

    [Fact]
    public async Task RoleRefused_Reply_ShowsTheForbiddenMessage()
    {
        var core = Core();
        core.RoomEditResult = AdminResult.Forbidden;
        var p = Make(core);
        Assert.False(await p.WireAsync("a.o", "east", "a.e"));
        Assert.Equal(Messages.RoomForbidden, p.WireError);
    }

    [Fact]
    public async Task AuditFailed_AndAnyOtherAnswer_ShowTheirFixedMessages()
    {
        var core = Core();
        core.RoomEditResult = AdminResult.AuditFailed;
        var p = Make(core);
        Assert.False(await p.WireAsync("a.o", "east", "a.e"));
        Assert.Equal(Messages.RoomAuditFailed, p.WireError);
        core.RoomEditResult = AdminResult.AlreadyExists;
        Assert.False(await p.WireAsync("a.o", "east", "a.e"));
        Assert.Equal(Messages.RoomSaveFailed, p.WireError);
    }

    [Fact]
    public async Task BadInput_NotACompassDirection_OrNoTarget_IsRefusedBeforeCallingCore()
    {
        var core = Core();
        var p = Make(core);
        Assert.False(await p.WireAsync("a.o", "up", "a.e"));
        Assert.False(await p.WireAsync("a.o", "east", " "));
        Assert.Equal(Messages.MapWireChanged, p.WireError);
        Assert.Empty(core.RoomTokens);
    }

    [Fact]
    public async Task AfterAWire_TheXBecomesABlackArrow()
    {
        var core = Core();
        var first = Make(core);
        await first.LoadAsync("a", 0);
        Assert.Equal(MapArrowKind.Wire, first.Map!.Arrows.Single(a => a.RoomKey == "a.o" && a.Direction == "east").Kind);
        await Make(core).WireAsync("a.o", "east", "a.e");
        var second = Make(core);
        await second.LoadAsync("a", 0);
        var arrow = second.Map!.Arrows.Single(a => a.RoomKey == "a.o" && a.Direction == "east");
        Assert.Equal((MapArrowKind.Go, true), (arrow.Kind, arrow.Black));
        // The far end still offers its own x: the way back is a second click.
        Assert.Equal(MapArrowKind.Wire, second.Map.Arrows.Single(a => a.RoomKey == "a.e" && a.Direction == "west").Kind);
    }

    [Fact]
    public async Task ClickingBothEnds_WiresTheWayBack_OneDirectionPerClick()
    {
        var core = Core();
        await Make(core).WireAsync("a.o", "east", "a.e");
        Assert.True(await Make(core).WireAsync("a.e", "west", "a.o"));
        Assert.Equal(2, core.RoomUpdates.Count);
        Assert.Equal(("west", "a.o"), Assert.Single(core.Rooms["a.e"].Exits.Select(e => (e.Name, e.To))));
    }

    [Fact]
    public void CarryWire_KeepsTheAnswerOnTheRedrawnPresenter()
    {
        var p = Make(Core());
        p.CarryWire("n", "e");
        Assert.Equal(("n", "e"), (p.WireNotice, p.WireError));
    }

    [Fact]
    public void MapPage_StaysUnderCreatorOrAdmin_AndHandlesWireClicksOnTheServer()
    {
        var page = typeof(MUD.Web.Components.Pages.CreatorMap);
        var auth = page.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true).Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Single();
        Assert.Equal(WebPolicies.CreatorOrAdmin, auth.Policy);
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
