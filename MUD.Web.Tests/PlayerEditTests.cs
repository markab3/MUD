using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using MUD.Contracts;
using MUD.Web.Pages;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

public class PlayerRulesTests
{
    [Theory]
    [InlineData(AccountRole.Player, AccountRole.Player, false)]
    [InlineData(AccountRole.Player, AccountRole.Creator, false)]
    [InlineData(AccountRole.Creator, AccountRole.Player, true)]
    [InlineData(AccountRole.Creator, AccountRole.Creator, false)]
    [InlineData(AccountRole.Creator, AccountRole.Admin, false)]
    [InlineData(AccountRole.Admin, AccountRole.Player, true)]
    [InlineData(AccountRole.Admin, AccountRole.Creator, true)]
    [InlineData(AccountRole.Admin, AccountRole.Admin, false)]
    public void CanEdit_RequiresStrictlyHigherRole(AccountRole editor, AccountRole target, bool expected) =>
        Assert.Equal(expected, PlayerRules.CanEdit(editor, target));

    [Theory]
    [InlineData(AccountRole.Creator, AccountRole.Player, AccountRole.Creator, false)]   // only Admin grants
    [InlineData(AccountRole.Admin, AccountRole.Player, AccountRole.Creator, true)]
    [InlineData(AccountRole.Admin, AccountRole.Creator, AccountRole.Player, true)]
    [InlineData(AccountRole.Admin, AccountRole.Player, AccountRole.Admin, true)]         // not above own role
    [InlineData(AccountRole.Admin, AccountRole.Admin, AccountRole.Player, false)]        // nobody edits an Admin
    public void CanSetRole(AccountRole editor, AccountRole target, AccountRole to, bool expected) =>
        Assert.Equal(expected, PlayerRules.CanSetRole(editor, target, to));

    [Fact]
    public void CannotGrantARoleAboveYourOwn()
    {
        Assert.False(PlayerRules.CanSetRole(AccountRole.Creator, AccountRole.Player, AccountRole.Admin));
    }

    [Theory]
    [InlineData(AccountRole.Creator, AccountRole.Player, false)]
    [InlineData(AccountRole.Admin, AccountRole.Player, true)]
    [InlineData(AccountRole.Admin, AccountRole.Creator, true)]
    [InlineData(AccountRole.Admin, AccountRole.Admin, false)]
    public void OnlyAnAdminRetires_AndNeverAnAdmin(AccountRole editor, AccountRole target, bool expected) =>
        Assert.Equal(expected, PlayerRules.CanSetRetired(editor, target));
}
