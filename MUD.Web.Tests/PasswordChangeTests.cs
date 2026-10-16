using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MUD.Core.Repositories;
using MUD.Web.Pages;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

public class PasswordChangeServiceTests
{
    private static (AccountService Service, FakeAccountStore Store) Make()
    {
        var store = new FakeAccountStore().Add("Holen", "oldsecret");
        return (new AccountService(store), store);
    }

    [Fact]
    public void Success_StoresNewPasswordUnderNameKey()
    {
        var (svc, store) = Make();
        Assert.Equal(PasswordChangeResult.Changed, svc.ChangePassword("Holen", "oldsecret", "newsecret", "newsecret"));
        Assert.Equal(new[] { ("holen", "newsecret") }, store.Updates);
        Assert.NotNull(svc.Authenticate("Holen", "newsecret"));
        Assert.Null(svc.Authenticate("Holen", "oldsecret"));
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("")]
    public void WrongCurrent_IsRejected_AndNothingStored(string current)
    {
        var (svc, store) = Make();
        Assert.Equal(PasswordChangeResult.WrongCurrent, svc.ChangePassword("Holen", current, "newsecret", "newsecret"));
        Assert.Empty(store.Updates);
    }

    [Fact]
    public void WrongCurrent_IsReportedBeforeRules()
    {
        var (svc, _) = Make();
        Assert.Equal(PasswordChangeResult.WrongCurrent, svc.ChangePassword("Holen", "wrong", "x", "y"));
    }

    [Fact]
    public void UnknownUser_IsWrongCurrent() =>
        Assert.Equal(PasswordChangeResult.WrongCurrent, Make().Service.ChangePassword("nobody", "a", "newsecret", "newsecret"));

    [Fact]
    public void Mismatch_IsRejected()
    {
        var (svc, store) = Make();
        Assert.Equal(PasswordChangeResult.Mismatch, svc.ChangePassword("Holen", "oldsecret", "newsecret", "newsecreT"));
        Assert.Empty(store.Updates);
    }

    [Fact]
    public void SameAsCurrent_IsRejected()
    {
        var (svc, store) = Make();
        Assert.Equal(PasswordChangeResult.Same, svc.ChangePassword("Holen", "oldsecret", "oldsecret", "oldsecret"));
        Assert.Empty(store.Updates);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("has\tcontrol")]
    public void RuleViolations_AreInvalid(string pw)
    {
        var (svc, store) = Make();
        Assert.Equal(PasswordChangeResult.Invalid, svc.ChangePassword("Holen", "oldsecret", pw, pw));
        Assert.Empty(store.Updates);
    }

    [Fact]
    public void StoreFailure_IsReported()
    {
        var (svc, store) = Make();
        store.UpdateResult = false;
        Assert.Equal(PasswordChangeResult.SaveFailed, svc.ChangePassword("Holen", "oldsecret", "newsecret", "newsecret"));
    }
}

public class PasswordRuleParityTests
{
    public static IEnumerable<object?[]> Samples()
    {
        yield return new object?[] { null };
        yield return new object?[] { "" };
        yield return new object?[] { "12345" };
        yield return new object?[] { "123456" };
        yield return new object?[] { new string('a', 64) };
        yield return new object?[] { new string('a', 65) };
        yield return new object?[] { "has\ttab" };
        yield return new object?[] { "has\nnewline" };
        yield return new object?[] { "nul\0char" };
        yield return new object?[] { "del\u007fchar" };
        yield return new object?[] { "sp ace ok" };
        yield return new object?[] { "unicode-\u00e9\u00e8" };
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void WebAndGameRulesAgree(string? password) =>
        Assert.Equal(PlayerAccounts.ValidatePassword(password!), PasswordRules.Validate(password));

    [Fact]
    public void LimitsAgree()
    {
        Assert.Equal(PlayerAccounts.MinPasswordLength, PasswordRules.MinLength);
        Assert.Equal(PlayerAccounts.MaxPasswordLength, PasswordRules.MaxLength);
    }
}

public class PasswordPageTests
{
    private static PasswordModel Make(FakeAccountStore store, LoginThrottle throttle, string ip = "9.9.9.9")
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "Holen") }, "test"));
        return new PasswordModel(new AccountService(store), throttle)
        {
            PageContext = new PageContext { HttpContext = ctx },
        };
    }

    private static FakeAccountStore Store() => new FakeAccountStore().Add("Holen", "oldsecret");

    [Fact]
    public void Page_RequiresSignIn() =>
        Assert.NotNull(Attribute.GetCustomAttribute(typeof(PasswordModel), typeof(AuthorizeAttribute)));

    [Fact]
    public async Task Success_ShowsConfirmation_AndUpdatesStore()
    {
        var store = Store();
        var page = Make(store, new LoginThrottle());
        page.Current = "oldsecret"; page.New = "newsecret"; page.Confirm = "newsecret";
        await page.OnPostAsync();
        Assert.True(page.Succeeded);
        Assert.Null(page.Error);
        Assert.Equal("newsecret", store.FindByNameKey("holen")!.Password);
    }

    [Fact]
    public async Task WrongCurrent_ShowsGenericError_AndCountsTowardThrottle()
    {
        var store = Store();
        var throttle = new LoginThrottle();
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            var page = Make(store, throttle);
            page.Current = "nope"; page.New = "newsecret"; page.Confirm = "newsecret";
            await page.OnPostAsync();
            Assert.Equal(PasswordModel.WrongCurrentError, page.Error);
            Assert.False(page.Succeeded);
        }
        Assert.True(throttle.IsBlocked("9.9.9.9"));
        Assert.Empty(store.Updates);

        // Now locked out: even the right current password gets the same generic error and changes nothing.
        var blocked = Make(store, throttle);
        blocked.Current = "oldsecret"; blocked.New = "newsecret"; blocked.Confirm = "newsecret";
        await blocked.OnPostAsync();
        Assert.Equal(PasswordModel.WrongCurrentError, blocked.Error);
        Assert.Empty(store.Updates);
    }

    [Fact]
    public async Task Mismatch_ShowsError_AndDoesNotThrottle()
    {
        var store = Store();
        var throttle = new LoginThrottle();
        var page = Make(store, throttle);
        page.Current = "oldsecret"; page.New = "newsecret"; page.Confirm = "different";
        await page.OnPostAsync();
        Assert.Equal(PasswordModel.MismatchError, page.Error);
        Assert.Empty(store.Updates);
        Assert.False(throttle.IsBlocked("9.9.9.9"));
    }

    [Fact]
    public async Task RuleViolation_ShowsRuleMessage()
    {
        var page = Make(Store(), new LoginThrottle());
        page.Current = "oldsecret"; page.New = "abc"; page.Confirm = "abc";
        await page.OnPostAsync();
        Assert.Equal(PasswordRules.Validate("abc"), page.Error);
    }

    [Fact]
    public async Task SaveFailure_IsShown()
    {
        var store = Store();
        store.UpdateResult = false;
        var page = Make(store, new LoginThrottle());
        page.Current = "oldsecret"; page.New = "newsecret"; page.Confirm = "newsecret";
        await page.OnPostAsync();
        Assert.Equal(PasswordModel.SaveFailedError, page.Error);
        Assert.False(page.Succeeded);
    }

    [Fact]
    public async Task Errors_NeverContainSubmittedPasswords()
    {
        var page = Make(Store(), new LoginThrottle());
        page.Current = "oldsecret"; page.New = "nope"; page.Confirm = "nope2";
        await page.OnPostAsync();
        Assert.DoesNotContain("oldsecret", page.Error ?? "");
        Assert.DoesNotContain("nope", page.Error ?? "");
    }
}
