using System;
using MUD.Core.GameObjects;

namespace MUD.Core.Statuses
{
    public class BleedingStatus : IStatus
    {
        public string StatusName { get => "bleeding"; }

        private int _duration = 0;

        public void DoHeartbeat(Living affectedLiving)
        {
            if (affectedLiving is Player)
            {
                ((Player)affectedLiving).ReceiveMessage("You continue to bleed from your wounds.");
            }

            _duration--;
            if (_duration <= 0) { RemoveStatus(affectedLiving); }
        }

        public void InitializeStatus(Living affectedLiving)
        {
            if (affectedLiving is Player)
            {
                ((Player)affectedLiving).ReceiveMessage("Blood begins to flow from your wounds.");
            }

            if (_duration == 0)
            {
                _duration = (new Random()).Next(100);
            }
            return;
        }

        public void RemoveStatus(Living affectedLiving)
        {
            if (affectedLiving is Player)
            {
                ((Player)affectedLiving).ReceiveMessage("The flow of blood slows to a trickle and then stops.");
            }

            return;
        }
    }
}