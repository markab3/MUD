using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MUD.Web.Pages;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

// Web side of retirement (specs/WEB_EDITING.md section 15): a retired character cannot sign in.
public class RetiredLoginTests
{
    private static FakeAccountStore Store() => new FakeAccountStore();

    private class RetiredStore : IAccountStore
    {
        public StoredAccount? FindByNameKey(string nameKey) => nameKey == "ann" ? new StoredAccount("Ann", "secret", AccountRole.Creator, "id-ann", Retired: true) : null;
        public bool UpdatePassword(string nameKey, string newPassword) => false;
    }

    [Fact]
    public void Authenticate_WithTheRightPassword_ReturnsTheAccountMarkedRetired()
    {
        var account = new AccountService(new RetiredStore()).Authenticate("Ann", "secret");
        Assert.NotNull(account);
        Assert.True(account!.Retired);
    }

    [Fact]
    public void Authenticate_WithAWrongPassword_ReturnsNull_SoRetirementIsNotRevealed()
    {
        Assert.Null(new AccountService(new RetiredStore()).Authenticate("Ann", "wrong"));
    }

    [Fact]
    public void GetByName_ReportsRetired_SoEditorsLoseTheirPowerAtOnce()
    {
        Assert.True(new AccountService(new RetiredStore()).GetByName("Ann")!.Retired);
        Assert.Null(RoomActors.Resolve(new AccountService(new RetiredStore()), "Ann"));
    }

    private static LoginModel Page(IAccountService accounts)
    {
        var page = new LoginModel(accounts, new LoginThrottle()) { Name = "Ann", Password = "secret" };
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        page.PageContext = new PageContext { HttpContext = http };
        return page;
    }

    [Fact]
    public async Task Login_OfARetiredCharacter_IsRefusedWithTheRetiredMessage_AndNoCookie()
    {
        var page = Page(new AccountService(new RetiredStore()));
        var result = await page.OnPostAsync();
        Assert.IsType<PageResult>(result);
        Assert.Equal("This character has been retired and can no longer be played.", page.Error);
        Assert.Equal(LoginModel.RetiredError, page.Error);
        Assert.False(page.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public void Account_DefaultsToNotRetired()
    {
        Assert.False(new Account("Bob", AccountRole.Player).Retired);
        Assert.False(new AccountService(Store().Add("Bob", "pw")).GetByName("Bob")!.Retired);
    }
}
