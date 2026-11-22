using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using MUD.Contracts;
using MUD.Web.Pages;
using MUD.Web.Services;
using Xunit;

namespace MUD.Web.Tests;

// The modules page over a fake of core's Admin service. Which module can be started, stopped or restarted, and by whom, is core's rule
// (MUD.Tests/AdminApiTests.cs); the page only sends the request with the cookie's token and shows the answer.
public class ModulesPageTests
{
    private class MemoryTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static ModulesModel Page(FakeAdminClient core, string? role = "Admin")
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            role == null ? Array.Empty<Claim>() : new[] { new Claim(ClaimTypes.Name, "Someone"), new Claim(ClaimTypes.Role, role) },
            role == null ? null : "test"));
        var http = new DefaultHttpContext { User = user };
        http.Request.Headers.Cookie = SessionAuthentication.CookieName + "=token-1";
        var model = new ModulesModel(core) { PageContext = new PageContext { HttpContext = http } };
        model.TempData = new TempDataDictionary(http, new MemoryTempData());
        return model;
    }

    private static FakeAdminClient Core() =>
        new FakeAdminClient()
            .AddModule("alpha", ModuleRunState.Ready, required: true, version: "1.2")
            .AddModule("beta", ModuleRunState.Failed, lastFailure: "exited with code 3")
            .AddModule("gamma", ModuleRunState.Stopped);

    private static string Notice(ModulesModel m) => (string)m.TempData["Notice"]!;

    // ---- list ----

    [Fact]
    public async Task List_ShowsEveryModuleWithItsFacts()
    {
        var core = Core();
        var page = Page(core);
        Assert.IsType<PageResult>(await page.OnGetAsync());
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, page.Items.Select(i => i.Name));
        Assert.Equal(new[] { "token-1" }, core.ModuleTokens);
        Assert.Null(page.Error);
    }

    [Fact]
    public void RunningText_IsWords_NotColour()
    {
        var core = Core();
        Assert.Equal("Running", ModulesModel.RunningText(core.Modules["alpha"]));
        Assert.Equal("Not running", ModulesModel.RunningText(core.Modules["beta"]));
        Assert.Equal("Not running", ModulesModel.RunningText(core.Modules["gamma"]));
        Assert.Equal("Ready", ModulesModel.StateText(core.Modules["alpha"]));
        Assert.Equal("Failed", ModulesModel.StateText(core.Modules["beta"]));
    }

    [Fact]
    public async Task Get_ShowsCarriedNotice_AndNoneWhenAbsent()
    {
        var page = Page(Core());
        page.TempData["Notice"] = "Start requested.";
        await page.OnGetAsync();
        Assert.Equal("Start requested.", page.Notice);

        var bare = Page(Core());
        await bare.OnGetAsync();
        Assert.Null(bare.Notice);
    }

    [Theory]
    [InlineData("Creator", false)]
    [InlineData("Admin", true)]
    public void IsAdmin_OnlyForAdmins(string role, bool expected) => Assert.Equal(expected, Page(Core(), role).IsAdmin);

    // ---- unavailable ----

    [Fact]
    public async Task Get_CoreUnavailable_Shows503AndMessage()
    {
        var page = Page(new FakeAdminClient { Unavailable = true });
        await page.OnGetAsync();
        Assert.Equal(ModulesModel.UnavailableError, page.Error);
        Assert.Equal(503, page.Response.StatusCode);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task Get_CoreRefuses_Shows403()
    {
        var page = Page(new FakeAdminClient { RefuseModuleCalls = true });
        await page.OnGetAsync();
        Assert.Equal(ModulesModel.ForbiddenError, page.Error);
        Assert.Equal(403, page.Response.StatusCode);
    }

    // ---- handlers ----

    public static IEnumerable<object[]> Actions() => new[]
    {
        new object[] { "start" }, new object[] { "stop" }, new object[] { "restart" },
    };

    private static Task<IActionResult> Run(ModulesModel page, string action, string? name) => action switch
    {
        "start" => page.OnPostStartAsync(name),
        "stop" => page.OnPostStopAsync(name),
        _ => page.OnPostRestartAsync(name),
    };

    private static List<string> Calls(FakeAdminClient core, string action) => action switch
    {
        "start" => core.ModuleStarts,
        "stop" => core.ModuleStops,
        _ => core.ModuleRestarts,
    };

    [Theory, MemberData(nameof(Actions))]
    public async Task Handler_Ok_SendsNameAndToken_AndRedirectsWithNotice(string action)
    {
        var core = Core();
        core.ModuleActionResult = AdminResult.Ok;
        var page = Page(core);
        var result = await Run(page, action, "alpha");
        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(new[] { "alpha" }, Calls(core, action));
        Assert.Equal(new[] { "token-1" }, core.ModuleTokens);
        Assert.Contains("requested", Notice(page));
    }

    [Fact]
    public async Task Restart_NoticeSaysNewInstanceStartsBeforeOldStops()
    {
        var page = Page(Core());
        await page.OnPostRestartAsync("alpha");
        Assert.Contains("starts a new instance before it stops the old one", Notice(page));
    }

    [Theory, MemberData(nameof(Actions))]
    public async Task Handler_OkWithCoreMessage_ShowsCoreMessage(string action)
    {
        var core = Core();
        core.ModuleActionMessage = "Queued by core.";
        var page = Page(core);
        await Run(page, action, "alpha");
        Assert.Equal("Queued by core.", Notice(page));
    }

    [Theory, MemberData(nameof(Actions))]
    public async Task Handler_NotFound_ShowsNotice(string action)
    {
        var core = Core();
        core.ModuleActionResult = AdminResult.NotFound;
        var page = Page(core);
        Assert.IsType<RedirectToPageResult>(await Run(page, action, "nope"));
        Assert.Equal("That module does not exist.", Notice(page));
    }

    [Theory, MemberData(nameof(Actions))]
    public async Task Handler_Refused_ShowsCoreRefusalMessage(string action)
    {
        var core = Core();
        core.ModuleActionResult = AdminResult.Refused;
        core.ModuleActionMessage = "The supervisor is not running.";
        var page = Page(core);
        await Run(page, action, "alpha");
        Assert.Equal("The supervisor is not running.", Notice(page));
    }

    [Theory, MemberData(nameof(Actions))]
    public async Task Handler_NoChange_ShowsNotice(string action)
    {
        var core = Core();
        core.ModuleActionResult = AdminResult.NoChange;
        var page = Page(core);
        await Run(page, action, "alpha");
        Assert.Equal("Nothing was changed.", Notice(page));
    }

    [Theory, MemberData(nameof(Actions))]
    public async Task Handler_CoreUnavailable_Shows503(string action)
    {
        var core = Core();
        core.Unavailable = true;
        var page = Page(core);
        Assert.IsType<PageResult>(await Run(page, action, "alpha"));
        Assert.Equal(ModulesModel.UnavailableError, page.Error);
        Assert.Equal(503, page.Response.StatusCode);
    }

    [Theory, MemberData(nameof(Actions))]
    public async Task Handler_CoreRefusesCaller_Shows403(string action)
    {
        // A creator can forge the POST; core refuses it and the page says so.
        var core = Core();
        core.RefuseModuleCalls = true;
        var page = Page(core, "Creator");
        Assert.IsType<PageResult>(await Run(page, action, "alpha"));
        Assert.Equal(ModulesModel.ForbiddenError, page.Error);
        Assert.Equal(403, page.Response.StatusCode);
    }

    [Theory, MemberData(nameof(Actions))]
    public async Task Handler_MissingName_IsSentAsEmpty(string action)
    {
        var core = Core();
        await Run(Page(core), action, null);
        Assert.Equal(new[] { "" }, Calls(core, action));
    }

    // ---- authorization and markup ----

    [Fact]
    public void Page_RequiresCreatorOrAdmin()
    {
        var attr = (AuthorizeAttribute?)Attribute.GetCustomAttribute(typeof(ModulesModel), typeof(AuthorizeAttribute));
        Assert.NotNull(attr);
        Assert.Equal(WebPolicies.CreatorOrAdmin, attr!.Policy);
    }

    [Fact]
    public void Route_IsCreatorModules()
    {
        Assert.StartsWith("@page \"/creator/modules\"", File.ReadAllText(Find("MUD.Web/Pages/Modules.cshtml")));
    }

    [Fact]
    public void Markup_ThreeAntiforgeryPostForms_StartOrStop_AndRestart_AdminOnly()
    {
        var cshtml = File.ReadAllText(Find("MUD.Web/Pages/Modules.cshtml"));
        // Start, Stop and Restart forms exist once each in the template; the Start/Stop pair is exclusive per row.
        Assert.Equal(3, Regex.Matches(cshtml, "<form method=\"post\"").Count);
        Assert.Equal(3, Regex.Matches(cshtml, "Html.AntiForgeryToken\\(\\)").Count);
        foreach (var handler in new[] { "Start", "Stop", "Restart" })
        {
            Assert.Single(Regex.Matches(cshtml, "asp-page-handler=\"" + handler + "\""));
        }
        // Every form sits inside the IsAdmin block.
        var adminBlock = cshtml.IndexOf("@if (Model.IsAdmin)\n            {", StringComparison.Ordinal);
        if (adminBlock < 0) { adminBlock = cshtml.IndexOf("@if (Model.IsAdmin)\r\n            {", StringComparison.Ordinal); }
        Assert.True(adminBlock >= 0);
        Assert.True(cshtml.IndexOf("<form", StringComparison.Ordinal) > adminBlock);
        // The running indicator is words, and the required flag stays visible to everyone.
        Assert.Contains("RunningText(m)", cshtml);
        Assert.Contains("m.Required", cshtml);
        Assert.DoesNotContain("Html.Raw", cshtml);
        Assert.DoesNotContain("<script", cshtml);
        Assert.DoesNotContain("http://", cshtml);
        Assert.DoesNotContain("https://", cshtml);
    }

    [Fact]
    public void Layout_LinksToModules_InsideTheCreatorOrAdminBlock()
    {
        var layout = File.ReadAllText(Find("MUD.Web/Pages/Shared/_Layout.cshtml"));
        var link = layout.IndexOf("href=\"/creator/modules\"", StringComparison.Ordinal);
        Assert.True(link > layout.IndexOf("WebPolicies.CreatorOrAdmin", StringComparison.Ordinal));
        Assert.True(link < layout.IndexOf("action=\"/logout\"", StringComparison.Ordinal));
    }

    private static string Find(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MUD.sln"))) { dir = dir.Parent; }
        Assert.NotNull(dir);
        return Path.GetFullPath(Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
    }
}
