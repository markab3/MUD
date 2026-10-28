using System;
using MongoDB.Bson;
using MUD.Core.GameObjects;

namespace MUD.Core.Components
{
    // The bleeding status of a living (replaces the retired BleedingProperty). It is a component "status.bleeding"
    // holding the remaining duration in heartbeats, plus a heartbeat behavior entry (status, Bleeding) that the
    // in-process registry resolves to OnHeartbeat. Both are stored with the living, so bleeding survives a restart.
    public static class BleedingStatus
    {
        public const string Module = "status";
        public const string Type = "Bleeding";
        public const string ComponentFullName = "status.bleeding";
        public const string DurationField = "duration";

        private static readonly Random Generator = new Random();

        public static bool IsBleeding(Living living)
        {
            return living.HasComponent(ComponentFullName);
        }

        // Starts bleeding for the given number of heartbeats; a duration of zero or less picks a random one,
        // as the old property did. Bleeding that has already started is not restarted: returns false then.
        public static bool Apply(Living living, int duration = 0)
        {
            if (IsBleeding(living)) { return false; }
            if (duration <= 0)
            {
                lock (Generator) { duration = Generator.Next(100) + 1; }
            }

            living.ReceiveMessage("Blood begins to flow from your wounds.");
            living.SetComponent(ComponentFullName, new BsonDocument(DurationField, duration));
            living.AddBehavior(new BehaviorRef(BehaviorHooks.Heartbeat, Module, Type));
            return true;
        }

        public static int RemainingDuration(Living living)
        {
            if (!living.TryGetComponent(ComponentFullName, out BsonDocument data)) { return 0; }
            BsonValue value = data.GetValue(DurationField, BsonNull.Value);
            return value.IsNumeric ? value.ToInt32() : 0;
        }

        public static void OnHeartbeat(Living living)
        {
            if (!IsBleeding(living))
            {
                // The component is gone but the entry remains: tidy up quietly.
                living.RemoveBehavior(BehaviorHooks.Heartbeat, Module, Type);
                return;
            }

            living.ReceiveMessage("You continue to bleed from your wounds.");
            int remaining = RemainingDuration(living) - 1;
            if (remaining <= 0)
            {
                living.RemoveComponent(ComponentFullName);
                living.RemoveBehavior(BehaviorHooks.Heartbeat, Module, Type);
                living.ReceiveMessage("The flow of blood slows to a trickle and then stops.");
            }
            else
            {
                living.SetComponent(ComponentFullName, new BsonDocument(DurationField, remaining));
            }
        }
    }
}
