using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MUD.Core.Formatting;
using MUD.Core.GameObjects;

namespace MUD.Core.Components
{
    // The core component "search" on a room (owner decisions D2, D13): what a player finds with the search command
    // and how long until the next reward. Search stays baked into core, so this is a typed core component and not
    // a behavior entry. The cooldown (NextRewardAt) is stored with the room, so it survives a restart.
    // Stored as the embedded document "search": { entries: [ { id, target, refreshMilliseconds, reward, nextRewardAt } ] }.
    [BsonIgnoreExtraElements]
    public class Search
    {
        public const string FieldName = "search";

        private List<SearchEntry> _entries;

        [BsonElement("entries")]
        public List<SearchEntry> Entries
        {
            // Never null, including for a stored document that has no entries element.
            get { return _entries ?? (_entries = new List<SearchEntry>()); }
            set { _entries = value; }
        }

        // All entries are consulted: the first whose target matches what was typed answers (so a room with one entry
        // behaves as before). Returns true when the searcher found something (a reward or a discovery).
        public bool TrySearch(Living searcher, string searchTarget, DateTime now)
        {
            if (searchTarget == null) { return false; }
            SearchEntry entry = Entries.FirstOrDefault(e => e != null && e.Matches(searchTarget));
            return entry != null && entry.TrySearch(searcher, searchTarget, now);
        }

        // Replaces the authored configuration with that of another copy of this component. The cooldown of an
        // entry with the same id is kept, because it is runtime state and not part of the definition.
        public void ApplyDefinition(Search source)
        {
            var previous = Entries;
            var replaced = new List<SearchEntry>();
            foreach (SearchEntry authored in source.Entries)
            {
                SearchEntry copy = authored.Clone();
                SearchEntry old = previous.FirstOrDefault(p => p.EntryId != null && p.EntryId == authored.EntryId);
                copy.NextRewardAt = old != null ? old.NextRewardAt : authored.NextRewardAt;
                replaced.Add(copy);
            }
            Entries = replaced;
        }
    }

    [BsonIgnoreExtraElements]
    public class SearchEntry
    {
        // Unique among the entries of a room; the area file uses it to match an entry on re-import.
        [BsonElement("id")]
        public string EntryId { get; set; }

        // What the player types after "search" (compared without case or surrounding spaces).
        [BsonElement("target")]
        public string Target { get; set; }

        // How long after a reward the next one can be found, in milliseconds.
        [BsonElement("refreshMilliseconds")]
        public int RefreshMilliseconds { get; set; }

        [BsonElement("reward")]
        [BsonIgnoreIfNull]
        public InventoryItem Reward { get; set; }

        // Optional: finding it flags the searcher (per player, no room cooldown). An entry has a reward, a discovery, or both.
        [BsonElement("discovery")]
        [BsonIgnoreIfNull]
        public SearchDiscovery Discovery { get; set; }

        // Runtime state: the time before which no reward is found. Absent until the first reward.
        [BsonElement("nextRewardAt")]
        [BsonIgnoreIfNull]
        [BsonDateTimeOptions(Kind = DateTimeKind.Local)]
        public DateTime? NextRewardAt { get; set; }

        public bool Matches(string searchTarget)
        {
            return Target != null && searchTarget != null && searchTarget.ToLower().Trim() == Target.ToLower().Trim();
        }

        public bool TrySearch(Living searcher, string searchTarget, DateTime now)
        {
            if (!Matches(searchTarget)) { return false; }
            bool found = false;

            if (Discovery != null && !string.IsNullOrEmpty(Discovery.FlagId))
            {
                if (!DiscoveryFlags.Has(searcher, Discovery.FlagId))
                {
                    DiscoveryFlags.Mark(searcher, Discovery.FlagId);
                    searcher.ReceiveMessage(!string.IsNullOrEmpty(Discovery.Found) ? Discovery.Found : string.Format("You search the {0} and find something!", Target));
                    // The flag is player state: saved with the player (a Living that is not a player keeps it in memory only).
                    var player = searcher as Player;
                    if (player != null) { player.Save(true); }
                    found = true;
                }
                else if (Reward == null)
                {
                    searcher.ReceiveMessage(!string.IsNullOrEmpty(Discovery.Again) ? Discovery.Again : string.Format("You search the {0} but find nothing new.", Target));
                    found = true;
                }
            }

            if (Reward != null && !(NextRewardAt.HasValue && now < NextRewardAt.Value))
            {
                searcher.ReceiveMessage(string.Format("You search the {0} and find {1} {2}!", Target, Reward.ShortDescription.GetArticle(), Reward.ShortDescription));
                searcher.Inventory.Add(Reward.Clone<InventoryItem>());
                NextRewardAt = now.AddMilliseconds(RefreshMilliseconds);
                found = true;
            }
            return found;
        }

        public SearchEntry Clone()
        {
            return new SearchEntry
            {
                EntryId = EntryId,
                Target = Target,
                RefreshMilliseconds = RefreshMilliseconds,
                Reward = Reward == null ? null : Reward.Clone<InventoryItem>(),
                Discovery = Discovery == null ? null : new SearchDiscovery { FlagId = Discovery.FlagId, Found = Discovery.Found, Again = Discovery.Again },
                NextRewardAt = NextRewardAt
            };
        }
    }

    // What a discovering search entry grants: a named per-player flag (see DiscoveryFlags) and the two messages.
    [BsonIgnoreExtraElements]
    public class SearchDiscovery
    {
        [BsonElement("id")]
        public string FlagId { get; set; }

        // Told to the searcher the first time.
        [BsonElement("found")]
        [BsonIgnoreIfNull]
        public string Found { get; set; }

        // Told to a searcher who has already found it.
        [BsonElement("again")]
        [BsonIgnoreIfNull]
        public string Again { get; set; }
    }

    // Per-player discovery flags: the player component "search.found" { ids: [ "<discovery id>" ] }. It is player state, saved with
    // the player (survives relog and restart) and never touched by an area re-import. A discovery id is global to the world.
    public static class DiscoveryFlags
    {
        public const string ComponentFullName = "search.found";
        public const string IdsField = "ids";

        public static bool Has(GameObject player, string id)
        {
            BsonDocument data;
            if (player == null || string.IsNullOrEmpty(id) || !player.TryGetComponent(ComponentFullName, out data)) { return false; }
            BsonValue ids = data.GetValue(IdsField, BsonNull.Value);
            if (!ids.IsBsonArray) { return false; }
            foreach (BsonValue value in ids.AsBsonArray)
            {
                if (value.IsString && value.AsString == id) { return true; }
            }
            return false;
        }

        // Records the flag on the player (the caller saves the player).
        public static void Mark(GameObject player, string id)
        {
            if (Has(player, id)) { return; }
            BsonDocument data;
            BsonArray ids = null;
            if (player.TryGetComponent(ComponentFullName, out data))
            {
                BsonValue existing = data.GetValue(IdsField, BsonNull.Value);
                if (existing.IsBsonArray) { ids = existing.AsBsonArray.DeepClone().AsBsonArray; }
            }
            if (ids == null) { ids = new BsonArray(); }
            ids.Add(id);
            player.SetComponent(ComponentFullName, new BsonDocument(IdsField, ids));
        }
    }
}
