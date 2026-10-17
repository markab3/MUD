using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MUD.Data
{
    // One-off, idempotent migration of documents written before string keys: BSON ObjectId values in "_id"
    // (top-level and embedded, e.g. inventory items) and in the reference fields listed in ReferenceFields
    // are rewritten as their 24-char hex string. Documents already using strings are left alone, so it is
    // safe to run on every startup.
    public static class ObjectIdMigration
    {
        public static readonly string[] ReferenceFields = { "DestinationId", "CurrentLocationId" };

        // Returns the number of documents rewritten. Never deletes a document before its replacement is stored.
        public static int MigrateCollection(IMongoCollection<BsonDocument> collection, Action<string> log = null)
        {
            log = log ?? (_ => { });
            var migrated = 0;
            var ids = collection.Find(new BsonDocument()).Project(new BsonDocument("_id", 1)).ToList()
                .Select(d => d["_id"]).ToList();

            foreach (var id in ids)
            {
                var doc = collection.Find(new BsonDocument("_id", id)).FirstOrDefault();
                if (doc == null || !Convert(doc)) { continue; }

                if (id.IsObjectId)
                {
                    // _id is immutable: store the converted copy, then remove the original.
                    try
                    {
                        collection.InsertOne(doc);
                    }
                    catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
                    {
                        log(string.Format("Skipped {0}: a document with the string id already exists.", id));
                        continue;
                    }
                    collection.DeleteOne(new BsonDocument("_id", id));
                }
                else
                {
                    collection.ReplaceOne(new BsonDocument("_id", id), doc);
                }
                migrated++;
            }
            return migrated;
        }

        // Converts ObjectIds in place. Returns true when anything changed.
        public static bool Convert(BsonDocument doc)
        {
            var changed = false;
            foreach (var name in doc.Names.ToList())
            {
                var value = doc[name];
                if (value.IsObjectId && (name == "_id" || ReferenceFields.Contains(name)))
                {
                    doc[name] = value.AsObjectId.ToString();
                    changed = true;
                }
                else
                {
                    changed |= ConvertValue(value);
                }
            }
            return changed;
        }

        private static bool ConvertValue(BsonValue value)
        {
            var changed = false;
            if (value.IsBsonDocument)
            {
                changed = Convert(value.AsBsonDocument);
            }
            else if (value.IsBsonArray)
            {
                var array = value.AsBsonArray;
                for (var i = 0; i < array.Count; i++) { changed |= ConvertValue(array[i]); }
            }
            return changed;
        }
    }
}
