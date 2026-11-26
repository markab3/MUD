using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MUD.Contracts;
using MUD.Web.Pages;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

public class RoomCreationServiceTests
{
    private static RoomForm Form(string key = "new", string area = "t") => new()
    {
        Key = key, Short = "a new room", Long = "Long.", Area = area,
        Exits = { new ExitInput { Dir = "north", To = "t.hall" }, new ExitInput() },
    };

    [Fact]
    public async Task Create_SendsTheFullKeyAndTheFormToCore_WithTheToken()
    {
        var core = new FakeAdminClient();
        var result = await new RoomCreationService(core).CreateAsync(Form(), "token-1");
        Assert.Equal(RoomCreationStatus.Created, result.Status);
        Assert.Equal("t.new", result.CreatedKey);
        var sent = Assert.Single(core.RoomCreates);
        Assert.Equal("t.new", sent.Key);
        Assert.Equal("t", sent.Area);
        Assert.Equal("a new room", sent.ShortDescription);
        Assert.Equal("Long.", sent.LongDescription);
        var exit = Assert.Single(sent.Exits);
        Assert.Equal(("north", "t.hall"), (exit.Name, exit.To));
        Assert.Equal(new[] { "token-1" }, core.RoomTokens);
    }

    [Fact]
    public async Task Create_BlankCoordinates_AreSentAsAbsent_NotZero()
    {
        var core = new FakeAdminClient();
        await new RoomCreationService(core).CreateAsync(Form(), "token-1");
        var sent = Assert.Single(core.RoomCreates);
        Assert.False(sent.HasX);
        Assert.False(sent.HasY);
        Assert.False(sent.HasZ);

        var placed = Form();
        placed.X = 0; placed.Y = 3; placed.Z = -1;
        await new RoomCreationService(core).CreateAsync(placed, "token-1");
        var second = core.RoomCreates[1];
        Assert.Equal((0, 3, -1), (second.X, second.Y, second.Z));
        Assert.True(second.HasX);
    }

    [Fact]
    public async Task Create_ExistingKey_IsReportedAsKeyExists()
    {
        var core = new FakeAdminClient { RoomEditResult = AdminResult.AlreadyExists };
        Assert.Equal(RoomCreationStatus.KeyExists, (await new RoomCreationService(core).CreateAsync(Form("hall"), "token-1")).Status);
    }

    [Fact]
    public async Task Create_InvalidReply_CarriesCoreFieldErrors()
    {
        var core = new FakeAdminClient { RoomEditResult = AdminResult.Invalid };
        core.RoomEditErrors.Add(new FieldError { Field = "Short", Message = "Too long." });
        var result = await new RoomCreationService(core).CreateAsync(Form(), "token-1");
        Assert.Equal(RoomCreationStatus.Invalid, result.Status);
        Assert.Contains(result.FieldErrors, e => e.Field == "Short" && e.Message == "Too long.");
    }

    [Fact]
    public async Task Create_ForbiddenResultOrRefusedSession_IsForbidden()
    {
        Assert.Equal(RoomCreationStatus.Forbidden,
            (await new RoomCreationService(new FakeAdminClient { RoomEditResult = AdminResult.Forbidden }).CreateAsync(Form(), "t")).Status);
        Assert.Equal(RoomCreationStatus.Forbidden,
            (await new RoomCreationService(new FakeAdminClient { RefuseRoomCalls = true }).CreateAsync(Form(), "t")).Status);
    }

    [Theory]
    [InlineData(AdminResult.AuditFailed)]
    [InlineData(AdminResult.Failed)]
    [InlineData(AdminResult.Refused)]
    public async Task Create_OtherRefusals_AreFailed(AdminResult result)
    {
        var core = new FakeAdminClient { RoomEditResult = result };
        Assert.Equal(RoomCreationStatus.Failed, (await new RoomCreationService(core).CreateAsync(Form(), "token-1")).Status);
    }

    [Fact]
    public async Task Create_InvalidLocalKey_IsRejectedHere_AndCoreIsNotCalled()
    {
        var core = new FakeAdminClient();
        var result = await new RoomCreationService(core).CreateAsync(Form("Bad Key"), "token-1");
        Assert.Equal(RoomCreationStatus.Invalid, result.Status);
        Assert.Contains(result.FieldErrors, e => e.Field == "Key");
        Assert.Empty(core.RoomCreates);
    }

    [Fact]
    public async Task Create_WhenCoreIsUnavailable_Throws()
    {
        await Assert.ThrowsAsync<CoreUnavailableException>(() =>
            new RoomCreationService(new FakeAdminClient { Unavailable = true }).CreateAsync(Form(), "token-1"));
    }
}

public class RoomValidatorParityTests
{
    // The importer, core and the website form all use MUD.Data.RoomRules; this table pins the verdicts so a change in one place
    // shows up in all (the importer side is covered by RoomRulesCharacterizationTests in MUD.Tests).
    [Theory]
    [InlineData("hall", true)]
    [InlineData("hall_2", true)]
    [InlineData("Hall", false)]
    [InlineData("a.b", false)]
    [InlineData("", false)]
    public void LocalKey(string key, bool ok) => Assert.Equal(ok, MUD.Data.RoomRules.CheckLocalKey(key) == null);

    [Theory]
    [InlineData("t", true)]
    [InlineData("a.b.c", true)]
    [InlineData("T", false)]
    [InlineData("a..b", false)]
    public void AreaPrefix(string prefix, bool ok) => Assert.Equal(ok, MUD.Data.RoomRules.CheckAreaPrefix(prefix) == null);

    [Fact]
    public void WebPagesDoNotReferenceMudCore()
    {
        var referenced = typeof(RoomEditPresenter).Assembly.GetReferencedAssemblies().Select(a => a.Name);
        Assert.DoesNotContain("MUD.Core", referenced);
    }
}
