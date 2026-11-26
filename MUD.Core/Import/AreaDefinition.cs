using System.Collections.Generic;

namespace MUD.Core.Import
{
    // Parsed, defaults-resolved form of an area file (specs/ROOM_FILES.md section 4).
    public class AreaDefinition
    {
        public string Prefix { get; set; }

        public string Name { get; set; }

        public List<RoomDefinition> Rooms { get; set; } = new List<RoomDefinition>();
    }

    public class RoomDefinition
    {
        public string LocalKey { get; set; }

        public string FullKey { get; set; }

        public string Short { get; set; }

        public string Long { get; set; }

        public List<string> Handles { get; set; } = new List<string>();

        public List<string> Adjectives { get; set; } = new List<string>();

        public List<string> Plurals { get; set; } = new List<string>();

        public int? X { get; set; }

        public int? Y { get; set; }

        public int? Z { get; set; }

        public List<ExitDefinition> Exits { get; set; } = new List<ExitDefinition>();

        public List<FeatureDefinition> Features { get; set; } = new List<FeatureDefinition>();

        public List<SearchPropertyDefinition> Properties { get; set; } = new List<SearchPropertyDefinition>();

        // Behavior entries from the 'behaviors:' list (owner decisions D5, D53). Features carry none (D14).
        public List<MUD.Core.Components.BehaviorRef> Behaviors { get; set; } = new List<MUD.Core.Components.BehaviorRef>();

        // Generator provenance from the room's 'x-gen' key (specs/TERRAIN_GENERATORS.md section 5.1). Null for authored rooms.
        public GenInfo Gen { get; set; }
    }

    public class GenInfo
    {
        public string Map { get; set; }

        public string Terrain { get; set; }

        public string Seed { get; set; }

        public int? LibVersion { get; set; }
    }

    public class ExitDefinition
    {
        public string Dir { get; set; }

        // Resolved full room key.
        public string To { get; set; }

        // Reverse exit name; consumed by reverse expansion, null afterwards.
        public string Reverse { get; set; }
    }

    public class FeatureDefinition
    {
        public string Short { get; set; }

        public string Long { get; set; }

        // Text appended to the room description. Null when the feature adds nothing.
        public string RoomLine { get; set; }

        // Text shown for 'look <noun>'. Null means fall back to Long.
        public string Detail { get; set; }

        public List<string> Handles { get; set; } = new List<string>();

        public List<string> Adjectives { get; set; } = new List<string>();

        public List<string> Plurals { get; set; } = new List<string>();

        // From the feature's 'x-gen' key: the library template this feature was generated from (section 5.2). Null when none.
        public string TemplateId { get; set; }

        public int? TemplateVersion { get; set; }
    }

    // The only room-file behavior today: "search" -> an entry of the room's search component.
    public class SearchPropertyDefinition
    {
        public string Id { get; set; }

        public string Target { get; set; }

        public int RefreshMs { get; set; }

        public ItemDefinition Reward { get; set; }

        // Optional: the entry grants a per-player flag instead of, or as well as, a reward.
        public DiscoveryDefinition Discovery { get; set; }
    }

    public class DiscoveryDefinition
    {
        public string Id { get; set; }

        public string Found { get; set; }

        public string Again { get; set; }
    }

    public class ItemDefinition
    {
        public string Short { get; set; }

        public string Long { get; set; }

        public List<string> Handles { get; set; } = new List<string>();

        public List<string> Adjectives { get; set; } = new List<string>();

        public List<string> Plurals { get; set; } = new List<string>();
    }
}
