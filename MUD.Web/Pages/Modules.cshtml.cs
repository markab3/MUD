using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MUD.Contracts;
using MUD.Web.Services;

namespace MUD.Web.Pages;

// /creator/modules: lists core's modules with their state; admins also get Start, Stop and Restart buttons for every module (required
// ones included). Creators see the list only. Core enforces the role on every call, so a forged POST from a creator is refused there.
// The calls only queue the request on core's supervisor, so the page shows core's message and the state may take a moment to change;
// refresh by hand. All messages are fixed strings or core's own wording, and Razor encodes every value it renders.
[Authorize(Policy = WebPolicies.CreatorOrAdmin)]
public class ModulesModel : PageModel
{
    public const string UnavailableError = "The game is not reachable right now. Please try again later.";
    public const string ForbiddenError = "You are not allowed to do that.";
    public const string FailedError = "The module request could not be completed. Please try again later.";
    public const string ListFailedError = "The module list could not be loaded. Please try again later.";
    public const string RestartHint = "Restart starts a new instance before it stops the old one.";

    private readonly IAdminClient _core;

    public ModulesModel(IAdminClient core) { _core = core; }

    public IReadOnlyList<ModuleInfo> Items { get; private set; } = Array.Empty<ModuleInfo>();
    public string? Error { get; private set; }
    public string? Notice { get; private set; }

    // Decides only whether the buttons render; core re-checks the role on each call.
    public bool IsAdmin => User.IsInRole(nameof(AccountRole.Admin));

    private string Token => SessionAuthentication.TokenFrom(Request);

    public static string RunningText(ModuleInfo m) => m.Running ? "Running" : "Not running";

    public static string StateText(ModuleInfo m) => m.State switch
    {
        ModuleRunState.Stopped => "Stopped",
        ModuleRunState.Starting => "Starting",
        ModuleRunState.Connected => "Connected",
        ModuleRunState.Ready => "Ready",
        ModuleRunState.Backoff => "Backoff (waiting to retry)",
        ModuleRunState.Failed => "Failed",
        ModuleRunState.Refused => "Refused",
        ModuleRunState.Stopping => "Stopping",
        _ => "Unknown",
    };

    public async Task<IActionResult> OnGetAsync()
    {
        if (TempData?["Notice"] is string carried) { Notice = carried; }
        return await Load();
    }

    public Task<IActionResult> OnPostStartAsync(string? name) =>
        Act(name, (n, ct) => _core.StartModuleAsync(Token, n, ct), "Start requested.");

    public Task<IActionResult> OnPostStopAsync(string? name) =>
        Act(name, (n, ct) => _core.StopModuleAsync(Token, n, ct), "Stop requested.");

    public Task<IActionResult> OnPostRestartAsync(string? name) =>
        Act(name, (n, ct) => _core.RestartModuleAsync(Token, n, ct), "Restart requested. " + RestartHint);

    private async Task<IActionResult> Act(string? name, Func<string, CancellationToken, Task<ModuleActionReply>> call, string okText)
    {
        try
        {
            var reply = await call(name ?? string.Empty, HttpContext.RequestAborted);
            var message = string.IsNullOrWhiteSpace(reply.Message) ? null : reply.Message;
            string notice;
            switch (reply.Result)
            {
                case AdminResult.Ok: notice = message ?? okText; break;
                case AdminResult.NoChange: notice = message ?? "Nothing was changed."; break;
                case AdminResult.NotFound: notice = message ?? "That module does not exist."; break;
                case AdminResult.Refused: notice = message ?? "Core refused the request."; break;
                case AdminResult.Forbidden: notice = ForbiddenError; break;
                default: notice = message ?? FailedError; break;
            }
            TempData["Notice"] = notice;
            return RedirectToPage();
        }
        catch (CoreUnavailableException) { return await Fail(UnavailableError, StatusCodes.Status503ServiceUnavailable); }
        catch (CoreRefusedException) { return await Fail(ForbiddenError, StatusCodes.Status403Forbidden); }
        catch (Exception) { return await Fail(FailedError, StatusCodes.Status500InternalServerError); }
    }

    private Task<IActionResult> Fail(string error, int status)
    {
        Error = error;
        Response.StatusCode = status;
        return Task.FromResult<IActionResult>(Page());
    }

    private async Task<IActionResult> Load()
    {
        try
        {
            Items = await _core.ListModulesAsync(Token, HttpContext.RequestAborted);
            return Page();
        }
        catch (CoreUnavailableException) { return await Fail(UnavailableError, StatusCodes.Status503ServiceUnavailable); }
        catch (CoreRefusedException) { return await Fail(ForbiddenError, StatusCodes.Status403Forbidden); }
        catch (Exception) { return await Fail(ListFailedError, StatusCodes.Status500InternalServerError); }
    }
}
