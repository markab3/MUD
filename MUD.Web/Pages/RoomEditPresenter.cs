using MUD.Contracts;
using MUD.Web.Services;

namespace MUD.Web.Pages;

// Why the page shows no form: the room is gone, core cannot be reached, or the session may no longer edit rooms.
public enum RoomEditMissing { None, NotFound, Unavailable, Forbidden }

public enum RoomEditOutcome
{
    Ok,         // done, the form shows the stored values and Notice may be set
    Problem,    // Error or Problems is set; the form stays as the user left it
    Missing,    // Missing says why; the form is not shown
    Moved,      // the room has a new key: NewKey
    Deleted,    // the room is gone
}

// The room edit logic (moved from RoomEditModel), free of HttpContext, ModelState, TempData and IActionResult. Core validates, audits and
// applies the edit (last write wins, D30). All messages are fixed strings; nothing from an exception is echoed. The room key and the token come
// from the constructor (route and circuit) only, so the form cannot redirect a save to another room.
public sealed class RoomEditPresenter
{
    // Web-only guard on form size; core applies the same limit (AdminRoomValidator.MaxExits).
    public const int MaxFormExits = 32;
    // Same limit as core (AdminFeatureValidator.MaxFeatures).
    public const int MaxFormFeatures = 64;

    private readonly IAdminClient _core;
    private readonly string _token;

    public RoomEditPresenter(IAdminClient core, string token, string key)
    {
        _core = core;
        _token = token;
        Key = key;
    }

    // Never settable from the form: the key comes from the route only.
    public string Key { get; }

    public string? Short { get; set; }
    public string? Long { get; set; }
    public string? Area { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? Z { get; set; }
    public bool ConfirmDangling { get; set; }
    public List<ExitInput> Exits { get; set; } = new();
    public List<FeatureInput> Features { get; set; } = new();
    public string? NewArea { get; set; }
    public string? NewLocalKey { get; set; }

    public string? Error { get; private set; }
    public string? Notice { get; set; }
    public RoomEditMissing Missing { get; private set; }
    // Core's validation sentences (with "Feature N: " where it names a feature), in core's order.
    public List<string> Problems { get; } = new();
    public string? NewKey { get; private set; }

    // Generator provenance of the loaded room (null for authored rooms). Regen and Update show only when it is set.
    public string? GenMap { get; private set; }
    public string? GenTerrain { get; private set; }
    public bool IsGenerated => GenTerrain != null;

    public bool IsMissing => Missing != RoomEditMissing.None;

    public async Task<RoomEditOutcome> LoadAsync(CancellationToken cancellation = default)
    {
        try
        {
            var room = await _core.GetRoomAsync(_token, Key, cancellation);
            if (room == null) { return NotFoundPage(); }
            Load(room);
            return RoomEditOutcome.Ok;
        }
        catch (CoreUnavailableException) { return UnavailablePage(); }
        catch (CoreRefusedException) { return ForbiddenPage(); }
    }

    public async Task<RoomEditOutcome> SaveAsync(CancellationToken cancellation = default)
    {
        Reset();
        TrimExits();
        TrimFeatures();
        try
        {
            var request = new RoomEditRequest
            {
                Key = Key,
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

            var reply = await _core.UpdateRoomAsync(_token, request, cancellation);
            switch (reply.Result)
            {
                case AdminResult.Ok:
                    if (reply.Room != null) { Load(reply.Room); }
                    Notice = Messages.RoomSaved;
                    return RoomEditOutcome.Ok;
                case AdminResult.NoChange:
                    if (reply.Room != null) { Load(reply.Room); }
                    Notice = Messages.RoomNoChange;
                    return RoomEditOutcome.Ok;
                case AdminResult.Invalid:
                    foreach (var e in reply.Errors) { Problems.Add(FeatureErrorText(e)); }
                    return RoomEditOutcome.Problem;
                case AdminResult.NotFound:
                    return NotFoundPage();
                case AdminResult.AuditFailed:
                    return Fail(Messages.RoomAuditFailed);
                case AdminResult.Forbidden:
                    return Fail(Messages.RoomForbidden);
                default:
                    return Fail(Messages.RoomSaveFailed);
            }
        }
        catch (CoreUnavailableException) { return Fail(Messages.Unavailable); }
        catch (CoreRefusedException) { return Fail(Messages.RoomForbidden); }
    }

    // Row edits are local to the form (no write, no core call): they change the form only.
    public void AddExit()
    {
        ClearMessages();
        if (Exits.Count < MaxFormExits) { Exits.Add(new ExitInput()); }
    }

    public void RemoveExit(int index)
    {
        ClearMessages();
        if (index >= 0 && index < Exits.Count) { Exits.RemoveAt(index); }
    }

    public void AddFeature()
    {
        ClearMessages();
        TrimFeatures();
        if (Features.Count < MaxFormFeatures) { Features.Add(new FeatureInput()); }
    }

    public void RemoveFeature(int index)
    {
        ClearMessages();
        TrimFeatures();
        if (index >= 0 && index < Features.Count) { Features.RemoveAt(index); }
    }

    public void MoveFeatureUp(int index) => MoveFeature(index, -1);

    public void MoveFeatureDown(int index) => MoveFeature(index, 1);

    // Swaps a feature row with its neighbour in the form.
    private void MoveFeature(int index, int step)
    {
        ClearMessages();
        TrimFeatures();
        var target = index + step;
        if (index >= 0 && index < Features.Count && target >= 0 && target < Features.Count)
        {
            (Features[index], Features[target]) = (Features[target], Features[index]);
        }
    }

    // Existing area prefixes for the move form's suggestions; none when core cannot answer.
    public async Task<IReadOnlyList<string>> GetAreasAsync(CancellationToken cancellation = default)
    {
        try
        {
            var rooms = await _core.ListRoomsAsync(_token, cancellation);
            return rooms.Select(r => r.Area).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal).ToList();
        }
        catch (CoreUnavailableException) { return Array.Empty<string>(); }
        catch (CoreRefusedException) { return Array.Empty<string>(); }
    }

    // Gives the room a new key (area prefix and/or local key). Players inside, contents and exits that led here follow it.
    public async Task<RoomEditOutcome> MoveAsync(CancellationToken cancellation = default)
    {
        Reset();
        try
        {
            var reply = await _core.MoveRoomAsync(_token, Key, (NewArea ?? string.Empty).Trim(), (NewLocalKey ?? string.Empty).Trim(), cancellation);
            switch (reply.Result)
            {
                case AdminResult.Ok:
                    Notice = string.IsNullOrEmpty(reply.Message) ? Messages.RoomMoved : reply.Message;
                    NewKey = reply.Room?.Key ?? Key;
                    return RoomEditOutcome.Moved;
                case AdminResult.NoChange:
                    Notice = Messages.RoomNoChange;
                    break;
                case AdminResult.Invalid:
                    foreach (var e in reply.Errors) { Problems.Add(e.Message); }
                    break;
                case AdminResult.AlreadyExists:
                    Error = Messages.RoomKeyExists;
                    break;
                case AdminResult.Refused:
                    Error = string.IsNullOrEmpty(reply.Message) ? Messages.RoomSaveFailed : reply.Message;
                    break;
                case AdminResult.NotFound:
                    return NotFoundPage();
                case AdminResult.AuditFailed:
                    Error = Messages.RoomAuditFailed;
                    break;
                case AdminResult.Forbidden:
                    Error = Messages.RoomForbidden;
                    break;
                default:
                    Error = Messages.RoomSaveFailed;
                    break;
            }
            return await ReloadAfterError();
        }
        catch (CoreUnavailableException) { return UnavailablePage(); }
        catch (CoreRefusedException) { return ForbiddenPage(); }
    }

    // Regen: throws away hand edits by design (no lock). The component asks first (Messages.RegenWarning); core is authoritative.
    public Task<RoomEditOutcome> RegenAsync(CancellationToken cancellation = default) => RunGeneration(regen: true, cancellation);

    // Update: rewrites only features that came from a library template, when the template is newer. Never touches the description.
    public Task<RoomEditOutcome> UpdateAsync(CancellationToken cancellation = default) => RunGeneration(regen: false, cancellation);

    private async Task<RoomEditOutcome> RunGeneration(bool regen, CancellationToken cancellation)
    {
        Reset();
        try
        {
            var reply = regen ? await _core.RegenerateRoomsAsync(_token, Key, cancellation) : await _core.UpdateRoomsAsync(_token, Key, cancellation);
            var line = reply.Rooms.FirstOrDefault();
            if (line != null && line.Outcome == RoomGenOutcome.RoomGenNotGenerated)
            {
                Error = Messages.RoomNotGenerated;
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
                    case AdminResult.Refused: Error = Messages.LibraryUnavailable; break;
                    case AdminResult.AuditFailed: Error = Messages.RoomAuditFailed; break;
                    case AdminResult.Forbidden: Error = Messages.RoomForbidden; break;
                    default: Error = regen ? Messages.RegenFailed : Messages.UpdateFailed; break;
                }
            }
            return await ReloadAfterError();
        }
        catch (CoreUnavailableException) { return UnavailablePage(); }
        catch (CoreRefusedException) { return ForbiddenPage(); }
    }

    // Fixed wording around core's counts; nothing else from the reply is echoed.
    public static string GenerationNotice(GenerationReply reply, bool regen) => regen
        ? "Regenerated: " + reply.UpdatedCount + " room replaced, " + reply.UnchangedCount + " unchanged."
        : "Updated: " + reply.UpdatedCount + " features updated, " + reply.UnchangedCount + " already current, " + reply.DeletedTemplateCount + " whose template was deleted (left as they are).";

    public async Task<RoomEditOutcome> DeleteAsync(CancellationToken cancellation = default)
    {
        Reset();
        try
        {
            var reply = await _core.DeleteRoomAsync(_token, Key, cancellation);
            switch (reply.Result)
            {
                case AdminResult.Ok:
                    Notice = Messages.RoomDeleted;
                    return RoomEditOutcome.Deleted;
                case AdminResult.Refused:
                    // Core's fixed sentence; it never contains stored values.
                    Error = string.IsNullOrEmpty(reply.Message) ? Messages.RoomSaveFailed : reply.Message;
                    return await ReloadAfterError();
                case AdminResult.NotFound:
                    return NotFoundPage();
                case AdminResult.AuditFailed:
                    Error = Messages.RoomAuditFailed;
                    return await ReloadAfterError();
                case AdminResult.Forbidden:
                    Error = Messages.RoomForbidden;
                    return await ReloadAfterError();
                default:
                    Error = Messages.RoomSaveFailed;
                    return await ReloadAfterError();
            }
        }
        catch (CoreUnavailableException) { return UnavailablePage(); }
        catch (CoreRefusedException) { return ForbiddenPage(); }
    }

    // After a refused action the form shows the stored values again. A pending Error or Notice is kept; the problem list is cleared by the reload only on success paths.
    private async Task<RoomEditOutcome> ReloadAfterError()
    {
        var error = Error;
        var notice = Notice;
        var problems = Problems.ToList();
        try
        {
            var room = await _core.GetRoomAsync(_token, Key);
            if (room == null) { return NotFoundPage(); }
            Load(room);
            Error = error;
            Notice = notice;
            Problems.Clear();
            Problems.AddRange(problems);
            return error != null || problems.Count > 0 ? RoomEditOutcome.Problem : RoomEditOutcome.Ok;
        }
        catch (CoreUnavailableException) { return UnavailablePage(); }
        catch (CoreRefusedException) { return ForbiddenPage(); }
    }

    private RoomEditOutcome Fail(string message)
    {
        Error = message;
        return RoomEditOutcome.Problem;
    }

    private RoomEditOutcome NotFoundPage() => MissingPage(RoomEditMissing.NotFound, Messages.RoomNotFound);

    private RoomEditOutcome UnavailablePage() => MissingPage(RoomEditMissing.Unavailable, Messages.Unavailable);

    private RoomEditOutcome ForbiddenPage() => MissingPage(RoomEditMissing.Forbidden, Messages.RoomForbidden);

    private RoomEditOutcome MissingPage(RoomEditMissing why, string message)
    {
        Missing = why;
        Error = message;
        return RoomEditOutcome.Missing;
    }

    private void Reset()
    {
        Error = null;
        Notice = null;
        NewKey = null;
        Problems.Clear();
    }

    private void ClearMessages()
    {
        Problems.Clear();
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
