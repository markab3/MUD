using System;
using System.Collections.Generic;
using System.Linq;
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

        // Only the first entry is consulted, as the search command has always consulted only the first search
        // property of a room. Returns true when the searcher found the reward.
        public bool TrySearch(Living searcher, string searchTarget, DateTime now)
        {
            SearchEntry entry = Entries.FirstOrDefault();
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
        public InventoryItem Reward { get; set; }

        // Runtime state: the time before which no reward is found. Absent until the first reward.
        [BsonElement("nextRewardAt")]
        [BsonIgnoreIfNull]
        [BsonDateTimeOptions(Kind = DateTimeKind.Local)]
        public DateTime? NextRewardAt { get; set; }

        public bool TrySearch(Living searcher, string searchTarget, DateTime now)
        {
            if (Target == null || searchTarget == null || Reward == null) { return false; }
            if (searchTarget.ToLower().Trim() != Target.ToLower().Trim()) { return false; }
            if (NextRewardAt.HasValue && now < NextRewardAt.Value) { return false; }

            searcher.ReceiveMessage(string.Format("You search the {0} and find {1} {2}!", Target, Reward.ShortDescription.GetArticle(), Reward.ShortDescription));
            searcher.Inventory.Add(Reward.Clone<InventoryItem>());
            NextRewardAt = now.AddMilliseconds(RefreshMilliseconds);
            return true;
        }

        public SearchEntry Clone()
        {
            return new SearchEntry
            {
                EntryId = EntryId,
                Target = Target,
                RefreshMilliseconds = RefreshMilliseconds,
                Reward = Reward == null ? null : Reward.Clone<InventoryItem>(),
                NextRewardAt = NextRewardAt
            };
        }
    }
}
