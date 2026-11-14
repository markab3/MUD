using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using MongoDB.Bson;
using MUD.Core;
using MUD.Core.GameObjects;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;

namespace MUD.Tests
{
    // A room repository with a live cache and a stored copy, like the real one but in memory.
    public class SyncRoomRepo : IRoomRepository, ISnapshotRepository
    {
        public Dictionary<string, Room> Cache = new Dictionary<string, Room>();
        public Dictionary<string, BsonDocument> Stored = new Dictionary<string, BsonDocument>();
        public List<string> Evicted = new List<string>();

        public Room Get(string id) { Room r; return id != null && Cache.TryGetValue(id, out r) ? r : null; }
        public BsonDocument GetRawDocument(string id) { BsonDocument d; return id != null && Stored.TryGetValue(id, out d) ? d.DeepClone().AsBsonDocument : null; }
        public bool UpdateFields(string id, BsonDocument set)
        {
            if (!Stored.ContainsKey(id)) { return false; }
            foreach (var e in set) { Stored[id][e.Name] = e.Value.DeepClone(); }
            return true;
        }
        public void Insert(Room model) { Stored[model.Id] = model.ToBsonDocument(); Cache[model.Id] = model; }
        public bool Update(Room model) { Stored[model.Id] = model.ToBsonDocument(); return true; }
        public bool Delete(Room model) { return Stored.Remove(model.Id); }
        public IEnumerable<Room> GetAll() { return Cache.Values; }
        public IEnumerable<Room> Search(Expression<Func<Room, bool>> m) { return new Room[0]; }
        public List<string> GetIdsByField(string f, string v) { return new List<string>(); }
        public void UpsertFields(string id, BsonDocument set, BsonDocument setOnInsert) { }

        public string CollectionName { get { return "room"; } }
        public IPersistSink Sink { get; set; }
        public bool ReplaceDocument(string id, BsonDocument document) { Stored[id] = document; return true; }
        public bool SetFields(string id, BsonDocument set) { return UpdateFields(id, set); }
        public void Evict(string id) { Evicted.Add(id); Cache.Remove(id); }
        public List<string> CachedIds() { return Cache.Keys.ToList(); }
    }

    // A player repository with a stored copy and a live list, like the real one but in memory. UpdateFields has $set
    // semantics, so tests can see exactly which stored fields a game save touches.
    public class SyncPlayerRepo : IPlayerRepository, ISnapshotRepository
    {
        public Dictionary<string, BsonDocument> Stored = new Dictionary<string, BsonDocument>();
        public List<Player> Live = new List<Player>();
        public List<BsonDocument> FieldWrites = new List<BsonDocument>();
        public int Replaces;

        public void CreateNameKeyIndex() { }
        public void Insert(Player model) { Stored[model.Id] = model.ToBsonDocument(); }
        public bool Update(Player model) { Replaces++; Stored[model.Id] = model.ToBsonDocument(); return true; }
        public bool Delete(Player model) { return Stored.Remove(model.Id); }
        public Player Get(string id) { return Live.FirstOrDefault(p => p.Id == id); }
        public IEnumerable<Player> GetAll() { return Live; }
        public IEnumerable<Player> Search(Expression<Func<Player, bool>> m) { return Live.Where(m.Compile()).ToList(); }
        public bool UpdateFields(string id, BsonDocument set)
        {
            if (!Stored.ContainsKey(id)) { return false; }
            FieldWrites.Add(set.DeepClone().AsBsonDocument);
            foreach (var e in set) { Stored[id][e.Name] = e.Value.DeepClone(); }
            return true;
        }
        public BsonDocument GetRawDocument(string id) { return Stored.ContainsKey(id) ? Stored[id].DeepClone().AsBsonDocument : null; }
        public List<string> GetIdsByField(string f, string v) { return new List<string>(); }
        public void UpsertFields(string id, BsonDocument set, BsonDocument setOnInsert) { }

        public string CollectionName { get { return "player"; } }
        public IPersistSink Sink { get; set; }
        public bool ReplaceDocument(string id, BsonDocument document) { Stored[id] = document; return true; }
        public bool SetFields(string id, BsonDocument set) { return UpdateFields(id, set); }
        public void Evict(string id) { Live.RemoveAll(p => p.Id == id); }
        public List<string> CachedIds() { return Live.Select(p => p.Id).ToList(); }
    }
}
