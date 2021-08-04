using MongoDB.Driver;
using MUD.Core.GameObjects;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;

namespace MUD.Core.Repositories {
    public class RoomRepository : Repository<Room>, IRoomRepository
    {
        public RoomRepository(IMongoClient dbClient) : base(dbClient, "testmud", "room") { }
    }
}