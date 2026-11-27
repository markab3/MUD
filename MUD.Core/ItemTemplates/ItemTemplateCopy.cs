using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MUD.Core.Components;
using MUD.Core.GameObjects;

namespace MUD.Core.ItemTemplates
{
    // Copies the fields of an item template into an item (specs/ITEM_TEMPLATES.md "References"). Used by the importer (reward) and the
    // clone command. The copy is independent of the template afterwards.
    public static class ItemTemplateCopy
    {
        // The template's free component object is read in the GameObject shape { module: { name: { ... } } }. Entries that do not fit
        // that shape (not an object, or an object whose members are not objects) cannot be stored on an item and are skipped.
        public static Dictionary<string, Dictionary<string, BsonDocument>> ComponentsOf(ItemTemplateEntry template)
        {
            var result = new Dictionary<string, Dictionary<string, BsonDocument>>();
            if (template == null || template.Components == null) { return result; }
            foreach (BsonElement module in template.Components)
            {
                if (!module.Value.IsBsonDocument || !ComponentName.IsValidPart(module.Name)) { continue; }
                var named = new Dictionary<string, BsonDocument>();
                foreach (BsonElement part in module.Value.AsBsonDocument)
                {
                    if (part.Value.IsBsonDocument && ComponentName.IsValidPart(part.Name)) { named[part.Name] = part.Value.AsBsonDocument.DeepClone().AsBsonDocument; }
                }
                if (named.Count > 0) { result[module.Name] = named; }
            }
            return result;
        }

        public static List<BehaviorRef> BehaviorsOf(ItemTemplateEntry template)
        {
            return (template == null ? new List<BehaviorRef>() : template.Behaviors ?? new List<BehaviorRef>())
                .Where(b => b != null)
                .Select(b => b.Clone())
                .ToList();
        }

        // A new item (new id) holding a copy of everything the template defines.
        public static InventoryItem ToItem(ItemTemplateEntry template)
        {
            return new InventoryItem
            {
                ShortDescription = template.Short,
                LongDescription = template.Long,
                Handles = new List<string>(template.Handles ?? new List<string>()),
                Adjectives = new List<string>(template.Adjectives ?? new List<string>()),
                Plurals = new List<string>(template.Plurals ?? new List<string>()),
                Components = ComponentsOf(template),
                Behaviors = BehaviorsOf(template),
            };
        }
    }

    // A stored search reward that carries a template id (specs/ITEM_TEMPLATES.md "Referenced by").
    public sealed class ItemTemplateReference
    {
        public string RoomKey { get; set; }
        public string EntryId { get; set; }
        public string TemplateId { get; set; }
        public int TemplateVersion { get; set; }
    }

    public static class ItemTemplateReferences
    {
        // Scans stored room documents ("_id", "search.entries[].reward.templateId / templateVersion"). templateId null = every template.
        // Tolerant of missing or wrongly typed fields; a reward without a numeric version counts as version 0 (stale).
        public static List<ItemTemplateReference> Scan(IEnumerable<BsonDocument> rooms, string templateId)
        {
            var found = new List<ItemTemplateReference>();
            foreach (BsonDocument room in rooms ?? new List<BsonDocument>())
            {
                if (room == null || !room.TryGetValue("search", out BsonValue search) || !search.IsBsonDocument) { continue; }
                if (!search.AsBsonDocument.TryGetValue("entries", out BsonValue entries) || !entries.IsBsonArray) { continue; }
                string key = room.TryGetValue("_id", out BsonValue id) ? id.ToString() : string.Empty;
                foreach (BsonValue item in entries.AsBsonArray.Where(e => e.IsBsonDocument))
                {
                    BsonDocument entry = item.AsBsonDocument;
                    if (!entry.TryGetValue("reward", out BsonValue reward) || !reward.IsBsonDocument) { continue; }
                    BsonDocument r = reward.AsBsonDocument;
                    if (!r.TryGetValue("templateId", out BsonValue tid) || !tid.IsString) { continue; }
                    if (templateId != null && tid.AsString != templateId) { continue; }
                    found.Add(new ItemTemplateReference
                    {
                        RoomKey = key,
                        EntryId = entry.TryGetValue("id", out BsonValue eid) && eid.IsString ? eid.AsString : string.Empty,
                        TemplateId = tid.AsString,
                        TemplateVersion = r.TryGetValue("templateVersion", out BsonValue v) && v.IsNumeric ? v.ToInt32() : 0,
                    });
                }
            }
            return found.OrderBy(x => x.RoomKey, StringComparer.Ordinal).ThenBy(x => x.EntryId, StringComparer.Ordinal).ToList();
        }
    }
}
