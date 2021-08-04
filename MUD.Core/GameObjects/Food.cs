namespace MUD.Core.GameObjects
{
    public class Food : InventoryItem
    {
        public int Servings { get; set; }

        public Food() { }

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