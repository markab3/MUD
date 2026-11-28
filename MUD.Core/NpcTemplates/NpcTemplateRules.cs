using System.Collections.Generic;
using MUD.Core.ItemTemplates;

namespace MUD.Core.NpcTemplates
{
    // Field rules of an NPC template (specs/NPC_TEMPLATES.md). The id and word-list rules are the item template ones (same id grammar).
    // The stat bounds are the ones Living keeps: vitals 0-1000, ability scores 0-255, experience 0 or more.
    public static class NpcTemplateRules
    {
        public const int MaxVital = 1000;
        public const int MaxAbility = 255;
        public const int DefaultVital = 1000;
        public const int DefaultAbility = 10;
        public const long DefaultExperience = 0;

        public static string CheckId(string id, string area) { return ItemTemplateRules.CheckId(id, area); }

        public static string CheckWords(string label, IList<string> words) { return ItemTemplateRules.CheckWords(label, words); }

        // A stat that is not set (null) is fine. Returns a fixed sentence or null.
        public static string CheckVital(string label, int? value)
        {
            return value.HasValue && (value.Value < 0 || value.Value > MaxVital) ? label + " must be between 0 and " + MaxVital + "." : null;
        }

        public static string CheckAbility(string label, int? value)
        {
            return value.HasValue && (value.Value < 0 || value.Value > MaxAbility) ? label + " must be between 0 and " + MaxAbility + "." : null;
        }

        public static string CheckExperience(long? value)
        {
            return value.HasValue && value.Value < 0 ? "Experience must be 0 or more." : null;
        }
    }
}
