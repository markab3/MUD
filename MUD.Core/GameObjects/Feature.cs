using MongoDB.Driver;

namespace MUD.Core
{
    /// A feature of a room. Perhaps we can have a library of these and then build rooms from them?
    public class Feature : GameObject
    {
        public Feature(IMongoDatabase db) : base(db) { }
        
        protected override string getCollection() { return "feature"; }
    }
}