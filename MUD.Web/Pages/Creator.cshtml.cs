using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MUD.Web.Services;

namespace MUD.Web.Pages;

[Authorize(Policy = WebPolicies.CreatorOrAdmin)]
public class CreatorModel : PageModel
{
    public const string KeyExistsError = "A room with that key already exists.";
    public const string FailedError = "The room could not be created. Please try again later.";
    public const string ForbiddenError = "You are not allowed to create rooms.";

    private readonly IRoomCreationService _rooms;

    public CreatorModel(IRoomCreationService rooms) => _rooms = rooms;

    [BindProperty] public RoomForm Form { get; set; } = NewForm();
    public string? Error { get; private set; }

    public void OnGet() => Form = NewForm();

    public IActionResult OnPost()
    {
        PadExits();
        if (!ModelState.IsValid) { return Page(); }

        var result = _rooms.Create(Form, User.FindFirstValue(ClaimTypes.Name) ?? string.Empty);
        switch (result.Status)
        {
            case RoomCreationStatus.Created:
                return Redirect(RoomEditModel.RoomUrl(result.CreatedKey!));
            case RoomCreationStatus.Invalid:
                foreach (var e in result.FieldErrors) { ModelState.AddModelError(FormField(e.Field), e.Message); }
                break;
            case RoomCreationStatus.KeyExists:
                Error = KeyExistsError;
                break;
            case RoomCreationStatus.Forbidden:
                Error = ForbiddenError;
                break;
            default:
                Error = FailedError;
                break;
        }
        return Page();
    }

    // Maps a writer field name onto the form's property path.
    private static string FormField(string field) => field switch
    {
        "Key" or "Area" or "Short" or "Long" or "X" or "Y" or "Z" or "ConfirmDangling" => "Form." + field,
        _ when field.StartsWith("Exits[", StringComparison.Ordinal) => "Form." + field,
        _ => string.Empty,
    };

    private void PadExits()
    {
        Form ??= NewForm();
        if (Form.Exits.Count > RoomForm.ExitRows)
        {
            Form.Exits = Form.Exits.Take(RoomForm.ExitRows).ToList();
        }
        while (Form.Exits.Count < RoomForm.ExitRows) { Form.Exits.Add(new ExitInput()); }
    }

    private static RoomForm NewForm()
    {
        var f = new RoomForm();
        for (var i = 0; i < RoomForm.ExitRows; i++) { f.Exits.Add(new ExitInput()); }
        return f;
    }
}
