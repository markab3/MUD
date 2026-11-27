using System.Text.Json;
using MUD.Contracts;
using MUD.Web.Services;

namespace MUD.Web.Pages;

public enum ItemTemplateEditOutcome { Ok, NoChange, Invalid, Failed, Missing }
public enum ItemTemplateMissing { None, NotFound, Unavailable, Forbidden }

// One row of the behaviors editor. Config is JSON text; empty means {}.
public sealed class BehaviorEditRow
{
    public string Hook { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public int Priority { get; set; }
    public string Config { get; set; } = string.Empty;
}

// The item template create/edit logic (/creator/items/edit), free of HttpContext, ModelState and IActionResult (WEB_BLAZOR.md 4.1). The id
// comes from the address only and has no setter, so a form can never change which template is written; a new template takes its area from
// the dropdown and the local key from the input (the page shows the fixed prefix). Core validates every field and owns the version. The
// page checks only what it can say better: JSON that does not parse is reported next to the field and nothing is sent.
public sealed class ItemTemplateEditPresenter
{
    public const string GoneText = "That item template does not exist.";
    public const string ExistsError = "An item template with that id already exists.";
    public const int MaxMatchPhrases = 12;
    // The component data limit of core (BehaviorRef.MaxConfigBytes); core is the judge, this only tells the user.
    public const int MaxComponentBytes = 4096;
    // The hooks core knows (BehaviorHooks.All), offered as suggestions only: core validates the hook.
    public static readonly IReadOnlyList<string> KnownHooks = new[] { "heartbeat", "command.entered", "living.exit", "living.enter", "item.drop", "item.take" };

    private readonly IAdminClient _core;
    private readonly string _token;

    public ItemTemplateEditPresenter(IAdminClient core, string token, string? id)
    {
        _core = core;
        _token = token;
        OriginalId = string.IsNullOrWhiteSpace(id) ? null : id;
    }

    public string? OriginalId { get; }
    public bool IsNew => OriginalId == null;

    // New template only: the area (a dropdown of existing areas) and the local part of the key.
    public string? NewArea { get; set; }
    public string? NewLocal { get; set; }
    // Edit: the stored area (read-only).
    public string StoredArea { get; private set; } = string.Empty;
    public string? Short { get; set; }
    public string? Long { get; set; }
    // Comma or space separated words.
    public string? Handles { get; set; }
    public string? Adjectives { get; set; }
    public string? Plurals { get; set; }
    public string? Components { get; set; }
    public List<BehaviorEditRow> Behaviors { get; set; } = new();

    public int Version { get; private set; }
    public IReadOnlyList<AreaRecord> Areas { get; private set; } = Array.Empty<AreaRecord>();
    public string? Error { get; private set; }
    public string? Notice { get; set; }
    public ItemTemplateMissing Missing { get; private set; }
    public bool IsMissing => Missing != ItemTemplateMissing.None;

    // Core's field messages after an Invalid reply, and the page's own JSON messages; the user's input stays in the form.
    public List<string> Problems { get; } = new();

    public string KeyPrefix => string.IsNullOrEmpty(NewArea) ? string.Empty : NewArea + ".";
    public string FullId => IsNew ? KeyPrefix + (NewLocal ?? string.Empty).Trim() : OriginalId!;
    public string Area => IsNew ? NewArea ?? string.Empty : StoredArea;

    public string VersionHint => IsNew
        ? "A new template starts at version 1."
        : "Every saved change raises the version from " + Version + " to " + (Version + 1) + ", so a copy of the template can be recognised as out of date later.";

    // ---- preview: how the item looks and what a player can type to match it

    public string PreviewShort => (Short ?? string.Empty).Trim();
    public string PreviewLong => (Long ?? string.Empty).Trim();

    // What a player may type to refer to the item: every handle alone and with each adjective in front, capped. Empty without a handle.
    public IReadOnlyList<string> MatchPhrases
    {
        get
        {
            var handles = ItemTemplateWords.Split(Handles);
            var adjectives = ItemTemplateWords.Split(Adjectives);
            var phrases = new List<string>();
            foreach (var h in handles)
            {
                phrases.Add(h);
                foreach (var a in adjectives) { phrases.Add(a + " " + h); }
            }
            return phrases.Take(MaxMatchPhrases).ToList();
        }
    }

    public bool MatchPhrasesTruncated => ItemTemplateWords.Split(Handles).Count * (1 + ItemTemplateWords.Split(Adjectives).Count) > MaxMatchPhrases;

    // ---- behavior rows

    public void AddBehavior() => Behaviors.Add(new BehaviorEditRow());

    public bool RemoveBehavior(int index)
    {
        if (index < 0 || index >= Behaviors.Count) { return false; }
        Behaviors.RemoveAt(index);
        return true;
    }

    // ---- load and save

    public async Task<ItemTemplateEditOutcome> LoadAsync(CancellationToken cancellation = default)
    {
        Error = null;
        Problems.Clear();
        Missing = ItemTemplateMissing.None;
        try
        {
            Areas = (await _core.ListAreasAsync(_token, cancellation)).OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
            if (IsNew) { return ItemTemplateEditOutcome.Ok; }
            var item = await _core.GetItemTemplateAsync(_token, OriginalId!, cancellation);
            if (item == null) { return Gone(ItemTemplateMissing.NotFound, GoneText); }
            Fill(item);
            return ItemTemplateEditOutcome.Ok;
        }
        catch (CoreUnavailableException) { return Gone(ItemTemplateMissing.Unavailable, ItemTemplateListPresenter.UnavailableError); }
        catch (CoreRefusedException) { return Gone(ItemTemplateMissing.Forbidden, ItemTemplateListPresenter.ForbiddenError); }
        catch (Exception) { return Gone(ItemTemplateMissing.Unavailable, ItemTemplateListPresenter.FailedError); }
    }

    private void Fill(ItemTemplateRecord item)
    {
        StoredArea = item.Area;
        Short = item.Short;
        Long = item.Long;
        Handles = ItemTemplateWords.Join(item.Handles);
        Adjectives = ItemTemplateWords.Join(item.Adjectives);
        Plurals = ItemTemplateWords.Join(item.Plurals);
        Components = ItemTemplateWords.Pretty(item.ComponentsJson);
        Behaviors = item.Behaviors.Select(b => new BehaviorEditRow
        {
            Hook = b.Hook, Module = b.Module, Type = b.Type, Priority = b.Priority, Config = ItemTemplateWords.Pretty(b.ConfigJson),
        }).ToList();
        Version = item.Version;
    }

    public async Task<ItemTemplateEditOutcome> SaveAsync(CancellationToken cancellation = default)
    {
        Error = null;
        Notice = null;
        Problems.Clear();

        // JSON that does not parse is reported here and nothing is sent; everything else is core's to judge.
        var componentsProblem = ItemTemplateWords.CheckJsonObject(Components);
        if (componentsProblem != null) { Problems.Add("Component data: " + componentsProblem); }
        for (var i = 0; i < Behaviors.Count; i++)
        {
            var configProblem = ItemTemplateWords.CheckJsonObject(Behaviors[i].Config);
            if (configProblem != null) { Problems.Add("Behavior " + (i + 1) + " config: " + configProblem); }
        }
        if (Problems.Count > 0) { return ItemTemplateEditOutcome.Invalid; }

        var record = new ItemTemplateRecord
        {
            Id = FullId, Area = Area, Short = Short ?? string.Empty, Long = Long ?? string.Empty,
            ComponentsJson = (Components ?? string.Empty).Trim(),
        };
        record.Handles.AddRange(ItemTemplateWords.Split(Handles));
        record.Adjectives.AddRange(ItemTemplateWords.Split(Adjectives));
        record.Plurals.AddRange(ItemTemplateWords.Split(Plurals));
        foreach (var b in Behaviors)
        {
            record.Behaviors.Add(new RoomBehavior
            {
                Hook = (b.Hook ?? string.Empty).Trim(), Module = (b.Module ?? string.Empty).Trim(), Type = (b.Type ?? string.Empty).Trim(),
                Priority = b.Priority, ConfigJson = (b.Config ?? string.Empty).Trim(),
            });
        }

        try
        {
            var reply = IsNew ? await _core.CreateItemTemplateAsync(_token, record, cancellation) : await _core.UpdateItemTemplateAsync(_token, record, cancellation);
            switch (reply.Result)
            {
                case AdminResult.Ok:
                    Notice = IsNew ? "Item template created." : "Item template saved.";
                    if (!IsNew && reply.Template != null) { Fill(reply.Template); }
                    return ItemTemplateEditOutcome.Ok;
                case AdminResult.NoChange:
                    Notice = "Nothing was changed.";
                    return ItemTemplateEditOutcome.NoChange;
                case AdminResult.Invalid:
                    foreach (var e in reply.Errors) { Problems.Add(string.IsNullOrEmpty(e.Field) ? e.Message : e.Field + ": " + e.Message); }
                    if (Problems.Count == 0) { Error = ItemTemplateListPresenter.FailedError; }
                    return ItemTemplateEditOutcome.Invalid;
                case AdminResult.AlreadyExists: Error = ExistsError; return ItemTemplateEditOutcome.Failed;
                case AdminResult.NotFound: return Gone(ItemTemplateMissing.NotFound, GoneText);
                case AdminResult.Refused: Error = string.IsNullOrWhiteSpace(reply.Message) ? ItemTemplateListPresenter.RefusedError : reply.Message; return ItemTemplateEditOutcome.Failed;
                case AdminResult.AuditFailed: Error = ItemTemplateListPresenter.AuditFailedError; return ItemTemplateEditOutcome.Failed;
                case AdminResult.Forbidden: Error = ItemTemplateListPresenter.ForbiddenError; return ItemTemplateEditOutcome.Failed;
                default: Error = ItemTemplateListPresenter.FailedError; return ItemTemplateEditOutcome.Failed;
            }
        }
        catch (CoreUnavailableException) { Error = ItemTemplateListPresenter.UnavailableError; return ItemTemplateEditOutcome.Failed; }
        catch (CoreRefusedException) { Error = ItemTemplateListPresenter.ForbiddenError; return ItemTemplateEditOutcome.Failed; }
        catch (Exception) { Error = ItemTemplateListPresenter.FailedError; return ItemTemplateEditOutcome.Failed; }
    }

    private ItemTemplateEditOutcome Gone(ItemTemplateMissing why, string error)
    {
        Missing = why;
        Error = error;
        return ItemTemplateEditOutcome.Missing;
    }
}

// Small text helpers of the item template pages.
public static class ItemTemplateWords
{
    public static List<string> Split(string? text) => TerrainLibrary.Split(text);

    public static string Join(IEnumerable<string> items) => TerrainLibrary.Join(items);

    // Null for empty text or a JSON object; otherwise a fixed sentence.
    public static string? CheckJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? null : "must be a JSON object, such as {\"key\": \"value\"}.";
        }
        catch (JsonException) { return "is not valid JSON."; }
    }

    // Indented JSON for editing; "{}" and empty text become empty; text that is not JSON is shown as it is.
    public static string Pretty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) { return string.Empty; }
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && !doc.RootElement.EnumerateObject().Any()) { return string.Empty; }
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException) { return json; }
    }
}
