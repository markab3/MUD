
using MongoDB.Driver;

namespace MUD.Core
{
    public class Food : InventoryItem
    {
        public int Servings { get; set; }

        public Food(IMongoDatabase db) : base(db) { }

        public override string Examine()
        {
            return base.Examine() + string.Format("\nIt currently has {0} servings left.", Servings);
        }

        public void Consume()
        {
            if (Servings > 0)
            {
                // Yum!
                Servings--;
            }
            else
            {
                // All gone!
            }
        }
    }
}