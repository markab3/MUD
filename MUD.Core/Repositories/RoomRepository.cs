using MongoDB.Driver;
using MUD.Core.Entities;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;

namespace MUD.Core.Repositories
{
    public class RoomRepository : Repository<RoomEntity>, IRoomRepository
    {
        public RoomRepository(IMongoClient dbClient) : base(dbClient, "testmud", "room") { }
    }
}