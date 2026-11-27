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
    // Core's field name for each entry of Problems (same order), so the page can open the section that holds the field.
    public List<string> ProblemFields { get; } = new();
    public string? NewKey { get; private set; }

    // Generator provenance of the loaded room (null for authored rooms). Regen and Update show only when it is set.
    public string? GenMap { get; private set; }
    public string? GenTerrain { get; private set; }
    public bool IsGenerated => GenTerrain != null;

    // Read-only views of the room's behavior entries and search entries, in core's stored order. Never sent back on save
    // (RoomEditRequest has no field for them). Empty when core is older and does not send them.
    public IReadOnlyList<BehaviorRow> Behaviors { get; private set; } = Array.Empty<BehaviorRow>();
    public IReadOnlyList<SearchRow> Searches { get; private set; } = Array.Empty<SearchRow>();

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
                    foreach (var e in reply.Errors) { Problems.Add(FeatureErrorText(e)); ProblemFields.Add(e.Field ?? string.Empty); }
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

    // "Link Adjacent Rooms": adds exits to the form only (no write, no core call). The user reviews them and presses Save.
    public LinkAdjacentResult LinkAdjacent(IReadOnlyList<RoomRecord> rooms)
    {
        ClearMessages();
        Exits ??= new List<ExitInput>();
        var plan = AdjacentRoomLinker.Plan(Key, Area, X, Y, Z, rooms, Exits.Where(e => e != null).Select(e => e.Dir));
        var added = new List<ExitInput>();
        var skipped = 0;
        foreach (var exit in plan.Add)
        {
            if (Exits.Count >= MaxFormExits) { skipped++; continue; }
            var input = new ExitInput { Dir = exit.Name, To = exit.To };
            Exits.Add(input);
            added.Add(input);
        }
        return new LinkAdjacentResult(added, skipped, plan.Conflicts);
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

    // The area records for the move form's dropdown; none when core cannot answer.
    public async Task<IReadOnlyList<AreaRecord>> GetAreasAsync(CancellationToken cancellation = default)
    {
        try
        {
            return (await _core.ListAreasAsync(_token, cancellation)).OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
        }
        catch (CoreUnavailableException) { return Array.Empty<AreaRecord>(); }
        catch (CoreRefusedException) { return Array.Empty<AreaRecord>(); }
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
                    foreach (var e in reply.Errors) { Problems.Add(e.Message); ProblemFields.Add(e.Field ?? string.Empty); }
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
        var fields = ProblemFields.ToList();
        try
        {
            var room = await _core.GetRoomAsync(_token, Key);
            if (room == null) { return NotFoundPage(); }
            Load(room);
            Error = error;
            Notice = notice;
            Problems.Clear();
            Problems.AddRange(problems);
            ProblemFields.Clear();
            ProblemFields.AddRange(fields);
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
        ProblemFields.Clear();
    }

    private void ClearMessages()
    {
        Problems.Clear();
        ProblemFields.Clear();
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

    // Core sends compact JSON. Indent it for reading; text that is not valid JSON is shown as it came.
    public static string PrettyJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) { return "{}"; }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return System.Text.Json.JsonSerializer.Serialize(doc.RootElement, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }
        catch (System.Text.Json.JsonException) { return json; }
    }

    private void Load(RoomDetail room)
    {
        Behaviors = room.Behaviors.Select(b => new BehaviorRow(b.Hook, b.Module, b.Type, b.Priority, PrettyJson(b.ConfigJson))).ToList();
        Searches = room.Searches.Select(x => new SearchRow(x.Id, x.Target, x.RefreshMs, x.RewardShort, x.Discovery == null ? null : new SearchDiscoveryRow(x.Discovery.Id, x.Discovery.Found, x.Discovery.Again))).ToList();
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

/// <summary>The outcome of "Link Adjacent Rooms". Status is the fixed-text line the page shows; nothing was written anywhere.</summary>
public sealed record LinkAdjacentResult(IReadOnlyList<ExitInput> Added, int NotAddedForLimit, IReadOnlyList<AdjacentConflict> Conflicts)
{
    public string Status
    {
        get
        {
            var text = Added.Count == 0
                ? Messages.LinkAdjacentNone
                : "Added " + Added.Count + (Added.Count == 1 ? " exit" : " exits") + " to the form: "
                  + string.Join(", ", Added.Select(e => e.Dir + " -> " + e.To)) + ". Save to apply.";
            if (NotAddedForLimit > 0) { text += " " + NotAddedForLimit + " more not added: the form holds at most " + RoomEditPresenter.MaxFormExits + " exits."; }
            return text;
        }
    }

    public IEnumerable<string> Warnings => Conflicts.Select(c =>
        "Not linked " + c.Direction + ": these rooms share the same coordinates: " + string.Join(", ", c.Keys) + ".");
}

/// <summary>One behavior entry of the room as shown read-only on the edit page. Config is pretty-printed JSON text.</summary>
public sealed record BehaviorRow(string Hook, string Module, string Type, int Priority, string Config);

/// <summary>One entry of the room's search component as shown read-only on the edit page.</summary>
public sealed record SearchRow(string Id, string Target, int RefreshMs, string RewardShort, SearchDiscoveryRow? Discovery = null)
{
    /// <summary>The refresh time in readable units (10 minutes, 1 hour 30 minutes, 500 ms).</summary>
    public string Cooldown => Duration(RefreshMs);

    /// <summary>One line saying what typing the search does: the reward and/or the discovery.</summary>
    public string Summary
    {
        get
        {
            var what = new List<string>();
            if (!string.IsNullOrEmpty(RewardShort)) { what.Add($"finds {RewardShort} (again after {Cooldown})"); }
            if (Discovery != null) { what.Add($"records the discovery '{Discovery.Id}' for the player (once per player)"); }
            return $"Typing `search {Target}` " + (what.Count == 0 ? "finds nothing." : string.Join(" and ", what) + ".");
        }
    }

    public static string Duration(int ms)
    {
        if (ms < 1000) { return ms + " ms"; }
        var parts = new List<string>();
        var rest = (long)ms;
        foreach (var (unit, size) in new[] { ("day", 86400000L), ("hour", 3600000L), ("minute", 60000L), ("second", 1000L) })
        {
            var n = rest / size;
            rest %= size;
            if (n > 0) { parts.Add(n + " " + unit + (n == 1 ? "" : "s")); }
        }
        if (rest > 0) { parts.Add(rest + " ms"); }
        return string.Join(" ", parts);
    }
}

/// <summary>The discovery of a search entry (per-player flag id and its two messages). Messages may be empty: core then uses a default text.</summary>
public sealed record SearchDiscoveryRow(string Id, string Found, string Again);

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
