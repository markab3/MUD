using MUD.Core.Commands;
using MUD.Core.Repositories.Interfaces;

namespace MUD.Core.GameObjects
{
    public class Creator : Player
    {
        public string CurrentDirectory { get; set; }

        public Creator(World world, CommandQueue commandQueue, IPlayerRepository playerRepository) : base(world, commandQueue, playerRepository)
        {
        }
    }
}