using MUD.Core.Commands;
using MUD.Core.Repositories.Interfaces;

namespace MUD.Core.GameObjects
{
    public class Admin : Creator
    {
        public Admin(World world, CommandQueue commandQueue, IPlayerRepository playerRepository) : base(world, commandQueue, playerRepository)
        {
        }
    }
}