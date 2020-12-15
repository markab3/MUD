using MongoDB.Driver;
using MUD.Core.Providers.Interfaces;
using MUD.Data;

namespace MUD.Core.Providers {
    public class RoomProvider : EntityProvider<Room>, IRoomProvider
    {
        public RoomProvider(IMongoClient dbClient) : base(dbClient, "testmud", "room") { }
    }
}