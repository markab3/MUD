using MongoDB.Driver;
using MUD.Core.Providers.Interfaces;
using MUD.Data;

namespace MUD.Core.Providers {
    public class PlayerProvider : EntityProvider<Player>, IPlayerProvider
    {
        public PlayerProvider(IMongoClient dbClient) : base(dbClient, "testmud", "room") { }
    }
}