using System;
using System.Collections.Generic;

namespace MUD.Core.Components
{
    // The hook identifier strings stored in the Hook field of a BehaviorRef (owner decision D54). They are stored on
    // entities, so they are not renamed without an owner decision. Nothing dispatches on them yet (phase 3).
    public static class BehaviorHooks
    {
        public const string Heartbeat = "heartbeat";
        public const string CommandEntered = "command.entered";
        public const string LivingExit = "living.exit";
        public const string LivingEnter = "living.enter";
        public const string ItemDrop = "item.drop";
        public const string ItemTake = "item.take";

        public static readonly IReadOnlyList<string> All = new[] { Heartbeat, CommandEntered, LivingExit, LivingEnter, ItemDrop, ItemTake };

        public static bool IsKnown(string hook)
        {
            if (hook == null) { return false; }
            foreach (string known in All)
            {
                if (string.Equals(known, hook, StringComparison.Ordinal)) { return true; }
            }
            return false;
        }
    }
}
