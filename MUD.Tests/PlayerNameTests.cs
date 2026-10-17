using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MUD.Core;
using MUD.Core.GameObjects;
using MUD.Core.Repositories;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;
using Xunit;

namespace MUD.Tests
{
    // In-memory player store that enforces NameKey uniqueness like the real unique index does.
    public class NameKeyPlayerRepo : IPlayerRepository
    {
        public List<Player> Stored = new List<Player>();
        public bool FailIndex;
        public int IndexCreates;
        public int Deletes;

        public void CreateNameKeyIndex()
        {
            if (FailIndex) { throw new InvalidOperationException("index build failed"); }
            IndexCreates++;
        }

        public void Insert(Player model)
        {
            if (model.NameKey != null && Stored.Any(p => p.NameKey == model.NameKey)) { throw new DuplicateKeyException(); }
            Stored.Add(model);
        }

        public bool Update(Player model)
        {
            if (model.NameKey != null && Stored.Any(p => p.NameKey == model.NameKey && p.Id != model.Id)) { throw new DuplicateKeyException(); }
            if (!Stored.Contains(model)) { Stored.Add(model); }
            return true;
        }

        public bool Delete(Player model) { Deletes++; return Stored.Remove(model); }
        public Player Get(string id) { return Stored.FirstOrDefault(p => p.Id == id); }
        public IEnumerable<Player> GetAll() { return Stored.ToList(); }
        public IEnumerable<Player> Search(Expression<Func<Player, bool>> m) { return Stored.Where(m.Compile()).ToList(); }

        public bool UpdateFields(string id, BsonDocument set)
        {
            var p = Get(id);
            if (p == null) { return false; }
            if (set.Contains("NameKey")) { p.NameKey = set["NameKey"].AsString; }
            return true;
        }

        public BsonDocument GetRawDocument(string id) { return null; }
        public List<string> GetIdsByField(string f, string v) { return new List<string>(); }
        public void UpsertFields(string id, BsonDocument set, BsonDocument setOnInsert) { }
    }

    public class PlayerNameTests
    {
        private static Player Stored(NameKeyPlayerRepo repo, string id, string name, string key, string password = "pw")
        {
            var p = new QuietPlayer(repo) { Id = id, PlayerName = name, NameKey = key, Password = password };
            repo.Stored.Add(p);
            return p;
        }

        private static PlayerAccounts Accounts(NameKeyPlayerRepo repo, List<string> log = null)
        {
            return new PlayerAccounts(repo, s => log?.Add(s));
        }

        [Fact]
        public void LoginIsCaseInsensitiveAndTrimmed()
        {
            var repo = new NameKeyPlayerRepo();
            var holen = Stored(repo, "p1", "Holen", "holen");

            Assert.Same(holen, Accounts(repo).Authenticate("HOLEN", "pw"));
            Assert.Same(holen, Accounts(repo).Authenticate("  hOlEn ", "pw"));
            Assert.Null(Accounts(repo).Authenticate("holen", "wrong"));
        }

        [Fact]
        public void LoginNeverMatchesBySubstring()
        {
            var repo = new NameKeyPlayerRepo();
            Stored(repo, "p1", "Holenbeck", "holenbeck");

            Assert.Null(Accounts(repo).FindByName("holen"));
            Assert.Null(Accounts(repo).FindByName("beck"));
        }

        [Fact]
        public void SubstringNameIsAllowedAndSeparateFromTheLongerName()
        {
            var repo = new NameKeyPlayerRepo();
            var longer = Stored(repo, "p1", "Holenbeck", "holenbeck");

            Assert.Null(PlayerAccounts.ValidateName("Holen"));
            Assert.Null(Accounts(repo).FindByName("Holen"));

            var shorter = new QuietPlayer(repo) { PlayerName = "Holen", NameKey = PlayerAccounts.Normalize("Holen"), Password = "pw" };
            Assert.Equal(PlayerSaveResult.Saved, Accounts(repo).TrySave(shorter));
            Assert.Same(shorter, Accounts(repo).FindByName("holen"));
            Assert.Same(longer, Accounts(repo).FindByName("HOLENBECK"));
        }

        [Fact]
        public void DuplicateKeyOnSaveMeansNameTakenEvenWhenPreCheckPassed()
        {
            var repo = new NameKeyPlayerRepo();
            Stored(repo, "p1", "Holen", "holen");
            // A concurrent creator that slipped past the pre-check.
            var racer = new QuietPlayer(repo) { PlayerName = "HOLEN", NameKey = "holen", Password = "pw" };

            Assert.Equal(PlayerSaveResult.NameTaken, Accounts(repo).TrySave(racer));
            Assert.Single(repo.Stored);
        }

        [Theory]
        [InlineData("Bob")]
        [InlineData("Abcdefghijklmnop")]
        [InlineData("Holen")]
        public void ValidNamesAreAccepted(string name)
        {
            Assert.Null(PlayerAccounts.ValidateName(name));
        }

        [Theory]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData("ab")]
        [InlineData("Abcdefghijklmnopq")]
        [InlineData("Hol3n")]
        [InlineData("Ho-len")]
        [InlineData("Ho len")]
        [InlineData("Ho_len")]
        [InlineData("H\u00f6len")]
        [InlineData("admin")]
        [InlineData("ADMIN")]
        [InlineData("System")]
        [InlineData("god")]
        [InlineData("Quit")]
        [InlineData("who")]
        [InlineData("create")]
        [InlineData("connect")]
        [InlineData("self")]
        [InlineData("Me")]
        public void InvalidOrReservedNamesAreRejectedWithAMessage(string name)
        {
            var problem = PlayerAccounts.ValidateName(name);
            Assert.False(string.IsNullOrWhiteSpace(problem));
        }

        [Fact]
        public void BackfillSetsMissingKeysAndLeavesExistingOnesAlone()
        {
            var repo = new NameKeyPlayerRepo();
            var a = Stored(repo, "p1", "Holen", null);
            var b = Stored(repo, "p2", "Zed", "zed");

            Accounts(repo).BackfillNameKeys();

            Assert.Equal("holen", a.NameKey);
            Assert.Equal("zed", b.NameKey);
        }

        [Fact]
        public void BackfillSkipsCollidingRecordsLogsBothAndDeletesOrRenamesNothing()
        {
            var repo = new NameKeyPlayerRepo();
            var one = Stored(repo, "p1", "Holen", null);
            var two = Stored(repo, "p2", "HOLEN", null);
            var other = Stored(repo, "p3", "Zed", null);
            var log = new List<string>();

            Accounts(repo, log).BackfillNameKeys();

            Assert.Null(one.NameKey);
            Assert.Null(two.NameKey);
            Assert.Equal("zed", other.NameKey);
            Assert.Equal(3, repo.Stored.Count);
            Assert.Equal(0, repo.Deletes);
            Assert.Equal("Holen", one.PlayerName);
            Assert.Equal("HOLEN", two.PlayerName);
            var line = Assert.Single(log);
            Assert.Contains("p1", line);
            Assert.Contains("p2", line);
            Assert.Contains("Holen", line);
            Assert.Contains("HOLEN", line);
        }

        [Fact]
        public void BackfillCollisionWithAnAlreadyKeyedRecordIsAlsoSkipped()
        {
            var repo = new NameKeyPlayerRepo();
            var keyed = Stored(repo, "p1", "Holen", "holen");
            var legacy = Stored(repo, "p2", "HOLEN", null);

            Accounts(repo).BackfillNameKeys();

            Assert.Equal("holen", keyed.NameKey);
            Assert.Null(legacy.NameKey);
        }

        [Fact]
        public void InitializeCreatesTheIndexAndIsRepeatable()
        {
            var repo = new NameKeyPlayerRepo();
            Accounts(repo).Initialize();
            Accounts(repo).Initialize();
            Assert.Equal(2, repo.IndexCreates);
        }

        [Fact]
        public void InitializeSurvivesIndexFailureAndLogsIt()
        {
            var repo = new NameKeyPlayerRepo { FailIndex = true };
            var legacy = Stored(repo, "p1", "Holen", null);
            var log = new List<string>();

            Accounts(repo, log).Initialize(); // must not throw

            Assert.Equal("holen", legacy.NameKey);
            Assert.Contains(log, l => l.Contains("unique index"));
        }

        // --- World level: the failure messages reach the client and nothing is stored ---

        private static (World world, NameKeyPlayerRepo repo) NewWorld()
        {
            var repo = new NameKeyPlayerRepo();
            var world = (World)RuntimeHelpers.GetUninitializedObject(typeof(World));
            world.Players = new List<Player>();
            var services = new ServiceCollection().AddSingleton<IPlayerRepository>(repo).BuildServiceProvider();
            typeof(World).GetField("_serviceProvider", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(world, services);
            return (world, repo);
        }

        private static string ReadAvailable(TcpClient peer)
        {
            peer.ReceiveTimeout = 2000;
            var buf = new byte[1024];
            int n = peer.GetStream().Read(buf, 0, buf.Length);
            return System.Text.Encoding.ASCII.GetString(buf, 0, n);
        }

        private static MUD.Telnet.Client Connect(out TcpClient peer)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            peer = new TcpClient();
            peer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var serverSide = listener.AcceptSocket();
            listener.Stop();
            return new MUD.Telnet.Client(serverSide);
        }

        [Theory]
        [InlineData("admin", "reserved")]
        [InlineData("Bo", "letters long")]
        [InlineData("Bob1", "letters A-Z")]
        public void DoCreateUserRejectsBadNamesWithAMessageAndStoresNothing(string name, string expected)
        {
            var (world, repo) = NewWorld();
            var client = Connect(out TcpClient peer);
            try
            {
                Assert.False(world.DoCreateUser(name, "pw", client));
                Assert.Contains(expected, ReadAvailable(peer));
                Assert.Empty(repo.Stored);
            }
            finally { peer.Close(); }
        }

        [Fact]
        public void DoCreateUserRejectsAnExistingNameRegardlessOfCase()
        {
            var (world, repo) = NewWorld();
            Stored(repo, "p1", "Holen", "holen");
            var client = Connect(out TcpClient peer);
            try
            {
                Assert.False(world.DoCreateUser("hOLEN", "pw", client));
                Assert.Contains("already someone", ReadAvailable(peer));
                Assert.Single(repo.Stored);
            }
            finally { peer.Close(); }
        }
    }
}
