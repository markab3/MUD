using System.ComponentModel.DataAnnotations;
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

public class CreatorPolicyTests
{
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

    private static ClaimsPrincipal User(AccountRole role) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "Someone"),
            new Claim(ClaimTypes.Role, role.ToString()),
        }, "test"));

    [Fact]
    public async Task Player_IsForbiddenNotChallenged()
    {
        var r = await Evaluate(User(AccountRole.Player));
        Assert.True(r.Forbidden);
        Assert.False(r.Challenged);
    }

    [Fact]
    public async Task Creator_IsAllowed() => Assert.True((await Evaluate(User(AccountRole.Creator))).Succeeded);

    [Fact]
    public async Task Admin_IsAllowed() => Assert.True((await Evaluate(User(AccountRole.Admin))).Succeeded);

    [Fact]
    public async Task Anonymous_IsChallenged()
    {
        var r = await Evaluate(null);
        Assert.True(r.Challenged);
        Assert.False(r.Succeeded);
    }

    [Fact]
    public void CreatorPage_RequiresPolicy()
    {
        var attr = (AuthorizeAttribute?)Attribute.GetCustomAttribute(typeof(CreatorModel), typeof(AuthorizeAttribute));
        Assert.NotNull(attr);
        Assert.Equal(WebPolicies.CreatorOrAdmin, attr!.Policy);
    }
}

public class RoomFormValidationTests
{
    private static RoomForm Valid() => new()
    {
        Key = "entry",
        Short = "the entrance hall",
        Long = "A grand hall.",
        Area = "tower_of_aellioc",
        Exits = { new ExitInput { Dir = "north", To = "tower_of_aellioc.stairs" }, new ExitInput() },
    };

    private static List<ValidationResult> Validate(RoomForm f)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(f, new ValidationContext(f), results, validateAllProperties: true);
        for (var i = 0; i < f.Exits.Count; i++)
        {
            Validator.TryValidateObject(f.Exits[i], new ValidationContext(f.Exits[i]), results, validateAllProperties: true);
        }
        return results;
    }

    [Fact]
    public void ValidForm_Passes() => Assert.Empty(Validate(Valid()));

    [Theory]
    [InlineData("Key")]
    [InlineData("Short")]
    [InlineData("Long")]
    [InlineData("Area")]
    public void RequiredFields_AreEnforced(string field)
    {
        var f = Valid();
        typeof(RoomForm).GetProperty(field)!.SetValue(f, null);
        Assert.Contains(Validate(f), r => r.MemberNames.Contains(field));
    }

    [Fact]
    public void LengthLimits_AreEnforced()
    {
        var f = Valid();
        f.Short = new string('x', 101);
        f.Long = new string('x', 4001);
        f.Key = new string('a', 65);
        var members = Validate(f).SelectMany(r => r.MemberNames).ToList();
        Assert.Contains("Short", members);
        Assert.Contains("Long", members);
        Assert.Contains("Key", members);
    }

    [Theory]
    [InlineData("Entry")]
    [InlineData("has space")]
    [InlineData("a.b")]
    public void BadKey_IsRejected(string key)
    {
        var f = Valid();
        f.Key = key;
        Assert.Contains(Validate(f), r => r.MemberNames.Contains("Key"));
    }

    [Fact]
    public void HalfFilledExit_IsRejected()
    {
        var f = Valid();
        f.Exits[1] = new ExitInput { Dir = "south" };
        Assert.Contains(Validate(f), r => r.MemberNames.Contains("Exits[1].To"));
    }

    [Fact]
    public void BadExitTarget_IsRejected()
    {
        var f = Valid();
        f.Exits[0].To = "Not A Key";
        Assert.Contains(Validate(f), r => r.MemberNames.Contains("To"));
    }

    [Fact]
    public void TooManyExits_IsRejected()
    {
        var f = Valid();
        for (var i = 0; i < RoomForm.ExitRows; i++) { f.Exits.Add(new ExitInput()); }
        Assert.Contains(Validate(f), r => r.MemberNames.Contains("Exits"));
    }
}
