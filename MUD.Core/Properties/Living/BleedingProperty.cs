using System;
using MUD.Core.GameObjects;

namespace MUD.Core.Properties
{
    public class BleedingProperty : ExtendedProperty, IHeartbeat
    {
        public int Duration { get; set; } = 0;

        public void DoHeartbeat()
        {
            Living affectedLiving = (Living)objectContext;
            affectedLiving.ReceiveMessage("You continue to bleed from your wounds.");

            Duration--;
            if (Duration <= 0) { RemoveProperty(); }
        }

        public override void InitializeProperty()
        {
            Living affectedLiving = (Living)objectContext;
            affectedLiving.ReceiveMessage("Blood begins to flow from your wounds.");

            if (Duration == 0)
            {
                Duration = (new Random()).Next(100);
            }
            return;
        }

        public override void RemoveProperty()
        {
            Living affectedLiving = (Living)objectContext;
            affectedLiving.ReceiveMessage("The flow of blood slows to a trickle and then stops.");

            return;
        }
    }
}