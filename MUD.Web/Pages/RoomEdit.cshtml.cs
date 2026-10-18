using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MUD.Web.Services;

namespace MUD.Web.Pages;

// /creator/rooms/{key}: edit one room's definition fields. All messages are fixed strings; nothing from the request or
// from an exception is echoed, and Razor encodes every value it renders.
[Authorize(Policy = WebPolicies.CreatorOrAdmin)]
public class RoomEditModel : PageModel
{
    public const string ConflictError = "This room was changed by someone else while you were editing. Your changes were not saved. The current values are shown on the right; review them and save again if you still want your changes.";
    public const string NotFoundError = "That room no longer exists.";
    public const string FailedError = "The room could not be saved. Please try again later.";
    public const string AuditFailedError = "The change could not be recorded, so nothing was saved. Please try again later.";
    public const string ForbiddenError = "You are not allowed to edit rooms.";
    public const int MaxFormExits = RoomValidator.MaxExits;

    private readonly IRoomWriter _writer;
    private readonly IRoomEditStore _store;
    private readonly IAccountService _accounts;

    public RoomEditModel(IRoomWriter writer, IRoomEditStore store, IAccountService accounts)
    {
        _writer = writer;
        _store = store;
        _accounts = accounts;
    }

    public static string RoomUrl(string key) => "/creator/rooms/" + Uri.EscapeDataString(key);

    public string Key { get; private set; } = string.Empty;

    // Never bound: the key comes from the route only, so a form cannot redirect a save to another room.
    public RoomDetail? Current { get; private set; }

    [BindProperty] public int Rev { get; set; }
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
    public bool Conflict { get; private set; }
    public bool Missing { get; private set; }

    public IActionResult OnGet(string key)
    {
        Key = key;
        var room = _store.Find(key);
        if (room == null) { return NotFoundPage(); }
        Load(room);
        return Page();
    }

    public IActionResult OnPostSave(string key)
    {
        Key = key;
        TrimExits();
        var actor = Actor();
        if (actor == null) { Error = ForbiddenError; Current = _store.Find(key); return Current == null ? NotFoundPage() : Page(); }

        var edit = new RoomEdit(key, Short, Long, Area, X, Y, Z, Exits.Select(ToData).ToList(), Rev, ConfirmDangling);
        var result = _writer.Save(actor, edit);
        switch (result.Status)
        {
            case RoomSaveStatus.Saved:
                Load(result.Current!);
                Notice = "Saved. The running game picks the change up within a few seconds.";
                ModelState.Clear();
                return Page();
            case RoomSaveStatus.NoChange:
                Load(result.Current!);
                Notice = "Nothing was changed.";
                ModelState.Clear();
                return Page();
            case RoomSaveStatus.Invalid:
                Current = result.Current;
                foreach (var e in result.FieldErrors) { ModelState.AddModelError(string.Empty, e.Message); }
                return Page();
            case RoomSaveStatus.Conflict:
                Conflict = true;
                Error = ConflictError;
                Current = result.Current;
                Rev = result.Current?.Rev ?? Rev; // the user's input stays in the form; saving again is deliberate
                ModelState.Remove(nameof(Rev));
                return Page();
            case RoomSaveStatus.NotFound:
                return NotFoundPage();
            case RoomSaveStatus.AuditFailed:
                Error = AuditFailedError;
                Current = result.Current ?? _store.Find(key);
                return Page();
            case RoomSaveStatus.Forbidden:
                Error = ForbiddenError;
                Current = _store.Find(key);
                return Page();
            default:
                Error = FailedError;
                Current = _store.Find(key);
                return Page();
        }
    }

    // Adds an empty exit row (no save).
    public IActionResult OnPostAddExit(string key)
    {
        Key = key;
        Current = _store.Find(key);
        if (Current == null) { return NotFoundPage(); }
        if (Exits.Count < MaxFormExits) { Exits.Add(new ExitInput()); }
        ModelState.Clear();
        return Page();
    }

    // Removes one exit row from the form (no save).
    public IActionResult OnPostRemoveExit(string key, int index)
    {
        Key = key;
        Current = _store.Find(key);
        if (Current == null) { return NotFoundPage(); }
        if (index >= 0 && index < Exits.Count) { Exits.RemoveAt(index); }
        ModelState.Clear();
        return Page();
    }

    public IActionResult OnPostDelete(string key)
    {
        Key = key;
        var actor = Actor();
        if (actor == null) { Error = ForbiddenError; Current = _store.Find(key); return Current == null ? NotFoundPage() : Page(); }

        var result = _writer.Delete(actor, key, Rev);
        switch (result.Status)
        {
            case RoomSaveStatus.Deleted:
                TempData["Notice"] = "Room deleted. Anything that was inside is moved by the running game within a few seconds.";
                return Redirect("/creator/map");
            case RoomSaveStatus.Refused:
                Error = result.Message;
                Current = result.Current ?? _store.Find(key);
                return Current == null ? NotFoundPage() : Page();
            case RoomSaveStatus.Conflict:
                Conflict = true;
                Error = ConflictError;
                Current = result.Current;
                Rev = result.Current?.Rev ?? Rev;
                ModelState.Remove(nameof(Rev));
                return Page();
            case RoomSaveStatus.NotFound:
                return NotFoundPage();
            case RoomSaveStatus.AuditFailed:
                Error = AuditFailedError;
                Current = result.Current ?? _store.Find(key);
                return Page();
            default:
                Error = FailedError;
                Current = _store.Find(key);
                return Page();
        }
    }

    private RoomActor? Actor() => RoomActors.Resolve(_accounts, User.FindFirstValue(ClaimTypes.Name));

    private IActionResult NotFoundPage()
    {
        Missing = true;
        Error = NotFoundError;
        Response.StatusCode = StatusCodes.Status404NotFound;
        return Page();
    }

    private static RoomExitData ToData(ExitInput e) => new((e.Dir ?? string.Empty).Trim(), (e.To ?? string.Empty).Trim());

    private void TrimExits()
    {
        Exits ??= new List<ExitInput>();
        if (Exits.Count > MaxFormExits) { Exits = Exits.Take(MaxFormExits).ToList(); }
        Exits = Exits.Where(e => e != null).ToList();
    }

    private void Load(RoomDetail room)
    {
        Current = room;
        Rev = room.Rev;
        Short = room.Short;
        Long = room.Long;
        Area = room.Area;
        X = room.X;
        Y = room.Y;
        Z = room.Z;
        ConfirmDangling = false;
        Exits = room.Exits.Select(e => new ExitInput { Dir = e.Name, To = e.To }).ToList();
    }
}
