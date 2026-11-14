using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MUD.Contracts;
using MUD.Core.Modules;
using MUD.Core.Sessions;
using MUD.CoreApi;
using MUD.CoreApi.Client;
using MUD.Main;
using MUD.Telnet;
using MUD.Telnet.Host;
using Xunit;

namespace MUD.Tests
{
    /// <summary>
    /// Card P7-2: the telnet host's settings and its reconnect-with-backoff to core. The reconnect logic is tested over a fake connection;
    /// one test runs it against the real Session service on frontends.sock with a fake core behind it. Mongo is not used.
    /// </summary>
    public class TelnetHostTests : IDisposable
    {
        private readonly string _directory;
        private readonly List<string> _log = new List<string>();

        public TelnetHostTests()
        {
            // Short path: a Unix socket path is limited to 107 bytes.
            _directory = Path.Combine(Path.GetTempPath(), "th" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_directory);
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, true); } catch (Exception) { }
        }

        private static readonly TimeSpan[] FastBackoff = { TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20) };

        private static async Task<bool> WaitFor(Func<bool> condition, int milliseconds = 5000)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < end)
            {
                if (condition()) { return true; }
                await Task.Delay(10);
            }
            return condition();
        }

        // ---- options ----

        [Fact]
        public void OptionsDefaultToAnyAddressPort7680AndTheStandardPaths()
        {
            TelnetHostOptions options = TelnetHostOptions.FromEnvironment(name => null, "/home/someone");
            Assert.Equal(System.Net.IPAddress.Any, options.BindAddress);
            Assert.Equal(7680, options.Port);
            Assert.Equal("/home/someone/.local/share/mud/run/frontends.sock", options.SocketPath);
            Assert.Equal("/home/someone/.config/mud/frontend-token", options.TokenFilePath);
        }

        [Fact]
        public void OptionsFollowTheRunDirectoryRuleOfCore()
        {
            var variables = new Dictionary<string, string> { { "XDG_RUNTIME_DIR", "/run/user/1000" } };
            Assert.Equal("/run/user/1000/mud/frontends.sock", TelnetHostOptions.FromEnvironment(name => variables.GetValueOrDefault(name), "/home/someone").SocketPath);
            variables["MUD_RUN_DIRECTORY"] = "/tmp/mudrun";
            Assert.Equal("/tmp/mudrun/frontends.sock", TelnetHostOptions.FromEnvironment(name => variables.GetValueOrDefault(name), "/home/someone").SocketPath);
        }

        [Fact]
        public void OptionsReadAddressPortTokenFileAndTelnetLimitsFromTheEnvironment()
        {
            var variables = new Dictionary<string, string>
            {
                { "MUD_TELNET_BIND_ADDRESS", "127.0.0.1" }, { "MUD_TELNET_PORT", "4000" },
                { "MUD_FRONTEND_TOKEN_FILE", "/tmp/token" }, { "MUD_MAX_LINE_LENGTH", "77" }
            };
            TelnetHostOptions options = TelnetHostOptions.FromEnvironment(name => variables.GetValueOrDefault(name), "/home/someone");
            Assert.Equal(System.Net.IPAddress.Loopback, options.BindAddress);
            Assert.Equal(4000, options.Port);
            Assert.Equal("/tmp/token", options.TokenFilePath);
            Assert.Equal(77, options.Limits.MaxLineLength);
        }

        [Fact]
        public void AnInvalidPortOrAddressFallsBackToTheDefault()
        {
            var variables = new Dictionary<string, string> { { "MUD_TELNET_BIND_ADDRESS", "nonsense" }, { "MUD_TELNET_PORT", "70000" } };
            TelnetHostOptions options = TelnetHostOptions.FromEnvironment(name => variables.GetValueOrDefault(name), "/home/someone");
            Assert.Equal(System.Net.IPAddress.Any, options.BindAddress);
            Assert.Equal(7680, options.Port);
        }

        // ---- reconnect over a fake connection ----

        private sealed class FakeConnection : ICoreConnection
        {
            private readonly Func<string> _connect;
            public readonly List<string> Calls = new List<string>();
            public bool Disposed;

            public FakeConnection(Func<string> connect) { _connect = connect; }

            public event Action<string> ConnectionLost;
            public void Lose(string reason) { ConnectionLost?.Invoke(reason); }
            public string Connect() { return _connect(); }
            public SessionId OpenSession(string frontEnd, string remoteKey, IFrontEndSink sink) { Calls.Add("open:" + remoteKey); return new SessionId("s1"); }
            public void SubmitLine(SessionId id, string rawLine) { Calls.Add("line:" + rawLine); }
            public void CloseSession(SessionId id, SessionEndReason reason) { Calls.Add("close:" + reason); }
            public LoginResult Authenticate(SessionId id, string name, string password) { return LoginResult.Success; }
            public LoginResult CreatePlayer(SessionId id, string name, string password) { return LoginResult.Success; }
            public bool IsThrottled(string remoteKey) { return true; }
            public void RecordLoginOutcome(string remoteKey, bool success) { Calls.Add("outcome:" + success); }
            public IReadOnlyList<string> GetOnlineNames() { return new List<string> { "Ann" }; }
            public void Dispose() { Disposed = true; }
        }

        private sealed class EndedSink : IFrontEndSink
        {
            public readonly List<SessionEndReason> Ended = new List<SessionEndReason>();
            public void Output(SessionId id, OutputChannel channel, string markupText, bool flush) { }
            public void SetEchoHidden(SessionId id, bool hidden) { }
            public void Prompt(SessionId id, string text) { }
            public void Close(SessionId id, string farewellMarkup, bool flush) { }
            public void SessionEnded(SessionId id, SessionEndReason reason) { lock (Ended) { Ended.Add(reason); } }
        }

        [Fact]
        public async Task ItRetriesAfterAFailedConnectUntilCoreIsThere()
        {
            int attempts = 0;
            var connections = new List<FakeConnection>();
            using (var api = new ReconnectingCoreApi(() =>
            {
                var connection = new FakeConnection(() => Interlocked.Increment(ref attempts) < 3 ? "core is not reachable" : null);
                lock (connections) { connections.Add(connection); }
                return connection;
            }, _log.Add, FastBackoff))
            {
                api.Start();
                Assert.True(await WaitFor(() => api.IsConnected));
                Assert.Equal(3, attempts);
                Assert.True(connections[0].Disposed);
                Assert.True(connections[1].Disposed);
                Assert.False(connections[2].Disposed);
            }
        }

        [Fact]
        public async Task ItConnectsAgainAfterCoreIsLostAndPassesCallsToTheNewConnection()
        {
            var connections = new List<FakeConnection>();
            int connected = 0;
            using (var api = new ReconnectingCoreApi(() =>
            {
                var connection = new FakeConnection(() => null);
                lock (connections) { connections.Add(connection); }
                return connection;
            }, _log.Add, FastBackoff))
            {
                api.Connected += () => Interlocked.Increment(ref connected);
                api.Start();
                Assert.True(await WaitFor(() => api.IsConnected));
                api.SubmitLine(new SessionId("s1"), "first");
                connections[0].Lose("core stopped");
                Assert.True(await WaitFor(() => connected == 2 && api.IsConnected));
                api.SubmitLine(new SessionId("s2"), "second");
                Assert.Equal(new[] { "line:first" }, connections[0].Calls);
                Assert.Equal(new[] { "line:second" }, connections[1].Calls);
                Assert.True(connections[0].Disposed);
            }
        }

        [Fact]
        public async Task WhileCoreIsLostANewSessionIsToldTheWorldIsRestartingAndOtherCallsFailSoft()
        {
            var connections = new List<FakeConnection>();
            int calls = 0;
            using (var api = new ReconnectingCoreApi(() =>
            {
                var connection = new FakeConnection(() => Interlocked.Increment(ref calls) == 1 ? null : "core is not reachable");
                lock (connections) { connections.Add(connection); }
                return connection;
            }, _log.Add, FastBackoff))
            {
                api.Start();
                Assert.True(await WaitFor(() => api.IsConnected));
                connections[0].Lose("core stopped");
                Assert.True(await WaitFor(() => !api.IsConnected));

                var sink = new EndedSink();
                Assert.True(api.OpenSession("telnet", "10.0.0.1", sink).IsEmpty);
                Assert.Equal(new[] { SessionEndReason.WorldStopping }, sink.Ended);
                Assert.Equal(LoginResult.TryAgainLater, api.Authenticate(new SessionId("x"), "a", "b"));
                Assert.Equal(LoginResult.TryAgainLater, api.CreatePlayer(new SessionId("x"), "a", "b"));
                Assert.False(api.IsThrottled("10.0.0.1"));
                Assert.Empty(api.GetOnlineNames());
                api.SubmitLine(new SessionId("x"), "look");
                api.CloseSession(new SessionId("x"), SessionEndReason.ClientClosed);
                api.RecordLoginOutcome("10.0.0.1", false);
            }
        }

        [Fact]
        public async Task TheWaitGrowsThroughTheBackoffListAndRepeatsTheLastValue()
        {
            var times = new List<DateTime>();
            using (var api = new ReconnectingCoreApi(() =>
            {
                lock (times) { times.Add(DateTime.UtcNow); }
                return new FakeConnection(() => "core is not reachable");
            }, _log.Add, new[] { TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(120) }))
            {
                api.Start();
                Assert.True(await WaitFor(() => { lock (times) { return times.Count >= 4; } }));
            }
            lock (times)
            {
                Assert.True(times[1] - times[0] >= TimeSpan.FromMilliseconds(35));
                Assert.True(times[2] - times[1] >= TimeSpan.FromMilliseconds(110));
                Assert.True(times[3] - times[2] >= TimeSpan.FromMilliseconds(110));
            }
        }

        [Fact]
        public async Task AConnectionThatThrowsIsTreatedAsAFailedAttempt()
        {
            int attempts = 0;
            using (var api = new ReconnectingCoreApi(() =>
            {
                if (Interlocked.Increment(ref attempts) == 1) { throw new InvalidOperationException("token file missing"); }
                return new FakeConnection(() => null);
            }, _log.Add, FastBackoff))
            {
                api.Start();
                Assert.True(await WaitFor(() => api.IsConnected));
                Assert.Equal(2, attempts);
            }
        }

        // ---- against the real Session service ----

        private sealed class FakeCore : ICoreApi
        {
            public readonly Dictionary<string, IFrontEndSink> Sinks = new Dictionary<string, IFrontEndSink>();
            public int Opened;
            public SessionId OpenSession(string frontEnd, string remoteKey, IFrontEndSink sink)
            {
                string id = "session" + Interlocked.Increment(ref Opened);
                lock (Sinks) { Sinks[id] = sink; }
                return new SessionId(id);
            }
            public void SubmitLine(SessionId id, string rawLine) { }
            public void CloseSession(SessionId id, SessionEndReason reason) { }
            public LoginResult Authenticate(SessionId id, string name, string password) { return LoginResult.Success; }
            public LoginResult CreatePlayer(SessionId id, string name, string password) { return LoginResult.Success; }
            public bool IsThrottled(string remoteKey) { return false; }
            public void RecordLoginOutcome(string remoteKey, bool success) { }
            public IReadOnlyList<string> GetOnlineNames() { return new List<string> { "Ann" }; }
        }

        private ModuleHostRunner StartCoreSide(RunDirectory directory, FakeCore core, string tokenPath)
        {
            string grants = Path.Combine(_directory, "grants.yaml");
            if (!File.Exists(grants))
            {
                File.WriteAllText(grants, "modules: {}\n");
                File.SetUnixFileMode(grants, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            var moduleGrants = new ModuleGrants(grants, _log.Add);
            Assert.True(moduleGrants.Reload());
            var registry = new ModuleRegistry(new ModuleTokenRegistry(), moduleGrants, ContractVersion.Current, new ModuleHostOptions(), _log.Add);
            var tokenFile = FrontEndTokenFile.LoadOrCreate(tokenPath, _log.Add);
            var frontEnds = new FrontEndRegistry(core, tokenFile, ContractVersion.Current, FrontEndRegistry.DefaultOutboundQueueCapacity, _log.Add);
            var runner = new ModuleHostRunner(directory, registry, action => false, _log.Add, null, null, frontEnds);
            runner.Start();
            return runner;
        }

        [Fact]
        public async Task TheHostEndsOpenSessionsWhenCoreStopsAndConnectsAgainWhenCoreIsBack()
        {
            string tokenPath = Path.Combine(_directory, "token");
            File.WriteAllText(tokenPath, "host-test-token\n");
            File.SetUnixFileMode(tokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            string runPath = Path.Combine(_directory, "r");

            var core = new FakeCore();
            RunDirectory directory = RunDirectory.Acquire(runPath, _log.Add);
            ModuleHostRunner runner = StartCoreSide(directory, core, tokenPath);
            string socketPath = directory.FrontEndsSocketPath;

            Func<ICoreConnection> factory = () => new GrpcCoreConnection(new GrpcCoreApi(GrpcCoreApi.OpenChannel(socketPath), "telnet", "host-test-token", _log.Add, TimeSpan.FromSeconds(3)));
            using (var api = new ReconnectingCoreApi(factory, _log.Add, FastBackoff))
            {
                api.Start();
                Assert.True(await WaitFor(() => api.IsConnected));
                var sink = new EndedSink();
                Assert.False(api.OpenSession("telnet", "10.0.0.1", sink).IsEmpty);

                // Core goes away: the open session is ended with WorldStopping (which the telnet sink turns into "world is restarting").
                runner.Dispose();
                directory.Dispose();
                Assert.True(await WaitFor(() => { lock (sink.Ended) { return sink.Ended.Contains(SessionEndReason.WorldStopping); } }, 10000));
                Assert.True(await WaitFor(() => !api.IsConnected));

                // Core comes back on the same path: the host connects again and a new session works.
                RunDirectory directoryAgain = RunDirectory.Acquire(runPath, _log.Add);
                ModuleHostRunner runnerAgain = StartCoreSide(directoryAgain, core, tokenPath);
                try
                {
                    Assert.True(await WaitFor(() => api.IsConnected, 15000));
                    Assert.False(api.OpenSession("telnet", "10.0.0.2", new EndedSink()).IsEmpty);
                    Assert.Equal(new[] { "Ann" }, api.GetOnlineNames());
                }
                finally
                {
                    runnerAgain.Dispose();
                    directoryAgain.Dispose();
                }
            }
        }
    }
}
