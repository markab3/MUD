using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using MongoDB.Bson;
using MongoDB.Driver;
using MUD.Core;
using MUD.Core.GameObjects;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;
using Xunit;

namespace MUD.Tests
{
    // Fake IMongoClient/IMongoDatabase/IMongoCollection built on DispatchProxy so Repository<T> can run without Mongo.
    public class FakeMongo : DispatchProxy
    {
        public static ReplaceOneResult ReplaceResult;
        public static int ReplaceCalls;

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            if (targetMethod.Name == "GetDatabase")
            {
                return Create<IMongoDatabase, FakeMongo>();
            }
            if (targetMethod.Name == "GetCollection")
            {
                return typeof(FakeMongo).GetMethod(nameof(MakeCollection), BindingFlags.Static | BindingFlags.NonPublic)
                    .MakeGenericMethod(targetMethod.GetGenericArguments()).Invoke(null, null);
            }
            if (targetMethod.Name == "ReplaceOne")
            {
                ReplaceCalls++;
                return ReplaceResult;
            }
            throw new NotSupportedException(targetMethod.Name);
        }

        private static object MakeCollection<T>() { return Create<IMongoCollection<T>, FakeMongo>(); }
    }

    public class TestRepo : Repository<Player>
    {
        public TestRepo() : base(DispatchProxy.Create<IMongoClient, FakeMongo>(), "db", "player") { }
    }

    public class RepositoryUpdateTests
    {
        private static bool Run(ReplaceOneResult result, string id = "5f6e27f20c1fdd24b4b18b1a")
        {
            FakeMongo.ReplaceResult = result;
            var player = new Player(null, null, null) { Id = id };
            return new TestRepo().Update(player);
        }

        [Fact]
        public void UnchangedDocumentStillCountsAsSuccess()
        {
            // matched 1, modified 0: what Mongo reports when the saved document is identical.
            Assert.True(Run(new ReplaceOneResult.Acknowledged(1, 0, null)));
        }

        [Fact]
        public void ModifiedDocumentIsSuccess()
        {
            Assert.True(Run(new ReplaceOneResult.Acknowledged(1, 1, null)));
        }

        [Fact]
        public void NoMatchIsFailure()
        {
            Assert.False(Run(new ReplaceOneResult.Acknowledged(0, 0, null)));
        }

        [Fact]
        public void UnacknowledgedIsFailure()
        {
            Assert.False(Run(ReplaceOneResult.Unacknowledged.Instance));
        }

        [Fact]
        public void SuccessfulUpdateIsCachedForGet()
        {
            FakeMongo.ReplaceResult = new ReplaceOneResult.Acknowledged(1, 0, null);
            var repo = new TestRepo();
            var player = new Player(null, null, null) { Id = "5f6e27f20c1fdd24b4b18b1a" };
            Assert.True(repo.Update(player));
            Assert.Same(player, repo.Get(player.Id));
        }
    }

    public class SavePlayerRepo : IPlayerRepository
    {
        public bool UpdateResult = true;
        public int Inserts, Updates;
        public void Insert(Player model) { Inserts++; }
        public bool Update(Player model) { Updates++; return UpdateResult; }
        public bool Delete(Player model) { return true; }
        public Player Get(string id) { return null; }
        public IEnumerable<Player> GetAll() { return new Player[0]; }
        public IEnumerable<Player> Search(Expression<Func<Player, bool>> m) { return new Player[0]; }
        public bool UpdateFields(string id, BsonDocument set) { return true; }
        public BsonDocument GetRawDocument(string id) { return null; }
        public List<string> GetIdsByField(string fieldName, string value) { return new List<string>(); }
        public void UpsertFields(string id, BsonDocument set, BsonDocument setOnInsert) { }
    }

    public class HeartbeatPlayer : Player
    {
        public HeartbeatPlayer(IPlayerRepository repo) : base(null, null, repo) { }
        public override void ReceiveMessage(string message) { }
        public void SetLastSave(DateTime when) { _lastSave = when; }
    }

    public class AutosaveTests
    {
        private static HeartbeatPlayer LoggedIn(SavePlayerRepo repo, TimeSpan sinceLastSave)
        {
            var p = new HeartbeatPlayer(repo) { Id = "5f6e27f20c1fdd24b4b18b1a", ConnectionStatus = PlayerConnectionStatuses.LoggedIn };
            p.SetLastSave(DateTime.Now - sinceLastSave);
            return p;
        }

        [Fact]
        public void SavesWhenMoreThanAnHourHasPassed()
        {
            // Minutes component of 1h05m is 5, which the old check treated as "not due".
            var repo = new SavePlayerRepo();
            LoggedIn(repo, TimeSpan.FromMinutes(65)).DoHeartbeat();
            Assert.Equal(1, repo.Updates);
        }

        [Fact]
        public void SavesAfterTenMinutes()
        {
            var repo = new SavePlayerRepo();
            LoggedIn(repo, TimeSpan.FromMinutes(11)).DoHeartbeat();
            Assert.Equal(1, repo.Updates);
        }

        [Fact]
        public void DoesNotSaveBeforeTenMinutes()
        {
            var repo = new SavePlayerRepo();
            LoggedIn(repo, TimeSpan.FromMinutes(5)).DoHeartbeat();
            Assert.Equal(0, repo.Updates);
        }

        [Fact]
        public void SaveReturnsFalseWhenUpdateFails()
        {
            var repo = new SavePlayerRepo { UpdateResult = false };
            Assert.False(LoggedIn(repo, TimeSpan.Zero).Save());
        }

        [Fact]
        public void SaveOfNewPlayerUpsertsAndGetsAnId()
        {
            // Entities are given an Id on construction, so a first save goes through Update (upsert).
            var repo = new SavePlayerRepo();
            var p = new HeartbeatPlayer(repo);
            Assert.False(string.IsNullOrWhiteSpace(p.Id));
            Assert.True(p.Save());
            Assert.Equal(1, repo.Updates);
        }
    }

    public class RoomSerializationTests
    {
        [Fact]
        public void OccupantsAreNotSerialized()
        {
            var room = new Room(null) { Id = "5f6e27f20c1fdd24b4b18b1a" };
            room.Occupants.Add(new HeartbeatPlayer(new SavePlayerRepo()) { PlayerName = "bob" });

            var doc = room.ToBsonDocument();

            Assert.False(doc.Contains("Occupants"));
            Assert.False(doc.Contains("occupants"));
            Assert.False(doc.Contains("_occupants"));
        }
    }
}
