using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MUD.Contracts;
using MUD.Web.Services;

namespace MUD.Web.Pages;

// /creator/rooms/{key}: edit one room's definition fields through core's Admin service. Core validates, audits and applies the edit
// (last write wins, D30). All messages are fixed strings; nothing from the request or from an exception is echoed, and Razor encodes
// every value it renders.
[Authorize(Policy = WebPolicies.CreatorOrAdmin)]
public class RoomEditModel : PageModel
{
    public const string NotFoundError = "That room no longer exists.";
    public const string FailedError = "The room could not be saved. Please try again later.";
    public const string AuditFailedError = "The change could not be recorded, so nothing was saved. Please try again later.";
    public const string ForbiddenError = "You are not allowed to edit rooms.";
    public const string UnavailableError = "The game is not reachable right now. Please try again later.";

    // Web-only guard on form size; core applies the same limit (AdminRoomValidator.MaxExits).
    public const int MaxFormExits = 32;

    private readonly IAdminClient _core;

    public RoomEditModel(IAdminClient core)
    {
        _core = core;
    }

    public static string RoomUrl(string key) => "/creator/rooms/" + Uri.EscapeDataString(key);

    // Never bound: the key comes from the route only, so a form cannot redirect a save to another room.
    public string Key { get; private set; } = string.Empty;

    [BindProperty] public string? Short { get; set; }
    [BindProperty] public string? Long { get; set; }
    [BindProperty] public string? Area { get; set; }
    [BindProperty] public int? X { get; set; }
    [BindProperty] public int? Y { get; set; }
    [BindProperty] public int? Z { get; set; }
    [BindProperty] public bool ConfirmDangling { get; set; }
    [BindProperty] public List<ExitInput> Exits { get; set; } = new();

    public string? Error { get; private set; }
    public string? Notice { get; private set; }
    public bool Missing { get; private set; }

    private string Token => SessionAuthentication.TokenFrom(Request);

    public async Task<IActionResult> OnGetAsync(string key)
    {
        Key = key;
        try
        {
            var room = await _core.GetRoomAsync(Token, key, HttpContext.RequestAborted);
            if (room == null) { return NotFoundPage(); }
            Load(room);
            return Page();
        }
        catch (CoreUnavailableException) { return UnavailablePage(); }
        catch (CoreRefusedException) { return ForbiddenPage(); }
    }

    public async Task<IActionResult> OnPostSaveAsync(string key)
    {
        Key = key;
        TrimExits();
        try
        {
            var request = new RoomEditRequest
            {
                Key = key,
                ShortDescription = Short ?? string.Empty,
                LongDescription = Long ?? string.Empty,
                Area = Area ?? string.Empty,
                ConfirmDangling = ConfirmDangling,
            };
            if (X.HasValue) { request.X = X.Value; }
            if (Y.HasValue) { request.Y = Y.Value; }
            if (Z.HasValue) { request.Z = Z.Value; }
            request.Exits.AddRange(Exits.Select(ToExit));

            var reply = await _core.UpdateRoomAsync(Token, request, HttpContext.RequestAborted);
            switch (reply.Result)
            {
                case AdminResult.Ok:
                    if (reply.Room != null) { Load(reply.Room); }
                    Notice = "Saved. The running game has the change now.";
                    ModelState.Clear();
                    return Page();
                case AdminResult.NoChange:
                    if (reply.Room != null) { Load(reply.Room); }
                    Notice = "Nothing was changed.";
                    ModelState.Clear();
                    return Page();
                case AdminResult.Invalid:
                    foreach (var e in reply.Errors) { ModelState.AddModelError(string.Empty, e.Message); }
                    return Page();
                case AdminResult.NotFound:
                    return NotFoundPage();
                case AdminResult.AuditFailed:
                    Error = AuditFailedError;
                    return Page();
                case AdminResult.Forbidden:
                    Error = ForbiddenError;
                    return Page();
                default:
                    Error = FailedError;
                    return Page();
            }
        }
        catch (CoreUnavailableException) { Error = UnavailableError; return Page(); }
        catch (CoreRefusedException) { Error = ForbiddenError; return Page(); }
    }

    // Adds an empty exit row (no save).
    public async Task<IActionResult> OnPostAddExitAsync(string key)
    {
        Key = key;
        var gone = await CheckRoomExists(key);
        if (gone != null) { return gone; }
        if (Exits.Count < MaxFormExits) { Exits.Add(new ExitInput()); }
        ModelState.Clear();
        return Page();
    }

    // Removes one exit row from the form (no save).
    public async Task<IActionResult> OnPostRemoveExitAsync(string key, int index)
    {
        Key = key;
        var gone = await CheckRoomExists(key);
        if (gone != null) { return gone; }
        if (index >= 0 && index < Exits.Count) { Exits.RemoveAt(index); }
        ModelState.Clear();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string key)
    {
        Key = key;
        try
        {
            var reply = await _core.DeleteRoomAsync(Token, key, HttpContext.RequestAborted);
            switch (reply.Result)
            {
                case AdminResult.Ok:
                    TempData["Notice"] = "Room deleted. Anything that was inside has been moved.";
                    return Redirect("/creator/map");
                case AdminResult.Refused:
                    // Core's fixed sentence; it never contains stored values.
                    Error = string.IsNullOrEmpty(reply.Message) ? FailedError : reply.Message;
                    return await ReloadAfterError(key);
                case AdminResult.NotFound:
                    return NotFoundPage();
                case AdminResult.AuditFailed:
                    Error = AuditFailedError;
                    return await ReloadAfterError(key);
                case AdminResult.Forbidden:
                    Error = ForbiddenError;
                    return await ReloadAfterError(key);
                default:
                    Error = FailedError;
                    return await ReloadAfterError(key);
            }
        }
        catch (CoreUnavailableException) { return UnavailablePage(); }
        catch (CoreRefusedException) { return ForbiddenPage(); }
    }

    // After a refused delete the form shows the stored values again.
    private async Task<IActionResult> ReloadAfterError(string key)
    {
        try
        {
            var room = await _core.GetRoomAsync(Token, key, HttpContext.RequestAborted);
            if (room == null) { return NotFoundPage(); }
            Load(room);
            return Page();
        }
        catch (CoreUnavailableException) { return UnavailablePage(); }
        catch (CoreRefusedException) { return ForbiddenPage(); }
    }

    // Null when the room exists; otherwise the page to return.
    private async Task<IActionResult?> CheckRoomExists(string key)
    {
        try
        {
            return await _core.GetRoomAsync(Token, key, HttpContext.RequestAborted) == null ? NotFoundPage() : null;
        }
        catch (CoreUnavailableException) { return UnavailablePage(); }
        catch (CoreRefusedException) { return ForbiddenPage(); }
    }

    private IActionResult NotFoundPage()
    {
        Missing = true;
        Error = NotFoundError;
        Response.StatusCode = StatusCodes.Status404NotFound;
        return Page();
    }

    private IActionResult UnavailablePage()
    {
        Missing = true;
        Error = UnavailableError;
        Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return Page();
    }

    private IActionResult ForbiddenPage()
    {
        Missing = true;
        Error = ForbiddenError;
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Page();
    }

    private static RoomExit ToExit(ExitInput e) => new() { Name = (e.Dir ?? string.Empty).Trim(), To = (e.To ?? string.Empty).Trim() };

    private void TrimExits()
    {
        Exits ??= new List<ExitInput>();
        if (Exits.Count > MaxFormExits) { Exits = Exits.Take(MaxFormExits).ToList(); }
        Exits = Exits.Where(e => e != null).ToList();
    }

    private void Load(RoomDetail room)
    {
        Short = room.ShortDescription;
        Long = room.LongDescription;
        Area = room.Area;
        X = room.HasX ? room.X : null;
        Y = room.HasY ? room.Y : null;
        Z = room.HasZ ? room.Z : null;
        ConfirmDangling = false;
        Exits = room.Exits.Select(e => new ExitInput { Dir = e.Name, To = e.To }).ToList();
    }
}
