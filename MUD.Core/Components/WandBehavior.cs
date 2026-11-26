using MongoDB.Bson;
using MUD.Core.Events;
using MUD.Core.GameObjects;

namespace MUD.Core.Components
{
    // A wand with charges as a behavior any item can carry (replaces the retired Wand subclass). The state is the
    // component "wand.charges" { current, max }; the verbs "zap" and "use" come from a command.entered entry (wand, Wand),
    // available only while the item is in the holder's inventory. A zap only uses a charge: it has no other effect yet.
    // A wand with no charges left stays in the inventory.
    public sealed class WandBehavior : ItemVerbBehavior
    {
        public const string Module = "wand";
        public const string Type = "Wand";
        public const string ComponentFullName = "wand.charges";
        public const string CurrentField = "current";
        public const string MaxField = "max";

        public override string[] Verbs { get { return new[] { "zap", "use" }; } }

        // Makes the item a wand with the given charges (replacing any earlier state). Current is held within 0..max.
        public static void Attach(InventoryItem item, int current, int max)
        {
            if (max < 0) { max = 0; }
            if (current < 0) { current = 0; }
            if (current > max) { current = max; }
            item.SetComponent(ComponentFullName, new BsonDocument { { CurrentField, current }, { MaxField, max } });
            if (!item.Behaviors.Exists(b => b.HasSameBinding(BehaviorHooks.CommandEntered, Module, Type)))
            {
                item.Behaviors.Add(new BehaviorRef(BehaviorHooks.CommandEntered, Module, Type));
            }
        }

        public static int Charges(InventoryItem item)
        {
            return ReadInt(item, ComponentFullName, CurrentField);
        }

        public static int MaxCharges(InventoryItem item)
        {
            return ReadInt(item, ComponentFullName, MaxField);
        }

        protected override Verdict Act(Living actor, InventoryItem item, string noun)
        {
            if (noun.Length == 0) { return Verdict.Handled("Use what? Syntax: zap <object>"); }

            string name = item.ShortDescription;
            int charges = Charges(item);
            if (charges <= 0) { return Verdict.Handled(string.Format("The {0} fizzles; it has no charges left.", name)); }

            item.SetComponent(ComponentFullName, new BsonDocument { { CurrentField, charges - 1 }, { MaxField, MaxCharges(item) } });
            return Verdict.Handled(string.Format("You zap the {0}. Nothing happens.", name));
        }
    }
}
