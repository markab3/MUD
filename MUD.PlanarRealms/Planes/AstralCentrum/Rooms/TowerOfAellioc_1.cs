using MongoDB.Driver;
using MUD.Core;
using MUD.Core.Providers.Interfaces;

namespace MUD.PlanarRealms.Planes.Neutral.Rooms
{
    public class TowerOfAellioc_1 : Room {
        public TowerOfAellioc_1 (IMongoDatabase db, IRoomProvider roomProvider) : base(db, roomProvider) {}
    }
}