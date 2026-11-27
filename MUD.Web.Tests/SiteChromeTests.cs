using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using MUD.Contracts;
using MUD.Web.Pages;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

// The top bar, the pull-out menu, the player page and the palette (WEB_BLAZOR.md 5.2, 5.5). The components are Razor and there is no
// bUnit here, so the rules live in NavMenu / CharacterPresenter (tested directly) and the markup and CSS are pinned by source checks;
// the rendered result was checked in a headless browser (see the PR).
public class NavMenuTests
{
    private static string[][] Labels(bool creatorOrAdmin, bool admin) =>
        NavMenu.SectionsFor(creatorOrAdmin, admin).Select(s => s.Items.Select(i => i.Label).ToArray()).ToArray();

    private static string[] Headings(bool creatorOrAdmin, bool admin) =>
        NavMenu.SectionsFor(creatorOrAdmin, admin).Select(s => s.Heading).ToArray();

    [Fact]
    public void APlayer_GetsOnlyThePlayerSection()
    {
        Assert.Equal(new[] { "Player" }, Headings(false, false));
        Assert.Equal(new[] { new[] { "Character", "Change password" } }, Labels(false, false));
    }

    [Fact]
    public void ACreator_GetsPlayerThenCreator_AndNoAdminSection()
    {
        Assert.Equal(new[] { "Player", "Creator" }, Headings(true, false));
        Assert.Equal(new[] { new[] { "Character", "Change password" }, new[] { "Map", "Modules", "Terrain", "Areas", "Create room" } }, Labels(true, false));
        Assert.DoesNotContain(NavMenu.SectionsFor(true, false).SelectMany(s => s.Items), i => i.Label == "Players");
    }

    [Fact]
    public void AnAdmin_GetsPlayerCreatorAndAdminSections_InThatOrder()
    {
        Assert.Equal(new[] { "Player", "Creator", "Admin" }, Headings(true, true));
        Assert.Equal(new[] { new[] { "Character", "Change password" }, new[] { "Map", "Modules", "Terrain", "Areas", "Create room" }, new[] { "Players" } }, Labels(true, true));
    }

    [Fact]
    public void TheMenuItems_KeepTheirRoutes_AndOnlyCreateRoomMatchesExactly()
    {
        var items = NavMenu.SectionsFor(true, true).SelectMany(s => s.Items).ToDictionary(i => i.Label);
        Assert.Equal(WebRoutes.Creator, items["Create room"].Href);
        Assert.Equal("/creator", items["Create room"].Href);
        Assert.Equal(WebRoutes.Players, items["Players"].Href);
        Assert.Equal(WebRoutes.Map, items["Map"].Href);
        Assert.Equal(WebRoutes.Modules, items["Modules"].Href);
        Assert.Equal(WebRoutes.Terrain, items["Terrain"].Href);
        Assert.Equal(WebRoutes.Character, items["Character"].Href);
        Assert.Equal(WebRoutes.Password, items["Change password"].Href);
        Assert.Equal(new[] { "Create room" }, items.Values.Where(i => i.ExactMatch).Select(i => i.Label).ToArray());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NoSectionIsEmpty_AndSectionIdsAreUnique(bool creatorOrAdmin, bool admin)
    {
        var sections = NavMenu.SectionsFor(creatorOrAdmin, admin);
        Assert.All(sections, s => Assert.NotEmpty(s.Items));
        Assert.Equal(sections.Count, sections.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void TheMenu_OpensAndClosesAndReportsChanges()
    {
        var menu = new NavMenu();
        Assert.False(menu.IsOpen);
        Assert.False(menu.Close());
        Assert.True(menu.Open());
        Assert.True(menu.IsOpen);
        Assert.False(menu.Open());
        Assert.True(menu.Close());
        Assert.False(menu.IsOpen);
        menu.Toggle();
        Assert.True(menu.IsOpen);
        menu.Toggle();
        Assert.False(menu.IsOpen);
    }
}

public class SiteChromeMarkupTests
{
    private static string Read(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MUD.sln"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string Nav() => Read("MUD.Web/Components/Layout/NavShell.razor");

    [Fact]
    public void TheBar_HasMenuButtonTitleAndUserName_InThatOrder_AndAnonymousSeesTheLogoOnly()
    {
        var nav = Nav();
        var authorized = nav.IndexOf("<Authorized>", StringComparison.Ordinal);
        var notAuthorized = nav.IndexOf("<NotAuthorized>", StringComparison.Ordinal);
        var button = nav.IndexOf("mud-menu-button", StringComparison.Ordinal);
        var title = nav.IndexOf("mud-title", StringComparison.Ordinal);
        var user = nav.IndexOf("mud-username", StringComparison.Ordinal);
        Assert.True(authorized < button && button < title && title < user && user < notAuthorized);
        var anonymous = nav.Substring(notAuthorized, nav.IndexOf("</NotAuthorized>", StringComparison.Ordinal) - notAuthorized);
        Assert.Contains("alt=\"Planar Realms\"", anonymous);
        Assert.Contains("img/planar-realms-logo.png", anonymous);
        Assert.DoesNotContain("mud-menu-button", anonymous);
        Assert.DoesNotContain("mud-username", anonymous);
    }

    [Fact]
    public void TheTitle_IsALogoImageLinkingHome_WithAltTextAndSize_InBothBars()
    {
        var nav = Nav();
        Assert.Equal(2, Regex.Matches(nav, @"<a class=""mud-title"" href=""/""><img src=""img/planar-realms-logo.png"" width=""116"" height=""40"" alt=""Planar Realms"" /></a>").Count);
        Assert.DoesNotContain("<span class=\"mud-title\"", nav);
        var png = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MUD.Web", "wwwroot", "img", "planar-realms-logo.png");
        Assert.True(new FileInfo(png).Length < 60 * 1024);
        var css = Read("MUD.Web/wwwroot/css/mobile.css");
        Assert.Matches(@"\.mud-title \{[^}]*min-height: var\(--mud-hit\)", css);
    }

    [Fact]
    public void TheUserName_LinksToThePlayerPage_AndTruncatesWithAnEllipsis()
    {
        Assert.Contains("<a class=\"mud-username\" href=\"@WebRoutes.Character\">@context.User.FindFirstValue(ClaimTypes.Name)</a>", Nav());
        Assert.Equal("/character", WebRoutes.Character);
        var css = Read("MUD.Web/wwwroot/css/mobile.css");
        var rule = Regex.Match(css, @"\.mud-username \{[^}]*\}").Value;
        Assert.Contains("text-overflow: ellipsis", rule);
        Assert.Contains("white-space: nowrap", rule);
        Assert.Contains("overflow: hidden", rule);
        Assert.Contains("min-width: 0", rule);
    }

    [Fact]
    public void TheMenuButton_IsADialogTrigger_44pxHigh_WithAriaState()
    {
        var nav = Nav();
        Assert.Contains("aria-expanded=\"@(_menu.IsOpen ? \"true\" : \"false\")\"", nav);
        Assert.Contains("aria-controls=\"mud-menu\"", nav);
        Assert.Contains("id=\"mud-menu\"", nav);
        Assert.Contains("role=\"dialog\"", nav);
        Assert.Contains("aria-modal=\"true\"", nav);
        Assert.Matches(@"\.mud-menu-button \{[^}]*width: var\(--mud-hit\)[^}]*height: var\(--mud-hit\)", Read("MUD.Web/wwwroot/css/mobile.css"));
        Assert.Contains("--mud-hit: 44px", Read("MUD.Web/wwwroot/css/mobile.css"));
    }

    [Fact]
    public void TheMenu_ClosesOnBackdropEscapeLinkAndNavigation_AndFocusMovesInAndBack()
    {
        var nav = Nav();
        Assert.Contains("class=\"mud-backdrop\" @onclick=\"CloseAndReturnFocus\"", nav);
        Assert.Contains("e.Key == \"Escape\"", nav);
        Assert.Contains("@onclick=\"CloseForNavigation\"", nav);
        Assert.Contains("Navigation.LocationChanged += OnLocationChanged", nav);
        Assert.Contains("Navigation.LocationChanged -= OnLocationChanged", nav);
        Assert.Contains("mudMenu.opened", nav);
        Assert.Contains("mudMenu.closed", nav);
        var js = Read("MUD.Web/wwwroot/js/app.js");
        Assert.Contains("window.mudMenu", js);
        Assert.Contains("e.key !== 'Tab'", js);
        Assert.Contains("last.focus()", js);
        Assert.Contains("first.focus()", js);
    }

    [Fact]
    public void TheMenu_SlidesInFromTheLeft_AndIsHiddenWhenClosed()
    {
        var css = Read("MUD.Web/wwwroot/css/mobile.css");
        Assert.Matches(@"\.mud-drawer \{[^}]*left: 0[^}]*transform: translateX\(-100%\)[^}]*transition: transform", css);
        Assert.Contains(".mud-open .mud-drawer { transform: translateX(0); }", css);
        Assert.Matches(@"\.mud-drawer-wrap \{[^}]*visibility: hidden", css);
    }

    [Fact]
    public void LogOut_StaysAPostFormWithTheAntiforgeryToken()
    {
        var nav = Nav();
        Assert.Contains("<form method=\"post\" action=\"@WebRoutes.Logout\">", nav);
        Assert.Contains("name=\"@Tokens.FormFieldName\" value=\"@Tokens.RequestToken\"", nav);
        Assert.DoesNotContain("<a href=\"@WebRoutes.Logout\"", nav);
    }

    [Fact]
    public void TheNav_ListsItsLinksFromNavMenu_UnderTheCreatorPolicy()
    {
        var nav = Nav();
        Assert.Contains("NavMenu.SectionsFor(_creator, _admin)", nav);
        Assert.Contains("WebPolicies.CreatorOrAdmin", nav);
        Assert.Contains("<h2 id=\"mud-nav-@sec.Id\"", nav);
        Assert.Contains("<ul aria-labelledby=\"mud-nav-@sec.Id\">", nav);
        // Log out stays outside the sections, after them.
        Assert.True(nav.IndexOf("mud-nav-logout", StringComparison.Ordinal) > nav.IndexOf("aria-labelledby", StringComparison.Ordinal));
        foreach (var gone in new[] { "mud-nav-more", "mud-nav-extra", ">More<", "LegacyPlayers", "LegacyModules", "LegacyTerrain" }) { Assert.DoesNotContain(gone, nav); }
    }

    [Fact]
    public void TheLayout_HasNoBottomBarOrLeftRail()
    {
        var css = Read("MUD.Web/wwwroot/css/mobile.css");
        Assert.DoesNotContain(".mud-shell", css);
        Assert.DoesNotContain("mud-nav-w", css);
        Assert.DoesNotContain("inset-bottom)); max-width", css);
        var layout = Read("MUD.Web/Components/Layout/MainLayout.razor");
        Assert.DoesNotContain("FluentHeader", layout);
        Assert.Contains("<NavShell />", layout);
    }

    [Fact]
    public void Palette_IsDefinedOnce_AndFluentTokensAreMappedToIt()
    {
        var css = Read("MUD.Web/wwwroot/css/mobile.css");
        foreach (var (name, hex) in new[] { ("--mud-red", "#931621"), ("--mud-teal-dark", "#28464B"), ("--mud-teal", "#326771"), ("--mud-cyan", "#2C8C99"), ("--mud-turquoise", "#42D9C8") })
        {
            Assert.Single(Regex.Matches(css, Regex.Escape(hex), RegexOptions.IgnoreCase));
            Assert.Contains(name + ": " + hex, css);
        }
        Assert.Contains("--accent-base-color: var(--mud-cyan-ink)", css);
        Assert.Contains("--focus-stroke-inner: var(--mud-turquoise)", css);
    }

    [Fact]
    public void EveryColourOutsideTheVariableBlock_UsesAVariable()
    {
        var css = Read("MUD.Web/wwwroot/css/mobile.css");
        var afterRoot = css.Substring(css.IndexOf("html, body", StringComparison.Ordinal));
        Assert.DoesNotMatch(@"#[0-9a-fA-F]{3,8}\b", Regex.Replace(afterRoot, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline));
    }

    // WCAG 2.x contrast of the pairs the stylesheet actually uses (the PR lists the ratios).
    [Theory]
    [InlineData("#FFFFFF", "#28464B", 4.5)]   // bar and drawer text
    [InlineData("#FFFFFF", "#326771", 4.5)]   // drawer hover
    [InlineData("#42D9C8", "#28464B", 4.5)]   // user-name hover, focus ring on the bar
    [InlineData("#216E79", "#FFFFFF", 4.5)]   // links, primary button fill with white text
    [InlineData("#216E79", "#F4F7F8", 4.5)]   // links on the page
    [InlineData("#1B5C66", "#F4F7F8", 4.5)]   // link hover
    [InlineData("#FFFFFF", "#931621", 4.5)]   // danger button
    [InlineData("#931621", "#F4F7F8", 4.5)]   // error text
    [InlineData("#1B2B2E", "#F4F7F8", 4.5)]   // body text
    [InlineData("#4A5A5E", "#F4F7F8", 4.5)]   // hints
    [InlineData("#28464B", "#42D9C8", 4.5)]   // dark on turquoise highlight
    [InlineData("#6F878C", "#F4F7F8", 3.0)]   // control border (UI component)
    [InlineData("#6F878C", "#FFFFFF", 3.0)]   // control border on a white card
    [InlineData("#28464B", "#FFFFFF", 3.0)]   // focus ring on light surfaces
    public void ThePalettePairs_MeetTheirContrastMinimum(string fg, string bg, double minimum) =>
        Assert.True(Contrast.Ratio(fg, bg) >= minimum, fg + " on " + bg + " = " + Contrast.Ratio(fg, bg));

    [Fact]
    public void TheRawPaletteColoursThatFail_AreNotUsedForTextOrFills()
    {
        // Raw cyan fails as text/fill with white (3.95:1); the site uses the darker cyan-ink instead.
        Assert.True(Contrast.Ratio("#2C8C99", "#FFFFFF") < 4.5);
        var css = Read("MUD.Web/wwwroot/css/mobile.css");
        Assert.DoesNotContain("var(--mud-cyan)", css.Substring(css.IndexOf("html, body", StringComparison.Ordinal)));
    }
}

public static class Contrast
{
    private static double Lum(string hex)
    {
        double C(int i) { var v = Convert.ToInt32(hex.Substring(1 + i * 2, 2), 16) / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * C(0) + 0.7152 * C(1) + 0.0722 * C(2);
    }

    public static double Ratio(string a, string b)
    {
        var (hi, lo) = (Math.Max(Lum(a), Lum(b)), Math.Min(Lum(a), Lum(b)));
        return (hi + 0.05) / (lo + 0.05);
    }
}

public class CharacterPresenterTests
{
    private static OwnPlayerReply Own(string name, AdminRole role = AdminRole.Player) =>
        new() { Name = name, Role = role, Race = "human", Gender = "female", Class = "fighter", Location = "t.hall", ShortDescription = "a tall one", LongDescription = "tall" };

    [Fact]
    public async Task Load_AsksCoreForTheCallersOwnRecord_WithTheirToken_AndNothingElse()
    {
        var core = new FakeAdminClient();
        core.OwnPlayers["tok-pat"] = Own("Pat");
        core.OwnPlayers["tok-quinn"] = Own("Quinn");
        var presenter = new CharacterPresenter(core, "tok-pat");
        await presenter.LoadAsync();
        Assert.Equal("Pat", presenter.Own!.Name);
        Assert.Null(presenter.Error);
        Assert.Equal(new[] { "tok-pat" }, core.OwnPlayerTokens);
        Assert.Empty(core.PlayerTokens); // never the creator-only GetPlayer
        Assert.Equal("Player", CharacterPresenter.RoleText(presenter.Own.Role));
    }

    [Fact]
    public async Task ACreatorAlsoSeesOnlyTheirOwnRecord()
    {
        var core = new FakeAdminClient().AddPlayer("Pat");
        core.OwnPlayers["tok-cris"] = Own("Cris", AdminRole.Creator);
        var presenter = new CharacterPresenter(core, "tok-cris");
        await presenter.LoadAsync();
        Assert.Equal("Cris", presenter.Own!.Name);
        Assert.Equal("Creator", CharacterPresenter.RoleText(presenter.Own.Role));
    }

    [Fact]
    public async Task WhenCoreIsDown_ThePageShowsTheExistingCoreDownMessage()
    {
        var core = new FakeAdminClient { Unavailable = true };
        var presenter = new CharacterPresenter(core, "tok");
        await presenter.LoadAsync();
        Assert.Null(presenter.Own);
        Assert.Equal(Messages.Unavailable, presenter.Error);
    }

    [Fact]
    public async Task WhenCoreRefusesTheToken_NothingIsShown_AndAFixedSentenceIs()
    {
        var core = new FakeAdminClient { RefuseOwnPlayer = true };
        var presenter = new CharacterPresenter(core, "tok");
        await presenter.LoadAsync();
        Assert.Null(presenter.Own);
        Assert.Equal(Messages.CharacterRefused, presenter.Error);
    }

    [Fact]
    public void EmptyValues_ShowADash()
    {
        Assert.Equal("-", CharacterPresenter.OrDash(""));
        Assert.Equal("-", CharacterPresenter.OrDash(null));
        Assert.Equal("elf", CharacterPresenter.OrDash("elf"));
    }

    [Fact]
    public void ThePage_IsAuthorizeOnly_UsesTheSelfViewCall_AndShowsNoCredentialFields()
    {
        var type = typeof(IAdminClient).Assembly.GetTypes().Single(t => t.FullName == "MUD.Web.Components.Pages.Character");
        Assert.Contains(type.GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>(), r => r.Template == "/character");
        Assert.NotEmpty(type.GetCustomAttributes(typeof(AuthorizeAttribute), false));
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MUD.sln"))) { dir = dir.Parent; }
        var razor = File.ReadAllText(Path.Combine(dir!.FullName, "MUD.Web", "Components", "Pages", "Character.razor"));
        Assert.Contains("FluentCard", razor);
        Assert.DoesNotContain("GetPlayerAsync", razor);
        Assert.DoesNotContain("assword", razor);
        var fields = OwnPlayerReply.Descriptor.Fields.InDeclarationOrder().Select(f => f.Name).ToList();
        Assert.DoesNotContain(fields, n => n.Contains("password") || n.Contains("token") || n.Contains("hash") || n.Contains("inventory"));
    }
}
