using MongoDB.Driver;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;

namespace MUD.Core.Repositories {
    public class PlayerRepository : Repository<Player>, IPlayerRepository
    {
        public PlayerRepository(IMongoClient dbClient) : base(dbClient, "testmud", "player") { }
    }
}