using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using MongoDB.Bson;
using MUD.Core.Commands;
using MUD.Core.GameObjects;
using MUD.Core.Repositories;
using MUD.Core.Repositories.Interfaces;
using MUD.Telnet;
using Xunit;

namespace MUD.Tests
{
    public class PasswordChangeTests
    {
        private class FakeConnection : ILoginConnection
        {
            public string RemoteKey { get; set; } = "5.6.7.8";
            public List<string> Events = new List<string>();
            public void Send(string text) { Events.Add("send:" + text); }
            public void SendPrompt(string text) { Events.Add("prompt:" + text); }
            public void SetEchoHidden(bool hidden) { Events.Add(hidden ? "echo-off" : "echo-on"); }
            public void DisconnectWithMessage(string message) { Events.Add("disconnect:" + message); }
            public string Last { get { return Events[Events.Count - 1]; } }
        }

        // In-memory player store. Docs is the "database": a whole-document Update replaces it, as in Mongo,
        // and tests simulate the website by editing Docs["Password"] directly.
        private class Repo : IPlayerRepository
        {
            public Dictionary<string, BsonDocument> Docs = new Dictionary<string, BsonDocument>();
            public List<Player> Players = new List<Player>();
            public bool UpdateResult = true;
            public int Updates;
            public void CreateNameKeyIndex() { }
            public void Insert(Player model) { Docs[model.Id] = model.ToBsonDocument(); }
            public bool Update(Player model)
            {
                if (!UpdateResult) { return false; }
                Updates++;
                Docs[model.Id] = model.ToBsonDocument();
                return true;
            }
            public bool Delete(Player model) { return Docs.Remove(model.Id); }
            public Player Get(string id) { return null; }
            public IEnumerable<Player> GetAll() { return Players; }
            public IEnumerable<Player> Search(Expression<Func<Player, bool>> m) { return Players.Where(m.Compile()); }
            // $set semantics, as in Mongo: only the given fields change; false when nothing is stored.
            public bool UpdateFields(string id, BsonDocument set)
            {
                if (!UpdateResult || !Docs.ContainsKey(id)) { return false; }
                Updates++;
                foreach (var e in set) { Docs[id][e.Name] = e.Value.DeepClone(); }
                return true;
            }
            public BsonDocument GetRawDocument(string id) { return Docs.ContainsKey(id) ? Docs[id].DeepClone().AsBsonDocument : null; }
            public List<string> GetIdsByField(string f, string v) { return new List<string>(); }
            public void UpsertFields(string id, BsonDocument set, BsonDocument setOnInsert) { }
            public string Stored { get { return Docs["p.holen"]["Password"].AsString; } }
        }

        private class TestPlayer : Player
        {
            [MongoDB.Bson.Serialization.Attributes.BsonIgnore]
            public ILoginConnection Channel { get; set; }
            public TestPlayer(IPlayerRepository repo) : base(null, null, repo) { }
            public override void ReceiveMessage(string message) { }
            public override ILoginConnection PromptChannel { get { return Channel; } }
        }

        private static LoginThrottle Throttle(int max = 3)
        {
            return new LoginThrottle(max, TimeSpan.FromMinutes(5));
        }

        // A logged-in player whose document is already stored (Updates reset afterwards).
        private static TestPlayer NewPlayer(Repo repo, FakeConnection conn, string password = "oldsecret")
        {
            var p = new TestPlayer(repo) { Id = "p.holen", PlayerName = "Holen", NameKey = "holen", Password = password, Channel = conn };
            repo.Update(p);
            repo.Players.Add(p);
            repo.Updates = 0;
            return p;
        }

        private static void Type(Player p, params string[] lines)
        {
            foreach (var l in lines) { p.DataReceivedHandler(null, l); }
        }

        // ---- the chpwd flow ----

        [Fact]
        public void HappyPath_PromptsWithEchoOff_ChangesAndSaves()
        {
            var repo = new Repo(); var conn = new FakeConnection();
            var p = NewPlayer(repo, conn);
            new PasswordChangeFlow(p, conn, Throttle()).Start();
            Assert.Equal(new[] { "echo-off", "prompt:Current password: " }, conn.Events);

            Type(p, "oldsecret");
            Assert.Equal("prompt:New password: ", conn.Last);
            Type(p, "newsecret");
            Assert.Equal("prompt:Confirm new password: ", conn.Last);
            Type(p, "newsecret");

            Assert.Equal("send:" + PasswordChangeFlow.SuccessMessage, conn.Last);
            Assert.Equal("newsecret", p.Password);
            Assert.Equal("newsecret", repo.Stored);
            Assert.False(p.HasPrompt);
            // Echo is hidden only while each password is typed, and always restored.
            Assert.Equal(3, conn.Events.Count(e => e == "echo-off"));
            Assert.Equal(3, conn.Events.Count(e => e == "echo-on"));
        }

        [Fact]
        public void WrongCurrent_Fails_AndCountsTowardTheLoginThrottle()
        {
            var repo = new Repo(); var conn = new FakeConnection();
            var p = NewPlayer(repo, conn);
            var throttle = Throttle(2);

            for (int i = 0; i < 2; i++)
            {
                new PasswordChangeFlow(p, conn, throttle).Start();
                Type(p, "wrong");
                Assert.Equal("send:" + PasswordChangeFlow.WrongCurrentMessage, conn.Last);
                Assert.False(p.HasPrompt);
            }
            Assert.Equal("oldsecret", p.Password);
            Assert.Equal(0, repo.Updates);
            Assert.True(throttle.IsBlocked("5.6.7.8"));

            // Blocked: no prompt is even offered.
            conn.Events.Clear();
            new PasswordChangeFlow(p, conn, throttle).Start();
            Assert.Equal(new[] { "send:" + PasswordChangeFlow.BlockedMessage }, conn.Events);
            Assert.False(p.HasPrompt);
        }

        [Fact]
        public void ThrottleIsSharedWithLogin_PriorLoginFailuresBlockChpwd()
        {
            var repo = new Repo(); var conn = new FakeConnection();
            var p = NewPlayer(repo, conn);
            var throttle = Throttle(2);
            throttle.RecordFailure("5.6.7.8");
            throttle.RecordFailure("5.6.7.8"); // two failed logins
            new PasswordChangeFlow(p, conn, throttle).Start();
            Assert.Equal("send:" + PasswordChangeFlow.BlockedMessage, conn.Last);
            Assert.False(p.HasPrompt);
        }

        [Fact]
        public void Mismatch_ChangesNothing()
        {
            var repo = new Repo(); var conn = new FakeConnection();
            var p = NewPlayer(repo, conn);
            new PasswordChangeFlow(p, conn, Throttle()).Start();
            Type(p, "oldsecret", "newsecret", "newsecreT");
            Assert.Equal("send:" + PasswordChangeFlow.MismatchMessage, conn.Last);
            Assert.Equal("oldsecret", p.Password);
            Assert.Equal(0, repo.Updates);
            Assert.False(p.HasPrompt);
        }

        [Fact]
        public void NewEqualToCurrent_IsRejected()
        {
            var repo = new Repo(); var conn = new FakeConnection();
            var p = NewPlayer(repo, conn);
            new PasswordChangeFlow(p, conn, Throttle()).Start();
            Type(p, "oldsecret", "oldsecret");
            Assert.Equal("send:" + PasswordChangeFlow.SameMessage, conn.Last);
            Assert.Equal(0, repo.Updates);
        }

        [Theory]
        [InlineData("short")]
        [InlineData("has\tcontrol")]
        public void RuleViolations_AreRejected_WithTheSharedMessage(string bad)
        {
            var repo = new Repo(); var conn = new FakeConnection();
            var p = NewPlayer(repo, conn);
            new PasswordChangeFlow(p, conn, Throttle()).Start();
            Type(p, "oldsecret", bad);
            Assert.Equal("send:" + PlayerAccounts.ValidatePassword(bad), conn.Last);
            Assert.Equal("oldsecret", p.Password);
            Assert.False(p.HasPrompt);
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("12345", false)]
        [InlineData("123456", true)]
        [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]   // 64
        [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]  // 65
        [InlineData("tab\there", false)]
        [InlineData("nul\0here", false)]
        public void ValidatePassword_Rules(string pw, bool ok)
        {
            Assert.Equal(ok, PlayerAccounts.ValidatePassword(pw) == null);
        }

        [Fact]
        public void SaveFailure_TellsPlayer_AndKeepsOldPasswordInMemory()
        {
            var repo = new Repo(); var conn = new FakeConnection();
            var p = NewPlayer(repo, conn);
            repo.UpdateResult = false;
            new PasswordChangeFlow(p, conn, Throttle()).Start();
            Type(p, "oldsecret", "newsecret", "newsecret");
            Assert.Equal("send:" + PasswordChangeFlow.SaveFailedMessage, conn.Last);
            Assert.Equal("oldsecret", p.Password);
            Assert.Equal("oldsecret", repo.Stored);
        }

        // ---- nothing secret in the command path or logs ----

        [Fact]
        public void ChpwdIsADefaultCommand_WithHelp_AndRedactsAnyArguments()
        {
            var cmd = new ChpwdCommand(null);
            Assert.Equal(new[] { "chpwd" }, cmd.CommandKeywords);
            Assert.Equal(CommandCategories.Default, cmd.CommandCategory);
            Assert.Contains("Syntax: chpwd", cmd.HelpText);
            Assert.Equal("chpwd", cmd.Redact("chpwd hunter2"));
            Assert.DoesNotContain("hunter2", cmd.Redact("chpwd hunter2 hunter3"));
        }

        [Fact]
        public void PasswordLines_AreConsumedByThePromptHandler_NotQueuedOrResolved()
        {
            // commandQueue and world are null: a line that reached command resolution would throw.
            var repo = new Repo(); var conn = new FakeConnection();
            var p = NewPlayer(repo, conn);
            var seen = new List<string>();
            p.SetPromptHandler(seen.Add);
            p.DataReceivedHandler(null, "TopSecret!");
            Assert.Equal(new[] { "TopSecret!" }, seen);
        }

        [Fact]
        public void NothingTheFlowSendsContainsAnyPassword()
        {
            var repo = new Repo(); var conn = new FakeConnection();
            var p = NewPlayer(repo, conn, "oldsecret");
            new PasswordChangeFlow(p, conn, Throttle()).Start();
            Type(p, "oldsecret", "newsecret", "othersecret");
            new PasswordChangeFlow(p, conn, Throttle()).Start();
            Type(p, "wrongpass");
            var all = string.Join("\n", conn.Events);
            foreach (var secret in new[] { "oldsecret", "newsecret", "othersecret", "wrongpass" })
            {
                Assert.DoesNotContain(secret, all);
            }
        }

        [Fact]
        public void FlowWritesNothingToTheConsole()
        {
            var repo = new Repo(); var conn = new FakeConnection();
            var p = NewPlayer(repo, conn);
            var original = Console.Out;
            var sw = new System.IO.StringWriter();
            Console.SetOut(sw);
            try
            {
                new PasswordChangeFlow(p, conn, Throttle()).Start();
                Type(p, "oldsecret", "newsecret", "newsecret");
            }
            finally { Console.SetOut(original); }
            var output = sw.ToString();
            Assert.DoesNotContain("oldsecret", output);
            Assert.DoesNotContain("newsecret", output);
        }

        // ---- stale in-memory password vs. a web change ----

        [Fact]
        public void StaleSave_DoesNotOverwriteAPasswordChangedOnTheWebsite()
        {
            var repo = new Repo(); var conn = new FakeConnection();
            var p = NewPlayer(repo, conn, "oldsecret");

            repo.Docs["p.holen"]["Password"] = "webpassword"; // the website $sets Password on the stored document

            Assert.True(p.Save(true)); // autosave / quit / save command
            Assert.Equal("webpassword", repo.Stored);
            Assert.Equal("webpassword", p.Password); // memory caught up
        }

        [Fact]
        public void StaleSave_StillSavesOtherFields()
        {
            var repo = new Repo(); var conn = new FakeConnection();
            var p = NewPlayer(repo, conn, "oldsecret");
            repo.Docs["p.holen"]["Password"] = "webpassword";
            p.SelectedTerm = "ansi";
            p.Save(true);
            Assert.Equal("ansi", repo.Docs["p.holen"]["SelectedTerm"].AsString);
            Assert.Equal("webpassword", repo.Stored);
        }

        [Fact]
        public void Chpwd_AfterWebChange_UsesTheStoredPasswordAsCurrent()
        {
            var repo = new Repo(); var conn = new FakeConnection();
            var p = NewPlayer(repo, conn, "oldsecret");
            repo.Docs["p.holen"]["Password"] = "webpassword";

            new PasswordChangeFlow(p, conn, Throttle()).Start();
            Type(p, "oldsecret"); // the stale password no longer works
            Assert.Equal("send:" + PasswordChangeFlow.WrongCurrentMessage, conn.Last);

            new PasswordChangeFlow(p, conn, Throttle()).Start();
            Type(p, "webpassword", "gamechange", "gamechange");
            Assert.Equal("gamechange", repo.Stored);
        }

        [Fact]
        public void IngameChange_IsNotRevertedByTheNextOrdinarySave()
        {
            var repo = new Repo(); var conn = new FakeConnection();
            var p = NewPlayer(repo, conn, "oldsecret");
            new PasswordChangeFlow(p, conn, Throttle()).Start();
            Type(p, "oldsecret", "newsecret", "newsecret");
            p.Save(true);
            Assert.Equal("newsecret", repo.Stored);
            Assert.Equal("newsecret", p.Password);
        }

        [Fact]
        public void Authenticate_AfterWebChange_UsesTheStoredPassword()
        {
            var repo = new Repo(); var conn = new FakeConnection();
            var live = NewPlayer(repo, conn, "oldsecret");
            repo.Docs["p.holen"]["Password"] = "webpassword";
            var accounts = new PlayerAccounts(repo, null);
            Assert.Null(accounts.Authenticate("Holen", "oldsecret"));
            Assert.Same(live, accounts.Authenticate("Holen", "webpassword"));
        }
    }
}
