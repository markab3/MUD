
namespace MUD.Core.GameObjects
{
    public class Wand : InventoryItem
    {
        public int CurrentCharges { get; set; }
        public int MaxCharges { get; set; }

        public Wand() { }

        public override string Examine()
        {
            return base.Examine() + string.Format("\nIt currently has {0} of {1} uses left.", CurrentCharges, MaxCharges);
        }

        public void UseWand()
        {
            if (CurrentCharges > 0)
            {
                // Zap!
                CurrentCharges--;
            }
            else
            {
                // Fizzle.
            }
        }
    }
}