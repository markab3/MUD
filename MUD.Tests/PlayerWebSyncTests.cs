using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MUD.Core;
using MUD.Core.Commands;
using MUD.Core.GameObjects;
using MUD.Core.Properties.PlayerProperties;
using MUD.Core.Repositories;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;
using Xunit;

namespace MUD.Tests
{
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
        public Dictionary<string, int> GetRevisions(IEnumerable<string> ids)
        {
            var result = new Dictionary<string, int>();
            foreach (var id in ids)
            {
                if (!Stored.ContainsKey(id)) { continue; }
                BsonValue rev;
                result[id] = Stored[id].TryGetValue("Rev", out rev) && rev.IsNumeric ? rev.ToInt32() : 0;
            }
            return result;
        }
    }

    [Collection("World construction")] // one test builds a World, which registers BSON class maps
    public class PlayerWebSyncTests
    {
        private class Listener : Player
        {
            public List<string> Messages = new List<string>();
            public int Quits;
            public Listener(string name, IPlayerRepository repo) : base(null, null, repo)
            {
                Id = "p." + name.ToLowerInvariant();
                PlayerName = name; NameKey = name.ToLowerInvariant(); ShortDescription = name; Password = "secret";
                Race = "human"; Gender = "female"; Class = "fighter";
            }
            public override void ReceiveMessage(string message) { Messages.Add(message); }
        }

        private static Listener Stored(SyncPlayerRepo repo, string name)
        {
            var p = new Listener(name, repo);
            repo.Stored[p.Id] = p.ToBsonDocument();
            repo.Live.Add(p);
            return p;
        }

        // ---- Player.Save never writes the website-owned fields ----

        [Fact]
        public void GameSave_SetsOnlyGameOwnedFields_AndNeverTheWebOwnedOnes()
        {
            var repo = new SyncPlayerRepo();
            var p = Stored(repo, "Ann");
            p.CurrentLocationId = "t.hall";
            p.Inventory.Add(new InventoryItem { ShortDescription = "rope", Handles = new List<string> { "rope" }, Plurals = new List<string>() });

            Assert.True(p.Save(true));

            var set = Assert.Single(repo.FieldWrites);
            Assert.True(set.Contains("Inventory"));
            Assert.True(set.Contains("CurrentLocationId"));
            Assert.True(set.Contains("Password"));
            foreach (var web in PlayerFields.NotWrittenByGame) { Assert.False(set.Contains(web), web + " must not be written by the game"); }
            Assert.Equal(0, repo.Replaces);
        }

        [Fact]
        public void StaleLiveObject_SavingDoesNotRevertAWebEdit()
        {
            var repo = new SyncPlayerRepo();
            var p = Stored(repo, "Ann");

            // The website edits the stored player while the live object is stale.
            repo.Stored[p.Id]["ShortDescription"] = "Ann the Bold";
            repo.Stored[p.Id]["Race"] = "elf";
            repo.Stored[p.Id]["Retired"] = true;
            repo.Stored[p.Id]["Rev"] = 4;
            p.Inventory.Add(new InventoryItem { ShortDescription = "rope", Handles = new List<string> { "rope" }, Plurals = new List<string>() });

            Assert.True(p.Save(true));

            Assert.Equal("Ann the Bold", repo.Stored[p.Id]["ShortDescription"].AsString);
            Assert.Equal("elf", repo.Stored[p.Id]["Race"].AsString);
            Assert.True(repo.Stored[p.Id]["Retired"].AsBoolean);
            Assert.Equal(4, repo.Stored[p.Id]["Rev"].AsInt32);
            Assert.Equal(1, repo.Stored[p.Id]["Inventory"].AsBsonArray.Count); // the game's own state still saved
        }

        [Fact]
        public void StaleLiveObject_SavingDoesNotRevertAWebRoleChange()
        {
            var repo = new SyncPlayerRepo();
            var p = Stored(repo, "Ann");
            var roles = new BsonArray { new BsonDocument { { "_t", new BsonArray { "Entity", "ExtendedProperty", "CreatorProperty" } }, { "_id", "x" } } };
            repo.Stored[p.Id]["ExtendedProperties"] = roles;

            p.Save(true);

            Assert.Equal(1, repo.Stored[p.Id]["ExtendedProperties"].AsBsonArray.Count);
        }

        [Fact]
        public void InGamePromote_StillWritesRoles()
        {
            var repo = new SyncPlayerRepo();
            var p = Stored(repo, "Ann");
            p.ExtendedProperties.Add(new CreatorProperty());
            p.MarkRolesChanged();

            p.Save(true);

            Assert.Equal(1, repo.Stored[p.Id]["ExtendedProperties"].AsBsonArray.Count);
            // The flag is one-shot: a later ordinary save leaves roles alone again.
            repo.Stored[p.Id]["ExtendedProperties"] = new BsonArray();
            p.Save(true);
            Assert.Equal(0, repo.Stored[p.Id]["ExtendedProperties"].AsBsonArray.Count);
        }

        [Fact]
        public void FirstSaveOfANewPlayer_WritesTheWholeDocument()
        {
            var repo = new SyncPlayerRepo();
            var p = new Listener("Newbie", repo);
            Assert.True(p.Save(true));
            Assert.Equal(1, repo.Replaces);
            Assert.True(repo.Stored.ContainsKey(p.Id));
        }

        // ---- ApplyWebEdit ----

        [Fact]
        public void ApplyWebEdit_AppliesAllowListOnly_AndLeavesGameStateAlone()
        {
            var repo = new SyncPlayerRepo();
            var p = Stored(repo, "Ann");
            p.CurrentLocationId = "t.hall";
            p.Inventory.Add(new InventoryItem { ShortDescription = "rope", Handles = new List<string> { "rope" }, Plurals = new List<string>() });
            var doc = repo.Stored[p.Id];
            doc["ShortDescription"] = "Ann the Bold";
            doc["LongDescription"] = "Tall.";
            doc["Race"] = "elf";
            doc["Gender"] = BsonNull.Value;
            doc["Class"] = "mage";
            doc["Rev"] = 3;
            doc["Password"] = "changed-elsewhere";       // never read by ApplyWebEdit
            doc["CurrentLocationId"] = "t.elsewhere";     // not web-editable
            doc["Inventory"] = new BsonArray();           // game-owned

            var changes = p.ApplyWebEdit(doc);

            Assert.Equal(Player.WebChanges.Text, changes);
            Assert.Equal("Ann the Bold", p.ShortDescription);
            Assert.Equal("Tall.", p.LongDescription);
            Assert.Equal("elf", p.Race);
            Assert.Null(p.Gender);
            Assert.Equal("mage", p.Class);
            Assert.Equal(3, p.Rev);
            Assert.Equal("secret", p.Password);
            Assert.Equal("t.hall", p.CurrentLocationId);
            Assert.Single(p.Inventory);
        }

        [Fact]
        public void ApplyWebEdit_SyncsRoles_ReplacingOnlyTheRoleElements()
        {
            var repo = new SyncPlayerRepo();
            var p = Stored(repo, "Ann");
            var doc = repo.Stored[p.Id];
            doc["ExtendedProperties"] = new BsonArray { new BsonDocument { { "_t", new BsonArray { "Entity", "ExtendedProperty", "CreatorProperty" } }, { "_id", "c1" } } };
            Assert.Equal(Player.WebChanges.Roles, p.ApplyWebEdit(doc));
            Assert.Single(p.ExtendedProperties.OfType<CreatorProperty>());

            doc["ExtendedProperties"] = new BsonArray();
            Assert.Equal(Player.WebChanges.Roles, p.ApplyWebEdit(doc));
            Assert.Empty(p.ExtendedProperties.OfType<CreatorProperty>());
            Assert.Equal(Player.WebChanges.None, p.ApplyWebEdit(doc)); // idempotent
        }

        // ---- the 5 second poll ----

        private static (PlayerWebSync Sync, SyncPlayerRepo Repo, List<string> Log) Make(params string[] names)
        {
            var repo = new SyncPlayerRepo();
            foreach (var n in names) { Stored(repo, n); }
            var log = new List<string>();
            return (new PlayerWebSync(repo, () => repo.Live.ToList(), log.Add), repo, log);
        }

        private static void PollAndApply(PlayerWebSync sync, SyncPlayerRepo repo) { sync.Apply(sync.Poll(repo.Live.Select(p => p.Id).ToList())); }

        [Fact]
        public void PollInterval_IsFiveSeconds_AndIsDueOncePerInterval()
        {
            Assert.Equal(TimeSpan.FromSeconds(5), PlayerWebSync.PollInterval);
            var (sync, _, _) = Make();
            var t = new DateTime(2026, 1, 1, 12, 0, 0);
            Assert.True(sync.IsDue(t));
            Assert.False(sync.IsDue(t.AddSeconds(4)));
            Assert.True(sync.IsDue(t.AddSeconds(5)));
        }

        [Fact]
        public void ChangedRev_AppliesToTheOnlinePlayer_AndTellsThem()
        {
            var (sync, repo, _) = Make("Ann", "Bob");
            var ann = (Listener)repo.Live[0];
            var bob = (Listener)repo.Live[1];
            repo.Stored[ann.Id]["ShortDescription"] = "Ann the Bold";
            repo.Stored[ann.Id]["Rev"] = 1;

            PollAndApply(sync, repo);

            Assert.Equal("Ann the Bold", ann.ShortDescription);
            Assert.Equal(1, ann.Rev);
            Assert.Equal(new[] { PlayerWebSync.DescriptionChangedMessage }, ann.Messages.ToArray());
            Assert.Empty(bob.Messages);
        }

        [Fact]
        public void UnchangedRev_AppliesNothing()
        {
            var (sync, repo, _) = Make("Ann");
            var ann = (Listener)repo.Live[0];
            repo.Stored[ann.Id]["ShortDescription"] = "sneaky, but Rev not bumped";

            PollAndApply(sync, repo);

            Assert.Equal("Ann", ann.ShortDescription);
            Assert.Empty(ann.Messages);
        }

        [Fact]
        public void RoleGrant_IsAppliedAndAnnounced()
        {
            var (sync, repo, _) = Make("Ann");
            var ann = (Listener)repo.Live[0];
            repo.Stored[ann.Id]["ExtendedProperties"] = new BsonArray { new BsonDocument { { "_t", new BsonArray { "Entity", "ExtendedProperty", "CreatorProperty" } }, { "_id", "c1" } } };
            repo.Stored[ann.Id]["Rev"] = 1;

            PollAndApply(sync, repo);

            Assert.Single(ann.ExtendedProperties.OfType<CreatorProperty>());
            Assert.Contains(PlayerWebSync.RolesChangedMessage, ann.Messages);
        }

        [Fact]
        public void RetiringAnOnlinePlayer_TellsThemThenQuits()
        {
            var world = new World(new MongoDB.Driver.MongoClient("mongodb://127.0.0.1:1"), new WorldSettings(), new FakeClock());
            var repo = new SyncPlayerRepo();
            var conn = new FakeSink();
            var ann = new QuitTracker(world, repo);
            world.AttachPlayer(ann, world.OpenSession("test", "test", conn));
            repo.Stored[ann.Id] = ann.ToBsonDocument();
            repo.Live.Add(ann);
            world.Players.Add(ann);
            var sync = new PlayerWebSync(repo, () => repo.Live.ToList(), s => { });

            repo.Stored[ann.Id]["Retired"] = true;
            repo.Stored[ann.Id]["Rev"] = 1;
            PollAndApply(sync, repo);

            Assert.True(ann.Retired);
            Assert.Contains(PlayerWebSync.RetiredMessage, ann.Messages);
            // The normal quit path ran: farewell, left the world, final save, flushing disconnect.
            Assert.DoesNotContain(ann, world.Players);
            Assert.Contains(conn.Events, e => e.StartsWith("close:"));
            Assert.True(ann.Messages.IndexOf(PlayerWebSync.RetiredMessage) < ann.Messages.IndexOf("Thanks for playing!"));
        }

        private class QuitTracker : Player
        {
            public List<string> Messages = new List<string>();
            public QuitTracker(World world, IPlayerRepository repo) : base(world, null, repo)
            {
                Id = "p.ann"; PlayerName = "Ann"; NameKey = "ann"; Password = "secret";
                ShortDescription = "Ann"; Race = "human";
            }
            public override void ReceiveMessage(string message) { Messages.Add(message); }
        }

        [Fact]
        public void MissingDocument_IsIgnored()
        {
            var (sync, repo, log) = Make("Ann");
            var ann = (Listener)repo.Live[0];
            repo.Stored.Remove(ann.Id);
            PollAndApply(sync, repo);
            Assert.Empty(ann.Messages);
            Assert.Empty(log);
        }

        [Fact]
        public void ApplyingToAPlayerNoLongerOnline_IsSkippedWithoutError()
        {
            var (sync, repo, log) = Make("Ann");
            var ann = (Listener)repo.Live[0];
            repo.Stored[ann.Id]["Rev"] = 1;
            var poll = sync.Poll(repo.Live.Select(p => p.Id).ToList());
            repo.Live.Clear();
            sync.Apply(poll);
            Assert.Empty(log);
        }
    }
}
