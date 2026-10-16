using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MUD.Web.Services;

namespace MUD.Web.Pages;

[Authorize(Policy = WebPolicies.CreatorOrAdmin)]
public class CreatorModel : PageModel
{
    private readonly IRoomCreationService _rooms;

    public CreatorModel(IRoomCreationService rooms) => _rooms = rooms;

    [BindProperty] public RoomForm Form { get; set; } = NewForm();
    public bool NotImplemented { get; private set; }

    public void OnGet() => Form = NewForm();

    public IActionResult OnPost()
    {
        PadExits();
        if (!ModelState.IsValid) { return Page(); }

        var result = _rooms.Create(Form, User.FindFirstValue(ClaimTypes.Name) ?? string.Empty);
        if (result.Status == RoomCreationStatus.NotImplemented) { NotImplemented = true; }
        return Page();
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

    private static RoomForm NewForm()
    {
        var f = new RoomForm();
        for (var i = 0; i < RoomForm.ExitRows; i++) { f.Exits.Add(new ExitInput()); }
        return f;
    }
}
