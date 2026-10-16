using MUD.Core.GameObjects;
using MUD.Data;

namespace MUD.Core.Repositories.Interfaces
{
    public interface IPlayerRepository : IRepository<Player>
    {
        // Idempotently creates the unique index on Player.NameKey. Throws if the server refuses it.
        void CreateNameKeyIndex();
    }
}