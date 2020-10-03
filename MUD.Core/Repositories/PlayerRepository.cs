using MongoDB.Driver;
using MUD.Core.Entities;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;

namespace MUD.Core.Repositories
{
    public class PlayerRepository : Repository<PlayerEntity>, IPlayerRepository
    {
        public PlayerRepository(IMongoClient dbClient) : base(dbClient, "testmud", "players") { }
    }
}