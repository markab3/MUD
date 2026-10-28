using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MUD.Core;
using MUD.Core.GameObjects;
using MUD.Core.Repositories;
using MUD.Core.Repositories.Interfaces;
using MUD.CoreApi;
using MUD.Telnet;
using Xunit;

namespace MUD.Tests
{
    /// <summary>
    /// The login half of <see cref="ICoreApi"/> as implemented by World: the throttle calls, Authenticate and
    /// CreatePlayer completing on the game thread, the bounded wait, and the online-names read. The game thread is
    /// stood in for by a helper thread that pumps the loop; Mongo is not used (fake repositories).
    /// </summary>
    [Collection("World construction")] // World registers BSON class maps; constructing it in parallel races
    public class CoreLoginApiTests : IDisposable
    {
        private class Quiet : Player
        {
            public Quiet(World world, IPlayerRepository repo) : base(world, new MUD.Core.Commands.CommandQueue(new FakeClock()), repo) { }
            public override void ReceiveMessage(string message) { }
        }

        private readonly World _world;
        private readonly SyncPlayerRepo _players = new SyncPlayerRepo();
        private readonly SyncRoomRepo _rooms = new SyncRoomRepo();
        private readonly FakeSink _sink = new FakeSink();
        private readonly SessionId _session;
        private readonly Quiet _ann;
        private Thread _pump;
        private volatile bool _pumping;

        public CoreLoginApiTests()
        {
            _world = new World(new MongoDB.Driver.MongoClient("mongodb://127.0.0.1:1"), new WorldSettings { StartRoomKey = "t.start" }, new FakeClock());
            var services = new ServiceCollection()
                .AddSingleton<IPlayerRepository>(_players)
                .AddSingleton<IRoomRepository>(_rooms)
                .BuildServiceProvider();
            typeof(World).GetField("_serviceProvider", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_world, services);

            var room = new Room(_rooms) { Id = "t.start", ShortDescription = "start", LongDescription = "start", Area = "t" };
            _rooms.Cache[room.Id] = room;
            _rooms.Stored[room.Id] = room.ToBsonDocument();

            _session = _world.OpenSession("test", "9.9.9.9", _sink);
            _ann = new Quiet(_world, _players) { Id = "p.ann", PlayerName = "Ann", NameKey = "ann", Password = "secret", ShortDescription = "Ann", Race = "human", CurrentLocationId = "t.start" };
            _players.Stored[_ann.Id] = _ann.ToBsonDocument();
            _players.Live.Add(_ann);
        }

        public void Dispose()
        {
            _pumping = false;
            if (_pump != null) { _pump.Join(); }
        }

        // Runs the game loop's work on a helper thread, as the real game thread would.
        private void StartGameThread()
        {
            var loop = (GameLoop)typeof(World).GetField("_loop", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_world);
            _pumping = true;
            _pump = new Thread(() => { while (_pumping) { loop.RunOnce(TimeSpan.FromMilliseconds(5)); } }) { IsBackground = true };
            _pump.Start();
        }

        // ---- the throttle lives in core ----

        [Fact]
        public void WithoutAThrottleNothingIsThrottledAndOutcomesAreIgnored()
        {
            _world.LoginThrottle = null;
            _world.RecordLoginOutcome("k", false);
            Assert.False(_world.IsThrottled("k"));
        }

        [Fact]
        public void FailedOutcomesBlockTheKeyAfterTheLimitAndSuccessClearsTheCount()
        {
            _world.LoginThrottle = new LoginThrottle(3, TimeSpan.FromMinutes(5));
            _world.RecordLoginOutcome("k", false);
            _world.RecordLoginOutcome("k", false);
            _world.RecordLoginOutcome("k", true); // clears
            _world.RecordLoginOutcome("k", false);
            _world.RecordLoginOutcome("k", false);
            Assert.False(_world.IsThrottled("k"));

            _world.RecordLoginOutcome("k", false);
            Assert.True(_world.IsThrottled("k"));
            Assert.False(_world.IsThrottled("other"));
        }

        [Fact]
        public void TheSameThrottleServesThePasswordChangeFlow()
        {
            _world.LoginThrottle = new LoginThrottle(2, TimeSpan.FromMinutes(5));
            _world.RecordLoginOutcome("5.6.7.8", false);
            _world.RecordLoginOutcome("5.6.7.8", false);
            Assert.True(_world.LoginThrottle.IsBlocked("5.6.7.8"));
        }

        // ---- Authenticate / CreatePlayer / GetOnlineNames complete on the game thread ----

        [Fact]
        public void AuthenticateRunsOnTheGameThreadAndReturnsSuccess()
        {
            StartGameThread();
            Assert.Equal(LoginResult.Success, _world.Authenticate(_session, "Ann", "secret"));
            Assert.Contains(_ann, _world.Players);
        }

        [Fact]
        public void AuthenticateWithAWrongPasswordReturnsBadCredentials()
        {
            StartGameThread();
            Assert.Equal(LoginResult.BadCredentials, _world.Authenticate(_session, "Ann", "wrong"));
            Assert.DoesNotContain(_ann, _world.Players);
        }

        [Fact]
        public void AuthenticateDoesNotTouchTheThrottle()
        {
            _world.LoginThrottle = new LoginThrottle(1, TimeSpan.FromMinutes(5));
            StartGameThread();
            _world.Authenticate(_session, "Ann", "wrong");
            Assert.False(_world.IsThrottled("9.9.9.9")); // the front end records the outcome
        }

        [Fact]
        public void AuthenticateForARetiredCharacterReturnsRetiredAndTheSinkIsTold()
        {
            _players.Stored[_ann.Id]["Retired"] = true;
            StartGameThread();
            Assert.Equal(LoginResult.Retired, _world.Authenticate(_session, "Ann", "secret"));
            Assert.Contains(PlayerAccounts.RetiredMessage, _sink.Sent);
        }

        [Fact]
        public void ASecondSessionLoggingInTakesTheAccountOverAndGetsAlreadyOnline()
        {
            StartGameThread();
            Assert.Equal(LoginResult.Success, _world.Authenticate(_session, "Ann", "secret"));
            var secondSink = new FakeSink();
            SessionId second = _world.OpenSession("test", "9.9.9.9", secondSink);

            Assert.Equal(LoginResult.AlreadyOnline, _world.Authenticate(second, "Ann", "secret"));
            Assert.Equal(second, _ann.SessionId);
        }

        [Fact]
        public void GetOnlineNamesListsLoggedInPlayersReadOnTheGameThread()
        {
            StartGameThread();
            Assert.Empty(_world.GetOnlineNames());
            _world.Authenticate(_session, "Ann", "secret");
            Assert.Equal(new[] { "Ann" }, _world.GetOnlineNames().ToArray());
        }

        [Fact]
        public void CreatePlayerWithAnExistingNameReturnsNameTakenAndSendsNothing()
        {
            StartGameThread();
            _sink.Events.Clear();
            Assert.Equal(LoginResult.NameTaken, _world.CreatePlayer(_session, "ann", "pw"));
            Assert.Empty(_sink.Events);
        }

        [Fact]
        public void AWaitThatRunsOutReturnsTryAgainLaterAndTheLoginNeverHappensLater()
        {
            _world.ApiCallTimeout = TimeSpan.FromMilliseconds(100);
            // No game thread is running yet: the request waits in the queue and the wait runs out.
            Assert.Equal(LoginResult.TryAgainLater, _world.Authenticate(_session, "Ann", "secret"));

            StartGameThread();
            Assert.Empty(_world.GetOnlineNames()); // the abandoned request was dropped, not run late
            Assert.DoesNotContain(_ann, _world.Players);
        }

        // ---- the pre-login dialogue calls the API ----

        // A scripted core for the telnet-side dialogue: who is online, and how many logins it was asked for.
        private class ScriptedCore : ICoreApi
        {
            public List<string> Online = new List<string>();
            public int Authentications;
            public SessionId OpenSession(string frontEnd, string remoteKey, IFrontEndSink sink) { return new SessionId("s"); }
            public void SubmitLine(SessionId id, string rawLine) { }
            public void CloseSession(SessionId id, SessionEndReason reason) { }
            public LoginResult Authenticate(SessionId id, string name, string password) { Authentications++; return LoginResult.BadCredentials; }
            public LoginResult CreatePlayer(SessionId id, string name, string password) { return LoginResult.NameTaken; }
            public bool IsThrottled(string remoteKey) { return false; }
            public void RecordLoginOutcome(string remoteKey, bool success) { }
            public IReadOnlyList<string> GetOnlineNames() { return Online; }
        }

        private class RecordingConnection : ILoginConnection
        {
            public readonly List<string> Sent = new List<string>();
            public bool Disconnected;
            public string RemoteKey { get { return "1.1.1.1"; } }
            public void Send(string text) { Sent.Add(text); }
            public void SendPrompt(string text) { Sent.Add("prompt:" + text); }
            public void SetEchoHidden(bool hidden) { }
            public void DisconnectWithMessage(string message) { Disconnected = true; }
            public void Disconnect(bool flush) { Disconnected = true; }
        }

        [Theory]
        [InlineData(0, "No one is logged on.")]
        [InlineData(1, "Ann is logged on.")]
        [InlineData(2, "Ann and Bob are logged on.")]
        [InlineData(3, "Ann, Bob and Cy are logged on.")]
        public void WhoListsThePlayersFromTheApi(int count, string expected)
        {
            var core = new ScriptedCore();
            core.Online.AddRange(new[] { "Ann", "Bob", "Cy" }.Take(count));
            var connection = new RecordingConnection();
            new PreLoginHandler(core, new SessionId("s"), connection, s => { }).HandleLine("who");
            Assert.Equal(new[] { expected }, connection.Sent.ToArray());
        }

        [Fact]
        public void QuitDisconnectsAndAnUnknownCommandIsAnsweredWithTheWholeLine()
        {
            var connection = new RecordingConnection();
            var handler = new PreLoginHandler(new ScriptedCore(), new SessionId("s"), connection, s => { });
            handler.HandleLine("dance wildly");
            Assert.Equal(new[] { "Command dance wildly was not recognized." }, connection.Sent.ToArray());
            handler.HandleLine("quit");
            Assert.True(connection.Disconnected);
        }

        [Fact]
        public void PasswordsNeverReachTheLogLines()
        {
            var logged = new List<string>();
            var connection = new RecordingConnection();
            var handler = new PreLoginHandler(new ScriptedCore(), new SessionId("s"), connection, logged.Add);
            handler.HandleLine("connect Alice hunter2");
            handler.HandleLine("connect");
            handler.HandleLine("Alice");
            handler.HandleLine("hunter3");
            Assert.DoesNotContain(logged, line => line.Contains("hunter"));
            Assert.Contains("MESSAGE: connect Alice <hidden>", logged);
        }
    }
}
