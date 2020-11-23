using MongoDB.Driver;
using MUD.Core.Entities;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;

namespace MUD.Core.Repositories
{
    public class NPCRepository : Repository<NPCEntity>, INPCRepository
    {
        public NPCRepository(IMongoClient dbClient) : base(dbClient, "testmud", "npc") { }
    }
}