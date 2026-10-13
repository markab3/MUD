using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace MUD.Data
{
    public interface IRepository<TModel> where TModel : Entity
    {
        void Insert(TModel model);	

        bool Update(TModel model);	

        bool Delete(TModel model);

        TModel Get(string id);

        IEnumerable<TModel> GetAll();

        IEnumerable<TModel> Search(Expression<Func<TModel, bool>> matchExpression);

        // Write-through of runtime state: $set only the given fields of an existing document (never replaces it).
        // Returns false if the document does not exist or the write was not acknowledged.
        bool UpdateFields(string id, MongoDB.Bson.BsonDocument set);

        // Raw stored document (bypasses the cache), or null.
        MongoDB.Bson.BsonDocument GetRawDocument(string id);

        // _ids of stored documents where the given field equals the given value.
        List<string> GetIdsByField(string fieldName, string value);

        // Field-level upsert: $set the given fields, $setOnInsert the insert defaults. Never replaces the document.
        // A cached live instance, if any, is refreshed afterwards.
        void UpsertFields(string id, MongoDB.Bson.BsonDocument set, MongoDB.Bson.BsonDocument setOnInsert);
    }
}