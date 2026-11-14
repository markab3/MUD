using MUD.Core.Events;
using MUD.Core.GameObjects;

namespace MUD.Core.Components
{
    // The code behind the heartbeat entry (status, Bleeding): the first real behavior resolved through the in-process
    // (Module, Type) dictionary. It acts on livings only; an entry on anything else does nothing.
    public sealed class BleedingBehavior : IEventHandler
    {
        public void Handle(EventDelivery delivery)
        {
            var living = delivery.Party.Entity as Living;
            if (living != null) { BleedingStatus.OnHeartbeat(living); }
        }
    }
}
