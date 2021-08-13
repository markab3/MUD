using MUD.Core.GameObjects;

namespace MUD.Core.Statuses
{
    public class AdminStatus : IStatus
    {
        public string StatusName { get => "admin"; }

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