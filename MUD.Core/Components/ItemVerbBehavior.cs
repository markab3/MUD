using System;
using MUD.Core.Events;
using MUD.Core.GameObjects;

namespace MUD.Core.Components
{
    // Shared shape of an item behavior that gives its holder a typed verb (owner decision D63: lines go to behaviors
    // first). The entry is a command.entered entry on an item. It takes the line only when the item is in the typing
    // player's inventory, the line starts with one of the item's verbs, and the rest of the line names the item (or is
    // empty). Anything else is answered "not handled" so the next behavior, then the built-in commands, get the line.
    public abstract class ItemVerbBehavior : IEventHandler, IBeforeHandler
    {
        // After notifications do not apply: this behavior only answers command.entered.
        public void Handle(EventDelivery delivery) { }

        // The verbs the item answers to, lowercase.
        public abstract string[] Verbs { get; }

        // Called when the line is for this item. Returns the verdict to give.
        protected abstract Verdict Act(Living actor, InventoryItem item, string noun);

        public void Begin(BeforeDelivery delivery)
        {
            delivery.Reply(Answer(delivery));
        }

        private Verdict Answer(BeforeDelivery delivery)
        {
            var item = delivery.Party.Entity as InventoryItem;
            if (item == null || !string.Equals(delivery.Hook, BehaviorHooks.CommandEntered, StringComparison.Ordinal)) { return Verdict.Allow(); }

            Living actor = null;
            foreach (EventParty party in delivery.Event.Parties)
            {
                if (party.Role == "actor") { actor = party.Entity as Living; break; }
            }
            if (actor == null || actor.Inventory == null || !actor.Inventory.Contains(item)) { return Verdict.Allow(); }

            object lineValue = null;
            string line = delivery.Event.Payload != null && delivery.Event.Payload.TryGetValue("line", out lineValue) ? lineValue as string : null;
            if (string.IsNullOrWhiteSpace(line)) { return Verdict.Allow(); }

            line = line.Trim();
            int space = line.IndexOf(' ');
            string verb = space < 0 ? line : line.Substring(0, space);
            string noun = space < 0 ? string.Empty : line.Substring(space + 1).Trim();
            if (Array.FindIndex(Verbs, v => string.Equals(v, verb, StringComparison.OrdinalIgnoreCase)) < 0) { return Verdict.Allow(); }
            if (noun.Length > 0 && !item.IsMatchForString(noun)) { return Verdict.Allow(); }

            return Act(actor, item, noun);
        }

        // The component's integer field, or the fallback when the component or field is missing or not a number.
        protected static int ReadInt(InventoryItem item, string component, string field, int fallback = 0)
        {
            MongoDB.Bson.BsonDocument data;
            if (!item.TryGetComponent(component, out data)) { return fallback; }
            MongoDB.Bson.BsonValue value = data.GetValue(field, MongoDB.Bson.BsonNull.Value);
            return value.IsNumeric ? value.ToInt32() : fallback;
        }
    }
}
