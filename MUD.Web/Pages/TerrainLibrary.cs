using MUD.Contracts;

namespace MUD.Web.Pages;

// Shared helpers of the terrain library pages (/creator/terrain and /creator/terrain/edit). An entity is "unassigned" when its terrains
// list is empty: it is used by no generator (never "any terrain"). The web cannot ask core for the registered terrain ids, so the known
// ids are the ones the library already uses; the edit form also takes free text for a new id.
public static class TerrainLibrary
{
    public const string Fragment = "fragment";
    public const string Feature = "feature";
    public const string Phrase = "phrase";
    public const string Unassigned = "unassigned";

    public const string UnavailableError = "The game is not reachable right now. Please try again later.";
    public const string ForbiddenError = "You are not allowed to do that.";
    public const string FailedError = "The library request could not be completed. Please try again later.";
    public const string RefusedError = "The terrain library is not available on the game right now.";
    public const string AuditFailedError = "The change was not saved because it could not be recorded. Please try again later.";
    public const string UnusedWarning = "Not used by any generator";

    public static bool IsKind(string? kind) => kind == Fragment || kind == Feature || kind == Phrase;

    public static string KindLabel(string kind) => kind switch
    {
        Fragment => "Description fragment",
        Feature => "Feature template",
        Phrase => "Neighbour phrase",
        _ => "Entity",
    };

    // Splits comma or space separated text, trimmed, no blanks, no repeats (first spelling wins).
    public static List<string> Split(string? text) =>
        (text ?? string.Empty).Split(new[] { ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToList();

    public static string Join(IEnumerable<string> items) => string.Join(", ", items);

    public static List<string> KnownTerrains(IEnumerable<FragmentRecord> f, IEnumerable<FeatureTemplateRecord> t, IEnumerable<NeighborPhraseRecord> p)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var x in f) { foreach (var id in x.Terrains) { set.Add(id); } }
        foreach (var x in t) { foreach (var id in x.Terrains) { set.Add(id); } }
        foreach (var x in p)
        {
            foreach (var id in x.Terrains) { set.Add(id); }
            if (!string.IsNullOrWhiteSpace(x.NeighborTerrain)) { set.Add(x.NeighborTerrain); }
        }
        return set.ToList();
    }

    // Sentence preview of a neighbour phrase with {dir} filled in.
    public static string PhrasePreview(string? text) => (text ?? string.Empty).Replace("{dir}", "north", StringComparison.Ordinal);

    public static string ErrorText(FieldError e) => string.IsNullOrEmpty(e.Field) ? e.Message : e.Field + ": " + e.Message;
}
