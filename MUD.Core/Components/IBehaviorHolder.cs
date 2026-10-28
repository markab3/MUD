using System.Collections.Generic;
using System.Linq;

namespace MUD.Core.Components
{
    // Implemented by the entities that can carry behavior entries: rooms, items, players and NPCs. Features do not
    // (owner decision D14). The list is stored on the entity document as "behaviors" (D53).
    public interface IBehaviorHolder
    {
        List<BehaviorRef> Behaviors { get; set; }
    }

    public static class BehaviorHolderExtensions
    {
        public static BehaviorRef FindBehavior(this IBehaviorHolder holder, string hook, string module, string type)
        {
            if (holder.Behaviors == null) { return null; }
            return holder.Behaviors.FirstOrDefault(b => b != null && b.HasSameBinding(hook, module, type));
        }

        // Adds a copy of the entry. Returns null on success, otherwise the reason it was not added: invalid, or an
        // entry with the same (hook, module, type) already exists (never silently replaced).
        public static string AddBehavior(this IBehaviorHolder holder, BehaviorRef entry)
        {
            string problem = entry.Validate();
            if (problem != null) { return problem; }
            if (holder.FindBehavior(entry.Hook, entry.Module, entry.Type) != null)
            {
                return "an entry for hook '" + entry.Hook + "', module '" + entry.Module + "', type '" + entry.Type + "' already exists; remove it first.";
            }
            if (holder.Behaviors == null) { holder.Behaviors = new List<BehaviorRef>(); }
            holder.Behaviors.Add(entry.Clone());
            return null;
        }

        public static bool RemoveBehavior(this IBehaviorHolder holder, string hook, string module, string type)
        {
            if (holder.Behaviors == null) { return false; }
            return holder.Behaviors.RemoveAll(b => b != null && b.HasSameBinding(hook, module, type)) > 0;
        }
    }
}
