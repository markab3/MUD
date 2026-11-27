using MUD.Contracts;
using MUD.Web.Services;

namespace MUD.Web.Pages;

public enum AreaOutcome { Ok, Failed }

// The areas page logic (/creator/areas): list, create, edit (name and description only) and delete. Free of HttpContext and IActionResult
// (WEB_BLAZOR.md 4.1). Everything goes through IAdminClient; core enforces the role, validates every field and refuses a delete while
// rooms still use the area. The id (prefix) is typed only for a new area and can never change afterwards: renaming an area means moving
// its rooms, which this page does not do.
public sealed class AreaListPresenter
{
    public const string UnavailableError = "The game is not reachable right now. Please try again later.";
    public const string ForbiddenError = "You are not allowed to do that.";
    public const string FailedError = "The area request could not be completed. Please try again later.";
    public const string RefusedError = "The area list is not available on the game right now.";
    public const string AuditFailedError = "The change was not saved because it could not be recorded. Please try again later.";
    public const string GoneError = "That area does not exist.";
    public const string ExistsError = "An area with that prefix already exists.";
    public const string DeleteConfirm = "Delete this area? Rooms must be moved out of it first.";
    public const string Help = "An area is an organisational label for the back end; areas usually line up with regions of the game. "
        + "The prefix is the start of every room key in the area and cannot be changed once the area exists: renaming an area means moving its rooms, which is done on each room's page.";

    private readonly IAdminClient _core;
    private readonly string _token;

    public AreaListPresenter(IAdminClient core, string token)
    {
        _core = core;
        _token = token;
    }

    public IReadOnlyList<AreaRecord> Areas { get; private set; } = Array.Empty<AreaRecord>();
    public string? Error { get; private set; }
    public string? Notice { get; set; }

    // Core's field messages after an Invalid reply; the user's input stays in the form.
    public List<string> Problems { get; } = new();

    // The form. FormOpen false = closed. EditingId null with the form open = a new area (the prefix is typed); otherwise the area being edited.
    public bool FormOpen { get; private set; }
    public string? EditingId { get; private set; }
    public bool IsNew => FormOpen && EditingId == null;
    public string? FormId { get; set; }
    public string? FormName { get; set; }
    public string? FormDescription { get; set; }

    public async Task<AreaOutcome> LoadAsync(CancellationToken cancellation = default)
    {
        Error = null;
        try
        {
            Areas = (await _core.ListAreasAsync(_token, cancellation)).OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
            return AreaOutcome.Ok;
        }
        catch (CoreUnavailableException) { Error = UnavailableError; }
        catch (CoreRefusedException) { Error = ForbiddenError; }
        catch (Exception) { Error = FailedError; }
        Areas = Array.Empty<AreaRecord>();
        return AreaOutcome.Failed;
    }

    public void BeginNew()
    {
        FormOpen = true;
        EditingId = null;
        FormId = null;
        FormName = null;
        FormDescription = null;
        Problems.Clear();
    }

    public bool BeginEdit(string id)
    {
        var area = Areas.FirstOrDefault(a => a.Id == id);
        if (area == null) { return false; }
        FormOpen = true;
        EditingId = area.Id;
        FormId = area.Id;
        FormName = area.Name;
        FormDescription = area.Description;
        Problems.Clear();
        return true;
    }

    public void CancelForm()
    {
        FormOpen = false;
        EditingId = null;
        Problems.Clear();
    }

    // Saves the open form. On Ok the form closes; otherwise it stays open with the input and the problems. An edit always writes the
    // original id (EditingId), never FormId, so a form can never change which area is written.
    public async Task<AreaOutcome> SaveAsync(CancellationToken cancellation = default)
    {
        Error = null;
        Notice = null;
        Problems.Clear();
        if (!FormOpen) { return AreaOutcome.Failed; }
        var creating = EditingId == null;
        var record = new AreaRecord
        {
            Id = (creating ? FormId : EditingId) ?? string.Empty,
            Name = FormName ?? string.Empty,
            Description = FormDescription ?? string.Empty,
        };
        try
        {
            var reply = creating ? await _core.CreateAreaAsync(_token, record, cancellation) : await _core.UpdateAreaAsync(_token, record, cancellation);
            switch (reply.Result)
            {
                case AdminResult.Ok:
                    Notice = creating ? "Area created." : "Area saved.";
                    CancelForm();
                    return AreaOutcome.Ok;
                case AdminResult.NoChange:
                    Notice = "Nothing was changed.";
                    CancelForm();
                    return AreaOutcome.Ok;
                case AdminResult.Invalid:
                    foreach (var e in reply.Errors) { Problems.Add(string.IsNullOrEmpty(e.Field) ? e.Message : e.Field + ": " + e.Message); }
                    if (Problems.Count == 0) { Error = FailedError; }
                    break;
                case AdminResult.AlreadyExists: Error = ExistsError; break;
                case AdminResult.NotFound: Error = GoneError; break;
                case AdminResult.Refused: Error = RefusedError; break;
                case AdminResult.AuditFailed: Error = AuditFailedError; break;
                case AdminResult.Forbidden: Error = ForbiddenError; break;
                default: Error = FailedError; break;
            }
        }
        catch (CoreUnavailableException) { Error = UnavailableError; }
        catch (CoreRefusedException) { Error = ForbiddenError; }
        catch (Exception) { Error = FailedError; }
        return AreaOutcome.Failed;
    }

    // Deletes one area. Core refuses while rooms still use it; its message (it names the count) becomes the notice. The caller reloads afterwards.
    public async Task<AreaOutcome> DeleteAsync(string? id, CancellationToken cancellation = default)
    {
        Error = null;
        Notice = null;
        try
        {
            var reply = await _core.DeleteAreaAsync(_token, id ?? string.Empty, cancellation);
            var message = string.IsNullOrWhiteSpace(reply.Message) ? null : reply.Message;
            switch (reply.Result)
            {
                case AdminResult.Ok: Notice = "Area deleted."; return AreaOutcome.Ok;
                case AdminResult.NotFound: Notice = GoneError; return AreaOutcome.Ok;
                case AdminResult.Refused: Error = message ?? RefusedError; break;
                case AdminResult.AuditFailed: Error = AuditFailedError; break;
                case AdminResult.Forbidden: Error = ForbiddenError; break;
                default: Error = FailedError; break;
            }
        }
        catch (CoreUnavailableException) { Error = UnavailableError; }
        catch (CoreRefusedException) { Error = ForbiddenError; }
        catch (Exception) { Error = FailedError; }
        return AreaOutcome.Failed;
    }
}
