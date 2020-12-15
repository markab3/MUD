namespace MUD.Core.Providers {
    public class RoomProvider : EntityProvider<Room>, IEntityProvider<Room>
    {
        public RoomProvider(IMongoClient dbClient) : base(dbClient, "testmud", "room") { }
    }
}