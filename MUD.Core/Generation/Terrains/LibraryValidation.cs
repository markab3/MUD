using System.Collections.Generic;
using System.Linq;

namespace MUD.Core.Generation.Terrains
{
    public sealed class LibraryError
    {
        public LibraryError(string field, string message) { Field = field; Message = message; }
        public string Field { get; private set; }
        public string Message { get; private set; }
        public override string ToString() { return Field + ": " + Message; }
    }

    // Field rules of specs/TERRAIN_GENERATORS.md section 6.1/6.2. Shared by the seed loader and the admin API.
    // Messages are fixed sentences (no entity text echoed).
    public static class LibraryValidation
    {
        public const int MaxTextLength = 2000;

        public static List<LibraryError> Validate(DescriptionFragment f)
        {
            var e = new List<LibraryError>();
            Id(e, f.Id);
            Terrains(e, "terrains", f.Terrains);
            if (string.IsNullOrWhiteSpace(f.Slot)) { e.Add(new LibraryError("slot", "The slot is required.")); }
            Required(e, "text", f.Text);
            if (f.Weight < 0) { e.Add(new LibraryError("weight", "The weight must be 0 or more.")); }
            Strings(e, "tags", f.Tags);
            return e;
        }

        public static List<LibraryError> Validate(FeatureTemplate f)
        {
            var e = new List<LibraryError>();
            Id(e, f.Id);
            Terrains(e, "terrains", f.Terrains);
            Required(e, "short", f.Short);
            Required(e, "long", f.Long);
            Strings(e, "handles", f.Handles);
            Strings(e, "adjectives", f.Adjectives);
            Strings(e, "tags", f.Tags);
            if (f.Weight < 0) { e.Add(new LibraryError("weight", "The weight must be 0 or more.")); }
            if (double.IsNaN(f.Chance) || f.Chance < 0 || f.Chance > 1) { e.Add(new LibraryError("chance", "The chance must be between 0 and 1.")); }
            if (f.Version < 1) { e.Add(new LibraryError("version", "The version must be 1 or more.")); }
            return e;
        }

        public static List<LibraryError> Validate(NeighborPhrase p)
        {
            var e = new List<LibraryError>();
            Id(e, p.Id);
            Terrains(e, "terrains", p.Terrains);
            if (string.IsNullOrWhiteSpace(p.NeighborTerrain)) { e.Add(new LibraryError("neighborTerrain", "The neighbour terrain is required.")); }
            Required(e, "text", p.Text);
            if (!string.IsNullOrEmpty(p.Text) && CountDir(p.Text) != 1) { e.Add(new LibraryError("text", "The text must contain {dir} exactly once.")); }
            if (p.Weight < 0) { e.Add(new LibraryError("weight", "The weight must be 0 or more.")); }
            return e;
        }

        public static int CountDir(string text)
        {
            int count = 0, i = 0;
            while ((i = text.IndexOf("{dir}", i, System.StringComparison.Ordinal)) >= 0) { count++; i += 5; }
            return count;
        }

        private static void Id(List<LibraryError> e, string id)
        {
            if (string.IsNullOrWhiteSpace(id)) { e.Add(new LibraryError("id", "The id is required.")); return; }
            if (id.Length > 100 || id.Any(c => !(char.IsLetterOrDigit(c) && c < 128) && c != '-' && c != '_' && c != '.'))
            {
                e.Add(new LibraryError("id", "The id may only contain letters, digits, '-', '_' and '.', up to 100 characters."));
            }
        }

        private static void Required(List<LibraryError> e, string field, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) { e.Add(new LibraryError(field, "This field is required.")); }
            else if (value.Length > MaxTextLength) { e.Add(new LibraryError(field, "This field is too long.")); }
        }

        // Empty list is valid (OD5: not used by any generator). Entries must be non-empty strings.
        private static void Terrains(List<LibraryError> e, string field, List<string> terrains)
        {
            if (terrains == null) { return; }
            if (terrains.Any(t => string.IsNullOrWhiteSpace(t))) { e.Add(new LibraryError(field, "Every terrain id must be a non-empty string.")); }
            if (terrains.Distinct().Count() != terrains.Count) { e.Add(new LibraryError(field, "A terrain id is listed twice.")); }
        }

        private static void Strings(List<LibraryError> e, string field, List<string> items)
        {
            if (items == null) { return; }
            if (items.Any(t => string.IsNullOrWhiteSpace(t))) { e.Add(new LibraryError(field, "Entries must be non-empty strings.")); }
        }
    }
}
