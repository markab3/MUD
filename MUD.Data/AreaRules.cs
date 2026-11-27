using System.Linq;
using System.Text;

namespace MUD.Data
{
    // Rules of an area record (specs/ADMIN_API.md "Areas"). The id is the area prefix and follows RoomRules.CheckAreaPrefix.
    // Name and description are one line of free text. All Check methods take already normalized text (RoomRules.NormalizeText).
    public static class AreaRules
    {
        public const int MaxName = 100;
        public const int MaxDescription = 200;

        public static string CheckName(string value)
        {
            if (string.IsNullOrEmpty(value)) { return "name is required."; }
            if (value.Length > MaxName) { return "name must be at most " + MaxName + " characters."; }
            if (value.Any(char.IsControl)) { return "name must be a single line without control characters."; }
            return null;
        }

        // Optional: empty is fine.
        public static string CheckDescription(string value)
        {
            if (string.IsNullOrEmpty(value)) { return null; }
            if (value.Length > MaxDescription) { return "description must be at most " + MaxDescription + " characters."; }
            if (value.Any(char.IsControl)) { return "description must be a single line without control characters."; }
            return null;
        }

        // 'sample_area' -> 'Sample area'; 'a.b_c' -> 'A b c'. Used to name the records the startup backfill creates.
        public static string ReadableName(string prefix)
        {
            var words = (prefix ?? string.Empty).Split(new[] { '_', '.' }, System.StringSplitOptions.RemoveEmptyEntries);
            string text = string.Join(" ", words);
            if (text.Length == 0) { return string.Empty; }
            text = char.ToUpperInvariant(text[0]) + text.Substring(1);
            return text.Length > MaxName ? text.Substring(0, MaxName) : text;
        }
    }
}
