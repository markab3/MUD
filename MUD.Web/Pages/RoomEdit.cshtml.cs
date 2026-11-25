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
    public const string MoveExistsError = "A room with that key already exists.";
    public const string ForbiddenError = "You are not allowed to edit rooms.";
    public const string UnavailableError = "The game is not reachable right now. Please try again later.";

    // Web-only guard on form size; core applies the same limit (AdminRoomValidator.MaxExits).
    public const int MaxFormExits = 32;
    // Same limit as core (AdminFeatureValidator.MaxFeatures).
    public const int MaxFormFeatures = 64;

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
    [BindProperty] public List<FeatureInput> Features { get; set; } = new();

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
            if (TempData?["Notice"] is string carried) { Notice = carried; }
            return Page();
        }
        catch (CoreUnavailableException) { return UnavailablePage(); }
        catch (CoreRefusedException) { return ForbiddenPage(); }
    }

    public async Task<IActionResult> OnPostSaveAsync(string key)
    {
        Key = key;
        TrimExits();
        TrimFeatures();
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
            // The whole list in page order: core replaces the Features and keeps the behavior config of those matched by short.
            request.FeaturesPresent = true;
            request.Features.AddRange(Features.Select(ToFeature));

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
                    foreach (var e in reply.Errors) { ModelState.AddModelError(string.Empty, FeatureErrorText(e)); }
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

    // Adds an empty feature row (no save).
    public async Task<IActionResult> OnPostAddFeatureAsync(string key)
    {
        Key = key;
        var gone = await CheckRoomExists(key);
        if (gone != null) { return gone; }
        TrimFeatures();
        if (Features.Count < MaxFormFeatures) { Features.Add(new FeatureInput()); }
        ModelState.Clear();
        return Page();
    }

    // Removes one feature row from the form (no save).
    public async Task<IActionResult> OnPostRemoveFeatureAsync(string key, int index)
    {
        Key = key;
        var gone = await CheckRoomExists(key);
        if (gone != null) { return gone; }
        TrimFeatures();
        if (index >= 0 && index < Features.Count) { Features.RemoveAt(index); }
        ModelState.Clear();
        return Page();
    }

    public Task<IActionResult> OnPostMoveFeatureUpAsync(string key, int index) => MoveFeature(key, index, -1);

    public Task<IActionResult> OnPostMoveFeatureDownAsync(string key, int index) => MoveFeature(key, index, 1);

    // Swaps a feature row with its neighbour in the form (no save).
    private async Task<IActionResult> MoveFeature(string key, int index, int step)
    {
        Key = key;
        var gone = await CheckRoomExists(key);
        if (gone != null) { return gone; }
        TrimFeatures();
        var target = index + step;
        if (index >= 0 && index < Features.Count && target >= 0 && target < Features.Count)
        {
            (Features[index], Features[target]) = (Features[target], Features[index]);
        }
        ModelState.Clear();
        return Page();
    }

    [BindProperty] public string? NewArea { get; set; }
    [BindProperty] public string? NewLocalKey { get; set; }

    // Existing area prefixes for the move form's autocomplete; none when core cannot answer.
    public async Task<IReadOnlyList<string>> GetAreasAsync()
    {
        try
        {
            var rooms = await _core.ListRoomsAsync(Token, HttpContext.RequestAborted);
            return rooms.Select(r => r.Area).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal).ToList();
        }
        catch (CoreUnavailableException) { return Array.Empty<string>(); }
        catch (CoreRefusedException) { return Array.Empty<string>(); }
    }

    // Gives the room a new key (area prefix and/or local key). Players inside, contents and exits that led here follow it.
    public async Task<IActionResult> OnPostMoveAsync(string key)
    {
        Key = key;
        try
        {
            var reply = await _core.MoveRoomAsync(Token, key, (NewArea ?? string.Empty).Trim(), (NewLocalKey ?? string.Empty).Trim(), HttpContext.RequestAborted);
            switch (reply.Result)
            {
                case AdminResult.Ok:
                    TempData["Notice"] = string.IsNullOrEmpty(reply.Message) ? "Room moved." : reply.Message;
                    return Redirect(RoomUrl(reply.Room?.Key ?? key));
                case AdminResult.NoChange:
                    Notice = "Nothing was changed.";
                    break;
                case AdminResult.Invalid:
                    foreach (var e in reply.Errors) { ModelState.AddModelError(string.Empty, e.Message); }
                    break;
                case AdminResult.AlreadyExists:
                    Error = MoveExistsError;
                    break;
                case AdminResult.Refused:
                    Error = string.IsNullOrEmpty(reply.Message) ? FailedError : reply.Message;
                    break;
                case AdminResult.NotFound:
                    return NotFoundPage();
                case AdminResult.AuditFailed:
                    Error = AuditFailedError;
                    break;
                case AdminResult.Forbidden:
                    Error = ForbiddenError;
                    break;
                default:
                    Error = FailedError;
                    break;
            }
            return await ReloadAfterError(key);
        }
        catch (CoreUnavailableException) { return UnavailablePage(); }
        catch (CoreRefusedException) { return ForbiddenPage(); }
    }

    // Generator provenance of the loaded room (not bound; null for authored rooms). Regen and Update show only when it is set.
    public string? GenMap { get; private set; }
    public string? GenTerrain { get; private set; }
    public bool IsGenerated => GenTerrain != null;

    public const string RegenWarning = "Regen replaces the description of this room, ALL features (including ones you added by hand) and properties with new generator output. Your hand edits to them are lost. Exits and occupants stay. Continue?";

    // Regen: throws away hand edits by design (no lock). The browser asks first (the button's confirm text); core is authoritative.
    public Task<IActionResult> OnPostRegenAsync(string key) => RunGeneration(key, regen: true);

    // Update: rewrites only features that came from a library template, when the template is newer. Never touches the description.
    public Task<IActionResult> OnPostUpdateAsync(string key) => RunGeneration(key, regen: false);

    private async Task<IActionResult> RunGeneration(string key, bool regen)
    {
        Key = key;
        try
        {
            var reply = regen ? await _core.RegenerateRoomsAsync(Token, key, HttpContext.RequestAborted) : await _core.UpdateRoomsAsync(Token, key, HttpContext.RequestAborted);
            var line = reply.Rooms.FirstOrDefault();
            if (line != null && line.Outcome == RoomGenOutcome.RoomGenNotGenerated)
            {
                Error = NotGeneratedError;
            }
            else
            {
                switch (reply.Result)
                {
                    case AdminResult.Ok:
                    case AdminResult.NoChange:
                        Notice = GenerationNotice(reply, regen);
                        break;
                    case AdminResult.NotFound: return NotFoundPage();
                    case AdminResult.Refused: Error = LibraryUnavailableError; break;
                    case AdminResult.AuditFailed: Error = AuditFailedError; break;
                    case AdminResult.Forbidden: Error = ForbiddenError; break;
                    default: Error = regen ? RegenFailedError : UpdateFailedError; break;
                }
            }
            var after = await ReloadAfterError(key);
            ModelState.Clear();
            return after;
        }
        catch (CoreUnavailableException) { return UnavailablePage(); }
        catch (CoreRefusedException) { return ForbiddenPage(); }
    }

    public const string NotGeneratedError = "This room was not made by a generator, so it cannot be regenerated or updated.";
    public const string LibraryUnavailableError = "The terrain library is not available right now. Nothing was changed.";
    public const string RegenFailedError = "The room could not be regenerated. Please try again later.";
    public const string UpdateFailedError = "The room could not be updated. Please try again later.";

    // Fixed wording around core's counts; nothing else from the reply is echoed.
    public static string GenerationNotice(GenerationReply reply, bool regen) => regen
        ? "Regenerated: " + reply.UpdatedCount + " room replaced, " + reply.UnchangedCount + " unchanged."
        : "Updated: " + reply.UpdatedCount + " features updated, " + reply.UnchangedCount + " already current, " + reply.DeletedTemplateCount + " whose template was deleted (left as they are).";

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

    private static RoomFeature ToFeature(FeatureInput f)
    {
        var feature = new RoomFeature
        {
            Short = (f.Short ?? string.Empty).Trim(),
            Long = (f.Long ?? string.Empty).Trim(),
            RoomLine = (f.RoomLine ?? string.Empty).Trim(),
            Detail = (f.Detail ?? string.Empty).Trim(),
        };
        feature.Handles.AddRange(SplitWords(f.Handles));
        feature.Adjectives.AddRange(SplitWords(f.Adjectives));
        feature.Plurals.AddRange(SplitWords(f.Plurals));
        return feature;
    }

    // Words are separated by commas and/or white space; empty pieces are dropped.
    public static IEnumerable<string> SplitWords(string? text) =>
        (text ?? string.Empty).Split(new[] { ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

    // Core names a feature error "Features[i].Field"; say which feature (1-based, as on the page). The text stays core's fixed sentence.
    private static string FeatureErrorText(FieldError e)
    {
        var m = System.Text.RegularExpressions.Regex.Match(e.Field ?? string.Empty, @"^Features\[(\d+)\]\.");
        return m.Success && int.TryParse(m.Groups[1].Value, out var i) ? "Feature " + (i + 1) + ": " + e.Message : e.Message;
    }

    private void TrimFeatures()
    {
        Features ??= new List<FeatureInput>();
        if (Features.Count > MaxFormFeatures) { Features = Features.Take(MaxFormFeatures).ToList(); }
        Features = Features.Where(f => f != null).ToList();
    }

    private void TrimExits()
    {
        Exits ??= new List<ExitInput>();
        if (Exits.Count > MaxFormExits) { Exits = Exits.Take(MaxFormExits).ToList(); }
        Exits = Exits.Where(e => e != null).ToList();
    }

    private void Load(RoomDetail room)
    {
        var dot = room.Key.IndexOf('.');
        NewArea ??= dot > 0 ? room.Key.Substring(0, dot) : room.Area;
        NewLocalKey ??= dot > 0 ? room.Key.Substring(dot + 1) : room.Key;

        GenMap = room.Gen?.Map;
        GenTerrain = room.Gen?.Terrain;
        Short = room.ShortDescription;
        Long = room.LongDescription;
        Area = room.Area;
        X = room.HasX ? room.X : null;
        Y = room.HasY ? room.Y : null;
        Z = room.HasZ ? room.Z : null;
        ConfirmDangling = false;
        Exits = room.Exits.Select(e => new ExitInput { Dir = e.Name, To = e.To }).ToList();
        Features = room.Features.Select(f => new FeatureInput
        {
            Short = f.Short, Long = f.Long, RoomLine = f.RoomLine, Detail = f.Detail,
            Handles = string.Join(", ", f.Handles), Adjectives = string.Join(", ", f.Adjectives), Plurals = string.Join(", ", f.Plurals),
        }).ToList();
    }
}

/// <summary>One feature row of the room edit form. Each word list is one text field: words separated by commas or spaces.</summary>
public class FeatureInput
{
    public string? Short { get; set; }
    public string? Long { get; set; }
    public string? RoomLine { get; set; }
    public string? Detail { get; set; }
    public string? Handles { get; set; }
    public string? Adjectives { get; set; }
    public string? Plurals { get; set; }
}
