using MongoDB.Driver;

namespace MUD.Core
{
    public class InventoryItem : GameObject
    {
        public InventoryItem(IMongoDatabase db) : base(db) {
            _dbCollection = "Items";
         }        
    }
}