using MongoDB.Bson;
using MUD.Core.Events;
using MUD.Core.GameObjects;

namespace MUD.Core.Components
{
    // Food as a behavior any item can carry (replaces the retired Food subclass). The state is the component
    // "food.servings" { servings }; the verb comes from a command.entered entry (food, Food), so the holder can "eat"
    // only while the item is in their inventory. Both are stored with the item, so they survive a restart.
    public sealed class FoodBehavior : ItemVerbBehavior
    {
        public const string Module = "food";
        public const string Type = "Food";
        public const string ComponentFullName = "food.servings";
        public const string ServingsField = "servings";

        public override string[] Verbs { get { return new[] { "eat" }; } }

        // Makes the item edible with the given number of servings (replacing any earlier state).
        public static void Attach(InventoryItem item, int servings)
        {
            item.SetComponent(ComponentFullName, new BsonDocument(ServingsField, servings < 0 ? 0 : servings));
            if (!item.Behaviors.Exists(b => b.HasSameBinding(BehaviorHooks.CommandEntered, Module, Type)))
            {
                item.Behaviors.Add(new BehaviorRef(BehaviorHooks.CommandEntered, Module, Type));
            }
        }

        public static int Servings(InventoryItem item)
        {
            return ReadInt(item, ComponentFullName, ServingsField);
        }

        protected override Verdict Act(Living actor, InventoryItem item, string noun)
        {
            if (noun.Length == 0) { return Verdict.Handled("Eat what? Syntax: eat <object>"); }

            string name = item.ShortDescription;
            int servings = Servings(item);
            if (servings <= 0) { return Verdict.Handled(string.Format("There is nothing left of the {0}.", name)); }

            servings--;
            item.SetComponent(ComponentFullName, new BsonDocument(ServingsField, servings));
            actor.ReceiveMessage(string.Format("You eat some of the {0}.", name));
            actor.CurrentLocation?.TellRoom(string.Format("{0} eats some of the {1}.", actor.ShortDescription, name), new Living[] { actor });

            if (servings <= 0)
            {
                // The last serving: the item is gone, and the player is saved so it stays gone.
                actor.Inventory.Remove(item);
                actor.ReceiveMessage(string.Format("You finish the {0}.", name));
                (actor as Player)?.Save(true);
            }
            return Verdict.Handled();
        }
    }
}
