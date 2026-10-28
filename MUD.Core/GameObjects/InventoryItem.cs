using System.Collections.Generic;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MUD.Core.Components;

namespace MUD.Core.GameObjects
{
    public class InventoryItem : GameObject, IBehaviorHolder
    {
        public InventoryItem() {}

        // Behavior entries (owner decisions D5, D53): stored as the embedded array "behaviors". Not dispatched yet.
        [BsonElement("behaviors")]
        public List<BehaviorRef> Behaviors
        {
            // Never null, including for a stored document that has no behaviors element.
            get { return _behaviors ?? (_behaviors = new List<BehaviorRef>()); }
            set { _behaviors = value; }
        }

        private List<BehaviorRef> _behaviors;

    }
}