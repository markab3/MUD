using System.ComponentModel.DataAnnotations;
using MUD.Web.Services;

namespace MUD.Web.Pages;

public enum CreatorOutcome { Created, Failed }

// The create-room logic of the Creator page (moved from CreatorModel), free of HttpContext, ModelState and IActionResult.
// The form's own annotations are checked here (what MVC model validation did), then RoomCreationService asks core.
public sealed class CreatorPresenter
{
    private readonly IRoomCreationService _rooms;
    private readonly string _token;

    public CreatorPresenter(IRoomCreationService rooms, string token)
    {
        _rooms = rooms;
        _token = token;
        Form = NewForm();
    }

    public RoomForm Form { get; set; }
    public string? Error { get; private set; }
    public string? CreatedKey { get; private set; }
    public IReadOnlyList<string> Areas { get; private set; } = Array.Empty<string>();

    // Field-level problems, as (form property path, message); an empty path is a summary-only message.
    public List<RoomFieldError> Errors { get; } = new();

    public IEnumerable<string> ErrorsFor(string field) => Errors.Where(e => e.Field == field).Select(e => e.Message);

    public async Task LoadAreasAsync(CancellationToken cancellation = default)
    {
        Areas = await _rooms.ListAreasAsync(_token, cancellation);
    }

    public async Task<CreatorOutcome> CreateAsync(CancellationToken cancellation = default)
    {
        Error = null;
        CreatedKey = null;
        Errors.Clear();
        PadExits();
        if (!ValidateForm()) { return CreatorOutcome.Failed; }

        RoomCreationResult result;
        try
        {
            result = await _rooms.CreateAsync(Form, _token, cancellation);
        }
        catch (CoreUnavailableException)
        {
            Error = Messages.Unavailable;
            return CreatorOutcome.Failed;
        }
        switch (result.Status)
        {
            case RoomCreationStatus.Created:
                CreatedKey = result.CreatedKey;
                return CreatorOutcome.Created;
            case RoomCreationStatus.Invalid:
                foreach (var e in result.FieldErrors) { Errors.Add(new RoomFieldError(FormField(e.Field), e.Message)); }
                break;
            case RoomCreationStatus.KeyExists:
                Error = Messages.RoomKeyExists;
                break;
            case RoomCreationStatus.Forbidden:
                Error = Messages.RoomCreateForbidden;
                break;
            default:
                Error = Messages.RoomCreateFailed;
                break;
        }
        return CreatorOutcome.Failed;
    }

    // Maps a writer field name onto the form's property path (unknown names are summary-only).
    private static string FormField(string field) => field switch
    {
        "Key" or "Area" or "Short" or "Long" or "X" or "Y" or "Z" or "ConfirmDangling" => field,
        _ when field.StartsWith("Exits[", StringComparison.Ordinal) => field,
        _ => string.Empty,
    };

    // What MVC model validation did for the Razor page: property annotations on the form and on each exit, then IValidatableObject.
    private bool ValidateForm()
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(Form, new ValidationContext(Form), results, validateAllProperties: true);
        for (var i = 0; i < Form.Exits.Count; i++)
        {
            var exit = Form.Exits[i];
            if (exit == null) { continue; }
            var exitResults = new List<ValidationResult>();
            Validator.TryValidateObject(exit, new ValidationContext(exit), exitResults, validateAllProperties: true);
            foreach (var r in exitResults)
            {
                foreach (var m in r.MemberNames.DefaultIfEmpty(string.Empty)) { Errors.Add(new RoomFieldError("Exits[" + i + "]." + m, r.ErrorMessage ?? string.Empty)); }
            }
        }
        foreach (var r in results)
        {
            foreach (var m in r.MemberNames.DefaultIfEmpty(string.Empty)) { Errors.Add(new RoomFieldError(m, r.ErrorMessage ?? string.Empty)); }
        }
        return Errors.Count == 0;
    }

    private void PadExits()
    {
        Form ??= NewForm();
        if (Form.Exits.Count > RoomForm.ExitRows)
        {
            Form.Exits = Form.Exits.Take(RoomForm.ExitRows).ToList();
        }
        while (Form.Exits.Count < RoomForm.ExitRows) { Form.Exits.Add(new ExitInput()); }
    }

    public static RoomForm NewForm()
    {
        var f = new RoomForm();
        for (var i = 0; i < RoomForm.ExitRows; i++) { f.Exits.Add(new ExitInput()); }
        return f;
    }
}
