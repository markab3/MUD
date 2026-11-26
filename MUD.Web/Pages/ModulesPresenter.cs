using MUD.Contracts;
using MUD.Web.Services;

namespace MUD.Web.Pages;

public enum ModulesOutcome { Ok, Failed }
public enum ModuleAction { Start, Stop, Restart }

// The modules page logic (moved from ModulesModel): lists core's modules with their state; admins also start, stop and restart any
// module (required ones included). Core enforces the role on every call, so a forged event from a creator is refused there.
// The calls only queue the request on core's supervisor, so the page shows core's message and the state may take a moment to change.
public sealed class ModulesPresenter
{
    private readonly IAdminClient _core;
    private readonly string _token;

    public ModulesPresenter(IAdminClient core, string token)
    {
        _core = core;
        _token = token;
    }

    public IReadOnlyList<ModuleInfo> Items { get; private set; } = Array.Empty<ModuleInfo>();
    public string? Error { get; private set; }
    public string? Notice { get; private set; }

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

    public static string VersionText(ModuleInfo m) => string.IsNullOrEmpty(m.Version) ? "unknown" : m.Version;

    public static string NameText(ModuleInfo m) => m.Name + (m.FolderMissing ? " (folder missing)" : string.Empty);

    public async Task<ModulesOutcome> LoadAsync(CancellationToken cancellation = default)
    {
        Error = null;
        try
        {
            Items = await _core.ListModulesAsync(_token, cancellation);
            return ModulesOutcome.Ok;
        }
        catch (CoreUnavailableException) { Error = Messages.Unavailable; }
        catch (CoreRefusedException) { Error = Messages.ModuleForbidden; }
        catch (Exception) { Error = Messages.ModuleListFailed; }
        Items = Array.Empty<ModuleInfo>();
        return ModulesOutcome.Failed;
    }

    // Sends one request and sets Notice (core's message or a fixed sentence) or Error. The list is reloaded by the caller on Ok.
    public async Task<ModulesOutcome> ActAsync(ModuleAction action, string? name, CancellationToken cancellation = default)
    {
        Error = null;
        Notice = null;
        try
        {
            var n = name ?? string.Empty;
            var (call, okText) = action switch
            {
                ModuleAction.Start => (_core.StartModuleAsync(_token, n, cancellation), "Start requested."),
                ModuleAction.Stop => (_core.StopModuleAsync(_token, n, cancellation), "Stop requested."),
                _ => (_core.RestartModuleAsync(_token, n, cancellation), "Restart requested. " + Messages.ModuleRestartHint),
            };
            var reply = await call;
            var message = string.IsNullOrWhiteSpace(reply.Message) ? null : reply.Message;
            switch (reply.Result)
            {
                case AdminResult.Ok: Notice = message ?? okText; break;
                case AdminResult.NoChange: Notice = message ?? Messages.ModuleNothingChanged; break;
                case AdminResult.NotFound: Notice = message ?? Messages.ModuleNotFound; break;
                case AdminResult.Refused: Notice = message ?? Messages.ModuleCoreRefused; break;
                case AdminResult.Forbidden: Notice = Messages.ModuleForbidden; break;
                default: Notice = message ?? Messages.ModuleFailed; break;
            }
            return ModulesOutcome.Ok;
        }
        catch (CoreUnavailableException) { Error = Messages.Unavailable; }
        catch (CoreRefusedException) { Error = Messages.ModuleForbidden; }
        catch (Exception) { Error = Messages.ModuleFailed; }
        return ModulesOutcome.Failed;
    }
}
