using MUD.Core.GameObjects;

namespace MUD.Core.Statuses
{
    public interface IStatus
    {
        string StatusName { get; }

        void InitializeStatus(Living affectedLiving);

        void DoHeartbeat(Living affectedLiving);

        void RemoveStatus(Living affectedLiving);
    }
}