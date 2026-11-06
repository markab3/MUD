using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MUD.Web.Pages;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

public class RoomMapLayoutTests
{
    private static RoomRecord Room(string key, int? x, int? y, int? z = 0, string area = "a", params (string Name, string To)[] exits) =>
        new(key, area, "short " + key, x, y, z, exits.Select(e => new RoomExitRecord(e.Name, e.To)).ToList());

    private static RoomMap Build(string area, int z, params RoomRecord[] rooms) => new RoomMapLayout().Build(rooms, area, z);

    [Fact]
    public void NorthIsUp_HigherYIsHigherOnScreen()
    {
        var map = Build("a", 0, Room("a.s", 0, 0), Room("a.n", 0, 1), Room("a.e", 1, 0));
        var north = map.Rects.Single(r => r.Key == "a.n");
        var south = map.Rects.Single(r => r.Key == "a.s");
        var east = map.Rects.Single(r => r.Key == "a.e");
        Assert.True(north.Y < south.Y);
        Assert.Equal(north.X, south.X);
        Assert.True(east.X > south.X);
        Assert.Equal(east.Y, south.Y);
    }

    [Fact]
    public void NorthExit_LeavesTopEdgeAndLandsOnBottomEdgeOfDestination()
    {
        var map = Build("a", 0, Room("a.s", 0, 0, 0, "a", ("north", "a.n")), Room("a.n", 0, 1));
        var line = Assert.Single(map.Lines);
        var s = map.Rects.Single(r => r.Key == "a.s");
        var n = map.Rects.Single(r => r.Key == "a.n");
        Assert.Equal(s.Y, line.Points[0].Y);
        Assert.Equal(n.Y + n.H, line.Points[^1].Y);
        Assert.True(line.Points[0].Y > line.Points[^1].Y);
    }

    [Fact]
    public void DiagonalExit_UsesCorners()
    {
        var map = Build("a", 0, Room("a.o", 0, 0, 0, "a", ("ne", "a.t")), Room("a.t", 1, 1));
        var line = Assert.Single(map.Lines);
        var o = map.Rects.Single(r => r.Key == "a.o");
        var t = map.Rects.Single(r => r.Key == "a.t");
        Assert.Equal((o.X + o.W, o.Y), line.Points[0]);
        Assert.Equal((t.X, t.Y + t.H), line.Points[^1]);
        Assert.False(line.Warning);
    }

    [Fact]
    public void ExitWithMatchingReverse_IsOneTwoWayLine()
    {
        var map = Build("a", 0,
            Room("a.w", 0, 0, 0, "a", ("e", "a.e")),
            Room("a.e", 1, 0, 0, "a", ("west", "a.w")));
        var line = Assert.Single(map.Lines);
        Assert.True(line.ArrowStart);
        Assert.True(line.ArrowEnd);
        Assert.Empty(map.Mismatches);
    }

    [Fact]
    public void ExitWithoutReverse_IsOneWayWithSingleArrowhead()
    {
        var map = Build("a", 0, Room("a.w", 0, 0, 0, "a", ("east", "a.e")), Room("a.e", 1, 0));
        var line = Assert.Single(map.Lines);
        Assert.False(line.ArrowStart);
        Assert.True(line.ArrowEnd);
        Assert.Equal("a.w", line.FromKey);
        Assert.Equal("a.e", line.ToKey);
    }

    [Fact]
    public void ReverseThatIsNotOppositeDirection_IsNotPaired()
    {
        // b -> a exists but via north, not west: two separate one-way lines.
        var map = Build("a", 0,
            Room("a.a", 0, 0, 0, "a", ("east", "a.b")),
            Room("a.b", 1, 0, 0, "a", ("north", "a.a")));
        Assert.Equal(2, map.Lines.Count);
        Assert.All(map.Lines, l => Assert.False(l.ArrowStart));
    }

    [Fact]
    public void DirectionDisagreeingWithCoordinates_IsFlaggedDashedAndListed()
    {
        // "north" exit, but destination is to the south by coordinates.
        var map = Build("a", 0, Room("a.a", 0, 1, 0, "a", ("north", "a.b")), Room("a.b", 0, 0));
        var line = Assert.Single(map.Lines);
        Assert.True(line.Warning);
        var m = Assert.Single(map.Mismatches);
        Assert.Equal(("a.a", "north", "a.b"), (m.FromKey, m.Direction, m.ToKey));
    }

    [Fact]
    public void ConsistentExits_AreNotFlagged()
    {
        var map = Build("a", 0, Room("a.a", 0, 0, 0, "a", ("north", "a.b")), Room("a.b", 0, 1));
        Assert.Empty(map.Mismatches);
        Assert.False(map.Lines.Single().Warning);
    }

    [Fact]
    public void RoomsWithoutCoordinates_AreUnplacedAndNotDrawn()
    {
        var map = Build("a", 0, Room("a.p", 0, 0), Room("a.u", null, null, null), Room("a.v", 3, null));
        Assert.Equal(new[] { "a.u", "a.v" }, map.Unplaced.Select(u => u.Key));
        Assert.Equal(new[] { "a.p" }, map.Rects.Select(r => r.Key));
    }

    [Fact]
    public void ExitToOtherArea_IsStubWithTargetKey()
    {
        var map = Build("a", 0, Room("a.a", 0, 0, 0, "a", ("east", "b.x")), Room("b.x", 1, 0, 0, "b"));
        Assert.Empty(map.Lines);
        var stub = Assert.Single(map.Stubs);
        Assert.Equal("b.x", stub.Target);
        Assert.False(stub.Missing);
        Assert.True(stub.Points[1].X > stub.Points[0].X);
    }

    [Fact]
    public void ExitToMissingRoom_IsStubMarkedMissing()
    {
        var map = Build("a", 0, Room("a.a", 0, 0, 0, "a", ("north", "a.ghost")));
        var stub = Assert.Single(map.Stubs);
        Assert.True(stub.Missing);
        Assert.Equal("a.ghost", stub.Target);
        Assert.True(stub.Points[1].Y < stub.Points[0].Y); // north is up
    }

    [Fact]
    public void UpDownAndNamedExits_AreBadgesWithDestinationKey()
    {
        var map = Build("a", 0, Room("a.a", 0, 0, 0, "a", ("up", "a.roof"), ("down", "a.cellar"), ("enter hut", "a.hut"), ("d", "gone.room")));
        Assert.Empty(map.Lines);
        Assert.Empty(map.Stubs);
        Assert.Equal(4, map.Badges.Count);
        Assert.Contains(map.Badges, b => b.Text == RoomMapLayout.Up && b.Target == "a.roof");
        Assert.Contains(map.Badges, b => b.Text == RoomMapLayout.Down && b.Target == "a.cellar");
        Assert.Contains(map.Badges, b => b.Text == "enter hut" && b.Target == "a.hut");
        Assert.Contains(map.Badges, b => b.Text == RoomMapLayout.Down && b.Target == "gone.room" && b.Missing);
        Assert.Equal(4, map.Badges.Select(b => b.Y).Distinct().Count());
    }

    [Fact]
    public void OnlyRoomsOfSelectedAreaAndLevelAreDrawn()
    {
        var map = Build("a", 1, Room("a.g", 0, 0, 0), Room("a.up", 0, 0, 1), Room("b.x", 0, 0, 1, "b"));
        Assert.Equal(new[] { "a.up" }, map.Rects.Select(r => r.Key));
    }

    [Fact]
    public void ExitToOtherLevel_IsStub()
    {
        var map = Build("a", 0, Room("a.g", 0, 0, 0, "a", ("north", "a.up")), Room("a.up", 0, 1, 1));
        Assert.Empty(map.Lines);
        Assert.Equal("a.up", Assert.Single(map.Stubs).Target);
    }

    [Fact]
    public void EmptyLevel_ReturnsEmptyMapButKeepsUnplaced()
    {
        var map = Build("a", 5, Room("a.u", null, null, null));
        Assert.Empty(map.Rects);
        Assert.Single(map.Unplaced);
    }

    [Fact]
    public void RoomRecord_MapsTheRoomCoreReturns()
    {
        var room = new MUD.Contracts.RoomDetail
        {
            Key = "castle.hall", ShortDescription = "a hall", LongDescription = "never used by the map", Area = "castle", X = 1,
        };
        room.Exits.Add(new MUD.Contracts.RoomExit { Name = "north", To = "castle.keep" });
        var r = RoomRecord.From(room);
        Assert.Equal("castle.hall", r.Key);
        Assert.Equal("castle", r.Area);
        Assert.Equal("a hall", r.Short);
        Assert.Equal(1, r.X);
        Assert.Null(r.Y); // absent in the message means unplaced, never 0
        Assert.Null(r.Z);
        var e = Assert.Single(r.Exits);
        Assert.Equal(("north", "castle.keep"), (e.Name, e.To));
    }

    [Fact]
    public void RoomRecord_EmptyArea_IsNoArea() => Assert.Null(RoomRecord.From(new MUD.Contracts.RoomDetail { Key = "loose" }).Area);

    [Fact]
    public void RoomRecord_ZeroCoordinatesStayPlaced()
    {
        var room = new MUD.Contracts.RoomDetail { Key = "a.x", Area = "a", X = 0, Y = 0, Z = 0 };
        var r = RoomRecord.From(room);
        Assert.Equal((0, 0, 0), (r.X, r.Y, r.Z));
    }
}

public class CreatorMapPageTests
{
    private static FakeAdminClient Core(params RoomRecord[] rooms)
    {
        var core = new FakeAdminClient();
        foreach (var r in rooms) { core.AddRoom(r.Key, r.Area ?? string.Empty, r.X, r.Y, r.Z); }
        return core;
    }

    private static CreatorMapModel Make(FakeAdminClient core, string token = "token-1")
    {
        var page = new CreatorMapModel(core);
        var http = new DefaultHttpContext();
        http.Request.Headers.Cookie = SessionAuthentication.CookieName + "=" + token;
        page.PageContext = new Microsoft.AspNetCore.Mvc.RazorPages.PageContext { HttpContext = http };
        return page;
    }

    private static RoomRecord R(string key, string area, int? x, int? y, int? z) =>
        new(key, area, "s " + key, x, y, z, Array.Empty<RoomExitRecord>());

    [Fact]
    public void Page_RequiresCreatorOrAdminPolicy()
    {
        var attr = (AuthorizeAttribute?)Attribute.GetCustomAttribute(typeof(CreatorMapModel), typeof(AuthorizeAttribute));
        Assert.NotNull(attr);
        Assert.Equal(WebPolicies.CreatorOrAdmin, attr!.Policy);
    }

    [Fact]
    public async Task Page_DefaultsToFirstAreaAndLevelZero()
    {
        var core = Core(R("b.x", "b", 0, 0, 0), R("a.x", "a", 0, 0, 0), R("a.y", "a", 0, 0, 2));
        var page = Make(core, "abc");
        await page.OnGetAsync(null, null);
        Assert.Equal(new[] { "abc" }, core.RoomTokens);
        Assert.Equal(new[] { "a", "b" }, page.Areas);
        Assert.Equal("a", page.Area);
        Assert.Equal(0, page.Z);
        Assert.Equal(new[] { 0, 2 }, page.Levels);
        Assert.Equal(new[] { "a.x" }, page.Map!.Rects.Select(r => r.Key));
    }

    [Fact]
    public async Task Page_UnknownAreaFallsBackToFirst()
    {
        var page = Make(Core(R("a.x", "a", 0, 0, 0)));
        await page.OnGetAsync("nope", 0);
        Assert.Equal("a", page.Area);
    }

    [Fact]
    public async Task Page_NoRooms_RendersNothing()
    {
        var page = Make(Core());
        Assert.IsType<Microsoft.AspNetCore.Mvc.RazorPages.PageResult>(await page.OnGetAsync(null, null));
        Assert.Empty(page.Areas);
        Assert.Null(page.Map);
        Assert.Null(page.Error);
    }

    [Fact]
    public async Task Page_UnplacedRoom_IsListedAsUnplaced_NotDrawnAtZero()
    {
        var page = Make(Core(R("a.x", "a", 0, 0, 0), R("a.u", "a", null, null, null)));
        await page.OnGetAsync(null, null);
        Assert.Equal(new[] { "a.x" }, page.Map!.Rects.Select(r => r.Key));
        Assert.Equal(new[] { "a.u" }, page.Map.Unplaced.Select(u => u.Key));
    }

    [Fact]
    public async Task Page_WhenCoreIsUnavailable_ShowsAFixedMessage_AndNoMap()
    {
        var page = Make(new FakeAdminClient { Unavailable = true });
        await page.OnGetAsync(null, null);
        Assert.Equal(CreatorMapModel.UnavailableError, page.Error);
        Assert.Null(page.Map);
    }

    [Fact]
    public async Task Page_WhenCoreRefusesTheSession_GoesToAccessDenied()
    {
        var page = Make(new FakeAdminClient { RefuseRoomCalls = true });
        var result = await page.OnGetAsync(null, null);
        Assert.Equal("/access-denied", Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectResult>(result).Url);
    }

    [Fact]
    public void RoomUrl_PointsAtEditPage() => Assert.Equal("/creator/rooms/castle.hall", CreatorMapModel.RoomUrl("castle.hall"));

    private static async Task<PolicyAuthorizationResult> Evaluate(ClaimsPrincipal? user)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(WebPolicies.Configure);
        var sp = services.BuildServiceProvider();
        var policy = await sp.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(WebPolicies.CreatorOrAdmin);
        var evaluator = new PolicyEvaluator(sp.GetRequiredService<IAuthorizationService>());
        var ctx = new DefaultHttpContext();
        if (user != null) { ctx.User = user; }
        var auth = user == null
            ? AuthenticateResult.NoResult()
            : AuthenticateResult.Success(new AuthenticationTicket(user, "test"));
        return await evaluator.AuthorizeAsync(policy!, auth, ctx, null);
    }

    [Fact]
    public async Task Anonymous_IsChallengedSoRedirectedToLogin() => Assert.True((await Evaluate(null)).Challenged);

    [Fact]
    public async Task PlainPlayer_IsForbiddenSoAccessDenied()
    {
        var player = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "Player") }, "test"));
        var r = await Evaluate(player);
        Assert.True(r.Forbidden);
        Assert.False(r.Challenged);
    }

    [Fact]
    public async Task Creator_IsAllowed()
    {
        var c = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "Creator") }, "test"));
        Assert.True((await Evaluate(c)).Succeeded);
    }
}
