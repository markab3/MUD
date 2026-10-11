using MongoDB.Driver;
using MUD.Core.GameObjects;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;

namespace MUD.Core.Repositories {
    public class PlayerRepository : Repository<Player>, IPlayerRepository
    {
        public PlayerRepository(IMongoClient dbClient, WorldSettings settings) : base(dbClient, settings.DatabaseName, "player") { }
    }
}