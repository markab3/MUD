
using MongoDB.Driver;

namespace MUD.Main
{
    public class Food : Item
    {
        public int Servings { get; set; }

        public Food(IMongoClient dbClient) : base(dbClient) { }

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