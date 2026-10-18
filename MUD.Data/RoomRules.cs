using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MUD.Data
{
    // The room validation rules shared by the area importer (MUD.Core) and the website (MUD.Web), so both apply the
    // same verdicts (specs/ROOM_FILES.md sections 3 and 11, specs/WEB_EDITING.md section 10). Pure functions only:
    // no I/O, no dependency on game types. Each Check* returns null when the value is acceptable, otherwise a
    // short message that does not repeat the value.
    public static class RoomRules
    {
        public const int MaxShort = 100;
        public const int MaxLong = 4000;
        public const int MaxDirection = 32;
        public const int MaxLocalKey = 64;
        public const int MaxKey = 128;

        public static readonly Regex LocalKeyPattern = new Regex("^[a-z0-9_]+$");
        public static readonly Regex PrefixPattern = new Regex("^[a-z0-9_]+(\\.[a-z0-9_]+)*$");
        public static readonly Regex DirectionPattern = new Regex("^[A-Za-z][A-Za-z ]*$");
        private static readonly Regex ObjectIdPattern = new Regex("^[0-9a-f]{24}$");

        // Section 11: trim trailing whitespace per line and overall, LF line endings.
        public static string NormalizeText(string value)
        {
            var lines = value.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').Select(l => l.TrimEnd());
            return string.Join("\n", lines).Trim();
        }

        // Authored keys must not look like generated ObjectIds.
        public static bool LooksLikeObjectId(string key)
        {
            return key != null && ObjectIdPattern.IsMatch(key);
        }

        public static string CheckLocalKey(string key)
        {
            if (string.IsNullOrEmpty(key)) { return "key is required."; }
            if (!LocalKeyPattern.IsMatch(key)) { return "key must match ^[a-z0-9_]+$."; }
            if (key.Length > MaxLocalKey) { return "key must be at most " + MaxLocalKey + " characters."; }
            return null;
        }

        public static string CheckAreaPrefix(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) { return "area prefix is required."; }
            if (!PrefixPattern.IsMatch(prefix)) { return "area prefix must be lowercase letters, digits and '_', segments joined by '.'."; }
            if (prefix.Length > MaxKey) { return "area prefix must be at most " + MaxKey + " characters."; }
            return null;
        }

        // An exit target (a full room key). Existence is checked by the caller against the store.
        public static string CheckRoomKeyReference(string key)
        {
            if (string.IsNullOrEmpty(key)) { return "target room key is required."; }
            if (!PrefixPattern.IsMatch(key)) { return "target must be a room key such as area.room."; }
            if (key.Length > MaxKey) { return "target must be at most " + MaxKey + " characters."; }
            return null;
        }

        // Takes already normalized text.
        public static string CheckShort(string value)
        {
            if (string.IsNullOrEmpty(value)) { return "short description is required."; }
            if (value.Length > MaxShort) { return "short description must be at most " + MaxShort + " characters."; }
            if (value.Any(char.IsControl)) { return "short description must be a single line without control characters."; }
            return null;
        }

        // Takes already normalized text. Line feeds and tabs are allowed.
        public static string CheckLong(string value)
        {
            if (string.IsNullOrEmpty(value)) { return "long description is required."; }
            if (value.Length > MaxLong) { return "long description must be at most " + MaxLong + " characters."; }
            if (value.Any(c => char.IsControl(c) && c != '\n' && c != '\t')) { return "long description must not contain control characters."; }
            return null;
        }

        public static string CheckDirection(string direction)
        {
            if (string.IsNullOrEmpty(direction)) { return "exit direction is required."; }
            if (direction.Length > MaxDirection) { return "exit direction must be at most " + MaxDirection + " characters."; }
            if (!DirectionPattern.IsMatch(direction)) { return "exit direction may only contain letters and spaces."; }
            return null;
        }

        // Directions that occur more than once in one room, compared case-insensitively (lowercased in the result).
        public static List<string> DuplicateDirections(IEnumerable<string> directions)
        {
            return directions.GroupBy(d => (d ?? "").ToLowerInvariant()).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        }
    }
}
