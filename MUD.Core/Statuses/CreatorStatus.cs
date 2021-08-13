using MUD.Core.GameObjects;

namespace MUD.Core.Statuses
{
    public class CreatorStatus : IStatus
    {
        public string StatusName { get => "creator"; }

        public void DoHeartbeat(Living affectedLiving)
        {
            return;
        }

        public void InitializeStatus(Living affectedLiving)
        {
            return;
        }

        public void RemoveStatus(Living affectedLiving)
        {
            return;
        }
    }
}