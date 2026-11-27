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

        // The item template this item was copied from at import time (specs/ITEM_TEMPLATES.md "References"). Null when not copied from
        // a template. Gameplay never reads these; they let a creator see which copies are stale.
        [BsonElement("templateId")]
        [BsonIgnoreIfNull]
        public string TemplateId { get; set; }

        // The version of that template when it was copied.
        [BsonElement("templateVersion")]
        [BsonIgnoreIfNull]
        public int? TemplateVersion { get; set; }

    }
}