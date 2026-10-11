using System.Collections.Generic;
using MongoDB.Bson;
using MUD.Core.Repositories.Interfaces;

namespace MUD.Core.Import
{
    // Adapts IRoomRepository to the importer's store. Only field-level operations are exposed.
    public class RoomRepositoryImportStore : IRoomImportStore
    {
        private readonly IRoomRepository _repository;

        public RoomRepositoryImportStore(IRoomRepository repository)
        {
            _repository = repository;
        }

        public BsonDocument Find(string id)
        {
            return _repository.GetRawDocument(id);
        }

        public List<string> FindIdsByArea(string area)
        {
            return _repository.GetIdsByField("Area", area);
        }

        public void Upsert(string id, BsonDocument set, BsonDocument setOnInsert)
        {
            _repository.UpsertFields(id, set, setOnInsert);
        }
    }
}
