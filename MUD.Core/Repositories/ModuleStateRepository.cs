using MongoDB.Driver;
using MUD.Core.Modules;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;

namespace MUD.Core.Repositories
{
    public class ModuleStateRepository : Repository<ModuleStateDocument>, IModuleStateRepository
    {
        public ModuleStateRepository(IMongoClient dbClient, WorldSettings settings) : base(dbClient, settings.DatabaseName, ModuleStateDocument.CollectionName) { }
    }
}
