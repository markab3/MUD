using MongoDB.Driver;
using MUD.Core.GameObjects;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;

namespace MUD.Core.Repositories {
    public class RoomRepository : Repository<Room>, IRoomRepository
    {
        public RoomRepository(IMongoClient dbClient) : base(dbClient, "testmud", "room") { }

        // Copy authored definition fields onto the live room so players inside it keep their
        // room object; Items and Occupants (state) are left alone.
        protected override void RefreshCached(Room cached, Room fresh)
        {
            cached.ApplyDefinition(fresh);
        }
    }
}