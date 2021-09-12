using System;
using MUD.Core.Formatting;
using MUD.Core.GameObjects;
using MUD.Core.Properties.Interfaces;

namespace MUD.Core.Properties.RoomProperties
{
    public class BasicSearchProperty : ExtendedProperty, ISearchable
    {
        public string SearchTarget;

        public InventoryItem Reward;

        public int RefreshInterval;

        private DateTime _nextReward;

        public BasicSearchProperty()
        {
            _nextReward = DateTime.Now;
        }

        public bool DoSearch(Living searcher, string searchTarget)
        {
            if (searchTarget.ToLower().Trim() == SearchTarget.ToLower().Trim() && DateTime.Now >= _nextReward)
            {
                searcher.ReceiveMessage(String.Format("You search the {0} and find {1} {2}!", SearchTarget, Reward.ShortDescription.GetArticle(), Reward.ShortDescription));
                // TODO: Clone the object first...
                var newObj = Reward.Clone<InventoryItem>();
                searcher.Inventory.Add(newObj);
                _nextReward = DateTime.Now.AddMilliseconds(RefreshInterval);
                return true;
            }
            else
            {
                return false;
            }
        }

        public override void InitializeProperty() { }

        public override void RemoveProperty() { }
    }
}