using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using MUD.Contracts;
using MUD.Core.Modules;
using MUD.Core.Sessions;
using MUD.CoreApi;
using MUD.CoreApi.Client;
using MUD.Main;
using Xunit;

namespace MUD.Tests
{
    /// <summary>
    /// Card P7-1: the Session service on frontends.sock over the real Kestrel host, and the gRPC implementation of ICoreApi against it.
    /// Core's side is a recording fake ICoreApi, so no database and no game thread are needed. Mongo is not used.
    /// </summary>
    public class SessionServiceTests : IDisposable
    {
        private const string Token = "test-front-end-token";

        private readonly string _directory;
        private readonly List<string> _log = new List<string>();

        public SessionServiceTests()
        {
            // Short path: a Unix socket path is limited to 107 bytes.
            _directory = Path.Combine(Path.GetTempPath(), "ss" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_directory);
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, true); } catch (Exception) { }
        }

        // ---- fakes ----

        /// <summary>Core's side: records calls, hands out ids, and keeps each session's sink so a test can make core send events.</summary>
        private sealed class FakeCore : ICoreApi
        {
            public readonly Dictionary<string, IFrontEndSink> Sinks = new Dictionary<string, IFrontEndSink>();
            public readonly List<string> Calls = new List<string>();
            public readonly List<string> Lines = new List<string>();
            public readonly HashSet<string> Throttled = new HashSet<string>();
            public LoginResult NextLogin = LoginResult.Success;
            public int Opened;

            public SessionId OpenSession(string frontEnd, string remoteKey, IFrontEndSink sink)
            {
                string id = "session" + Interlocked.Increment(ref Opened);
                lock (Calls) { Sinks[id] = sink; Calls.Add("open:" + frontEnd + ":" + remoteKey); }
                return new SessionId(id);
            }

            public void SubmitLine(SessionId id, string rawLine) { lock (Calls) { Lines.Add(id.Value + ":" + rawLine); } }

            public void CloseSession(SessionId id, SessionEndReason reason) { lock (Calls) { Calls.Add("close:" + id.Value + ":" + reason); Sinks.Remove(id.Value); } }

            public LoginResult Authenticate(SessionId id, string name, string password) { lock (Calls) { Calls.Add("auth:" + id.Value + ":" + name + ":" + password); } return NextLogin; }

            public LoginResult CreatePlayer(SessionId id, string name, string password) { lock (Calls) { Calls.Add("create:" + id.Value + ":" + name); } return NextLogin; }

            public bool IsThrottled(string remoteKey) { return Throttled.Contains(remoteKey); }

            public void RecordLoginOutcome(string remoteKey, bool success) { lock (Calls) { Calls.Add("outcome:" + remoteKey + ":" + success); } }

            public IReadOnlyList<string> GetOnlineNames() { return new List<string> { "Ann", "Bob" }; }

            public List<string> CallsSnapshot() { lock (Calls) { return new List<string>(Calls); } }
        }

        /// <summary>The client's sink: records events in order.</summary>
        private sealed class RecordingSink : IFrontEndSink
        {
            public readonly List<string> Events = new List<string>();

            public void Output(SessionId id, OutputChannel channel, string markupText, bool flush) { Add("output:" + channel + ":" + markupText + ":" + flush); }
            public void SetEchoHidden(SessionId id, bool hidden) { Add("echo:" + hidden); }
            public void Prompt(SessionId id, string text) { Add("prompt:" + text); }
            public void Close(SessionId id, string farewellMarkup, bool flush) { Add("close:" + farewellMarkup + ":" + flush); }
            public void SetTerminal(SessionId id, string terminalName) { Add("terminal:" + terminalName); }
            public void SessionEnded(SessionId id, SessionEndReason reason) { Add("ended:" + reason); }

            private void Add(string text) { lock (Events) { Events.Add(text); } }
            public List<string> Snapshot() { lock (Events) { return new List<string>(Events); } }
        }

        private sealed class Host : IDisposable
        {
            public RunDirectory Directory;
            public ModuleHostRunner Runner;
            public FrontEndRegistry FrontEnds;
            public FakeCore Core;

            public void Dispose()
            {
                Runner.Dispose();
                Directory.Dispose();
            }
        }

        private Host StartHost(int queueCapacity = FrontEndRegistry.DefaultOutboundQueueCapacity)
        {
            string grants = Path.Combine(_directory, "grants.yaml");
            File.WriteAllText(grants, "modules: {}\n");
            File.SetUnixFileMode(grants, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var moduleGrants = new ModuleGrants(grants, _log.Add);
            Assert.True(moduleGrants.Reload());
            var registry = new ModuleRegistry(new ModuleTokenRegistry(), moduleGrants, ContractVersion.Current, new ModuleHostOptions(), _log.Add);

            string tokenPath = Path.Combine(_directory, "token");
            File.WriteAllText(tokenPath, Token + "\n");
            File.SetUnixFileMode(tokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var tokenFile = FrontEndTokenFile.LoadOrCreate(tokenPath, _log.Add);

            var core = new FakeCore();
            var frontEnds = new FrontEndRegistry(core, tokenFile, ContractVersion.Current, queueCapacity, _log.Add);
            RunDirectory directory = RunDirectory.Acquire(Path.Combine(_directory, "r"), _log.Add);
            var runner = new ModuleHostRunner(directory, registry, action => false, _log.Add, null, null, frontEnds);
            runner.Start();
            return new Host { Directory = directory, Runner = runner, FrontEnds = frontEnds, Core = core };
        }

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

        private GrpcCoreApi NewClient(Host host, string token = Token)
        {
            return new GrpcCoreApi(GrpcCoreApi.OpenChannel(host.Directory.FrontEndsSocketPath), "telnet", token, _log.Add, TimeSpan.FromSeconds(5));
        }

        // ---- Connect: token and contract version ----

        [Fact]
        public void ConnectWithTheRightTokenAndVersionIsAccepted()
        {
            using (Host host = StartHost())
            using (GrpcCoreApi client = NewClient(host))
            {
                Assert.Null(client.Connect());
                Assert.True(client.IsConnected);
                Assert.Equal(1, host.FrontEnds.ConnectionCount);
            }
        }

        [Fact]
        public void ConnectIsRefusedWhileCoreIsNotOpenToFrontEndsAndAcceptedOnceItIs()
        {
            using (Host host = StartHost())
            using (GrpcCoreApi client = NewClient(host))
            {
                host.FrontEnds.AcceptingConnections = false;
                Assert.NotNull(client.Connect());
                Assert.Equal(0, host.FrontEnds.ConnectionCount);

                host.FrontEnds.AcceptingConnections = true;
                Assert.Null(client.Connect());
                Assert.Equal(1, host.FrontEnds.ConnectionCount);
            }
        }

        [Fact]
        public void ConnectWithAWrongTokenIsRefusedAndTheTokenIsNeverLogged()
        {
            using (Host host = StartHost())
            using (GrpcCoreApi client = NewClient(host, "not-the-token"))
            {
                string refusal = client.Connect();
                Assert.NotNull(refusal);
                Assert.Contains("token", refusal);
                Assert.False(client.IsConnected);
                Assert.Equal(0, host.FrontEnds.ConnectionCount);
                Assert.DoesNotContain(_log, line => line.Contains("not-the-token") || line.Contains(Token));
            }
        }

        [Fact]
        public void ConnectWithAnEmptyTokenIsRefused()
        {
            using (Host host = StartHost())
            using (GrpcCoreApi client = NewClient(host, ""))
            {
                Assert.NotNull(client.Connect());
            }
        }

        [Fact]
        public void ConnectWithAContractVersionMismatchIsRefusedAndBothNumbersAreLogged()
        {
            using (Host host = StartHost())
            {
                FrontEndWelcome welcome = host.FrontEnds.Connect(new FrontEndHello { Name = "telnet", Token = Token, ContractVersion = ContractVersion.Current + 5 });
                Assert.False(welcome.Ok);
                Assert.Contains((ContractVersion.Current + 5).ToString(), welcome.Refusal);
                Assert.Contains(ContractVersion.Current.ToString(), welcome.Refusal);
                Assert.Contains(_log, line => line.Contains((ContractVersion.Current + 5).ToString()) && line.Contains(ContractVersion.Current.ToString()));
            }
        }

        [Fact]
        public void ConnectWithoutANameIsRefused()
        {
            using (Host host = StartHost())
            {
                Assert.False(host.FrontEnds.Connect(new FrontEndHello { Name = "  ", Token = Token, ContractVersion = ContractVersion.Current }).Ok);
            }
        }

        [Fact]
        public async Task CallsWithAnUnknownConnectionAreUnauthenticated()
        {
            using (Host host = StartHost())
            using (GrpcChannel channel = GrpcCoreApi.OpenChannel(host.Directory.FrontEndsSocketPath))
            {
                var client = new Session.SessionClient(channel);
                RpcException open = await Assert.ThrowsAsync<RpcException>(async () => await client.OpenSessionAsync(new OpenSessionRequest { ConnectionId = "nope" }));
                Assert.Equal(StatusCode.Unauthenticated, open.StatusCode);
                RpcException line = await Assert.ThrowsAsync<RpcException>(async () => await client.LineAsync(new LineRequest { ConnectionId = "nope", SessionId = "x", Line = "look" }));
                Assert.Equal(StatusCode.Unauthenticated, line.StatusCode);
                RpcException names = await Assert.ThrowsAsync<RpcException>(async () => await client.GetOnlineNamesAsync(new OnlineNamesRequest { ConnectionId = "nope" }));
                Assert.Equal(StatusCode.Unauthenticated, names.StatusCode);
            }
        }

        [Fact]
        public async Task ASessionOfAnotherConnectionCannotBeUsed()
        {
            using (Host host = StartHost())
            using (GrpcCoreApi first = NewClient(host))
            {
                Assert.Null(first.Connect());
                SessionId id = first.OpenSession("telnet", "1.1.1.1", new RecordingSink());
                Assert.False(id.IsEmpty);

                // A second connection (another name) presents the first one's session id.
                FrontEndWelcome other = host.FrontEnds.Connect(new FrontEndHello { Name = "web", Token = Token, ContractVersion = ContractVersion.Current });
                using (GrpcChannel channel = GrpcCoreApi.OpenChannel(host.Directory.FrontEndsSocketPath))
                {
                    var raw = new Session.SessionClient(channel);
                    RpcException denied = await Assert.ThrowsAsync<RpcException>(async () =>
                        await raw.LineAsync(new LineRequest { ConnectionId = other.ConnectionId, SessionId = id.Value, Line = "look" }));
                    Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
                }
                Assert.Empty(host.Core.Lines);
            }
        }

        // ---- the calls, through the real client ----

        [Fact]
        public async Task OpenSessionLineAndCloseReachCoreInOrder()
        {
            using (Host host = StartHost())
            using (GrpcCoreApi client = NewClient(host))
            {
                Assert.Null(client.Connect());
                SessionId id = client.OpenSession("telnet", "10.0.0.7", new RecordingSink());
                Assert.False(id.IsEmpty);
                Assert.Equal("session1", id.Value);
                Assert.Contains("open:telnet:10.0.0.7", host.Core.CallsSnapshot());

                for (int index = 0; index < 20; index++) { client.SubmitLine(id, "line " + index); }
                Assert.True(await WaitFor(() => { lock (host.Core.Calls) { return host.Core.Lines.Count == 20; } }));
                lock (host.Core.Calls)
                {
                    Assert.Equal(Enumerable.Range(0, 20).Select(index => "session1:line " + index), host.Core.Lines);
                }

                client.CloseSession(id, SessionEndReason.Idle);
                Assert.True(await WaitFor(() => host.Core.CallsSnapshot().Contains("close:session1:Idle")));
            }
        }

        [Theory]
        [InlineData(LoginResult.Success)]
        [InlineData(LoginResult.BadCredentials)]
        [InlineData(LoginResult.Retired)]
        [InlineData(LoginResult.Throttled)]
        [InlineData(LoginResult.AlreadyOnline)]
        [InlineData(LoginResult.NameTaken)]
        [InlineData(LoginResult.TryAgainLater)]
        public void AuthenticateAndCreatePlayerCarryEveryResult(LoginResult result)
        {
            using (Host host = StartHost())
            using (GrpcCoreApi client = NewClient(host))
            {
                Assert.Null(client.Connect());
                SessionId id = client.OpenSession("telnet", "k", new RecordingSink());
                host.Core.NextLogin = result;
                Assert.Equal(result, client.Authenticate(id, "Ann", "secret"));
                Assert.Equal(result, client.CreatePlayer(id, "Ann", "secret"));
                List<string> calls = host.Core.CallsSnapshot();
                Assert.Contains("auth:session1:Ann:secret", calls);
                Assert.Contains("create:session1:Ann", calls);
            }
        }

        [Fact]
        public void ThePasswordIsNotWrittenToTheLog()
        {
            using (Host host = StartHost())
            using (GrpcCoreApi client = NewClient(host))
            {
                Assert.Null(client.Connect());
                SessionId id = client.OpenSession("telnet", "k", new RecordingSink());
                client.Authenticate(id, "Ann", "hunter2-secret");
                Assert.DoesNotContain(_log, line => line.Contains("hunter2-secret"));
            }
        }

        [Fact]
        public async Task TheThrottleOutcomeAndTheOnlineNamesWork()
        {
            using (Host host = StartHost())
            using (GrpcCoreApi client = NewClient(host))
            {
                Assert.Null(client.Connect());
                host.Core.Throttled.Add("bad.address");
                Assert.True(client.IsThrottled("bad.address"));
                Assert.False(client.IsThrottled("good.address"));

                client.RecordLoginOutcome("bad.address", false);
                client.RecordLoginOutcome("bad.address", true);
                Assert.True(await WaitFor(() => host.Core.CallsSnapshot().Contains("outcome:bad.address:True")));
                List<string> calls = host.Core.CallsSnapshot();
                Assert.True(calls.IndexOf("outcome:bad.address:False") < calls.IndexOf("outcome:bad.address:True"));

                Assert.Equal(new[] { "Ann", "Bob" }, client.GetOnlineNames());
            }
        }

        // ---- the event stream ----

        [Fact]
        public async Task EveryEventKindReachesTheSinkInOrderWithItsChannel()
        {
            using (Host host = StartHost())
            using (GrpcCoreApi client = NewClient(host))
            {
                Assert.Null(client.Connect());
                var sink = new RecordingSink();
                SessionId id = client.OpenSession("telnet", "k", sink);
                IFrontEndSink fromCore = host.Core.Sinks[id.Value];

                fromCore.Output(id, OutputChannel.Say, "You say hi.", false);
                fromCore.Output(id, OutputChannel.Tell, "Bob tells you hi.", false);
                fromCore.Output(id, OutputChannel.Room, "A room.", false);
                fromCore.Output(id, OutputChannel.Combat, "Ouch.", false);
                fromCore.Output(id, OutputChannel.System, "System text.", false);
                fromCore.Output(id, OutputChannel.Chat, "[chat] hello", true);
                fromCore.SetEchoHidden(id, true);
                fromCore.Prompt(id, "Password: ");
                fromCore.SetTerminal(id, "ansi");
                fromCore.Close(id, "Goodbye.", true);
                fromCore.SessionEnded(id, SessionEndReason.Quit);

                Assert.True(await WaitFor(() => sink.Snapshot().Count == 11));
                Assert.Equal(new[]
                {
                    "output:Say:You say hi.:False",
                    "output:Tell:Bob tells you hi.:False",
                    "output:Room:A room.:False",
                    "output:Combat:Ouch.:False",
                    "output:System:System text.:False",
                    "output:Chat:[chat] hello:True",
                    "echo:True",
                    "prompt:Password: ",
                    "terminal:ansi",
                    "close:Goodbye.:True",
                    "ended:Quit"
                }, sink.Snapshot());
            }
        }

        [Fact]
        public async Task EventsOfTwoSessionsGoToTheirOwnSinks()
        {
            using (Host host = StartHost())
            using (GrpcCoreApi client = NewClient(host))
            {
                Assert.Null(client.Connect());
                var sinkA = new RecordingSink();
                var sinkB = new RecordingSink();
                SessionId a = client.OpenSession("telnet", "a", sinkA);
                SessionId b = client.OpenSession("telnet", "b", sinkB);
                host.Core.Sinks[a.Value].Output(a, OutputChannel.System, "for a", false);
                host.Core.Sinks[b.Value].Output(b, OutputChannel.System, "for b", false);
                Assert.True(await WaitFor(() => sinkA.Snapshot().Count == 1 && sinkB.Snapshot().Count == 1));
                Assert.Equal("output:System:for a:False", sinkA.Snapshot()[0]);
                Assert.Equal("output:System:for b:False", sinkB.Snapshot()[0]);
            }
        }

        [Fact]
        public async Task TheSinkOfAClosedSessionGetsNothingMore()
        {
            using (Host host = StartHost())
            using (GrpcCoreApi client = NewClient(host))
            {
                Assert.Null(client.Connect());
                var sink = new RecordingSink();
                SessionId id = client.OpenSession("telnet", "k", sink);
                IFrontEndSink fromCore = host.Core.Sinks[id.Value];
                client.CloseSession(id, SessionEndReason.ClientClosed);
                fromCore.Output(id, OutputChannel.System, "late", false);
                await Task.Delay(200);
                Assert.Empty(sink.Snapshot());
                Assert.Equal(0, client.OpenSessionCount);
            }
        }

        [Fact]
        public async Task ASinkThatThrowsDoesNotStopTheStream()
        {
            using (Host host = StartHost())
            using (GrpcCoreApi client = NewClient(host))
            {
                Assert.Null(client.Connect());
                SessionId bad = client.OpenSession("telnet", "bad", new ThrowingSink());
                var good = new RecordingSink();
                SessionId ok = client.OpenSession("telnet", "ok", good);
                host.Core.Sinks[bad.Value].Output(bad, OutputChannel.System, "boom", false);
                host.Core.Sinks[ok.Value].Output(ok, OutputChannel.System, "fine", false);
                Assert.True(await WaitFor(() => good.Snapshot().Count == 1));
                Assert.True(client.IsConnected);
            }
        }

        private sealed class ThrowingSink : IFrontEndSink
        {
            public void Output(SessionId id, OutputChannel channel, string markupText, bool flush) { throw new InvalidOperationException("sink failed"); }
            public void SetEchoHidden(SessionId id, bool hidden) { }
            public void Prompt(SessionId id, string text) { }
            public void Close(SessionId id, string farewellMarkup, bool flush) { }
            public void SessionEnded(SessionId id, SessionEndReason reason) { }
        }

        // ---- losing the front end, losing core ----

        [Fact]
        public async Task AFrontEndThatDisconnectsHasItsSessionsClosedInCore()
        {
            using (Host host = StartHost())
            {
                var client = NewClient(host);
                Assert.Null(client.Connect());
                client.OpenSession("telnet", "a", new RecordingSink());
                client.OpenSession("telnet", "b", new RecordingSink());
                Assert.Equal(1, host.FrontEnds.ConnectionCount);
                client.Dispose();

                Assert.True(await WaitFor(() => host.Core.CallsSnapshot().Count(call => call.StartsWith("close:")) == 2));
                Assert.Equal(0, host.FrontEnds.ConnectionCount);
                Assert.Contains("close:session1:ClientClosed", host.Core.CallsSnapshot());
                Assert.Contains("close:session2:ClientClosed", host.Core.CallsSnapshot());
            }
        }

        [Fact]
        public async Task ANewerConnectionOfTheSameFrontEndReplacesTheOldOne()
        {
            using (Host host = StartHost())
            using (GrpcCoreApi first = NewClient(host))
            using (GrpcCoreApi second = NewClient(host))
            {
                Assert.Null(first.Connect());
                first.OpenSession("telnet", "old", new RecordingSink());
                Assert.Null(second.Connect());
                Assert.True(await WaitFor(() => host.Core.CallsSnapshot().Contains("close:session1:ClientClosed")));
                Assert.Equal(1, host.FrontEnds.ConnectionCount);
            }
        }

        [Fact]
        public async Task WhenCoreStopsEveryOpenSessionEndsWithTheWorldStoppingReasonAndConnectionLostIsRaisedOnce()
        {
            Host host = StartHost();
            var client = NewClient(host);
            try
            {
                Assert.Null(client.Connect());
                var sink = new RecordingSink();
                client.OpenSession("telnet", "k", sink);
                int lostCount = 0;
                string lostReason = null;
                client.ConnectionLost += reason => { lostReason = reason; Interlocked.Increment(ref lostCount); };

                host.Dispose();

                Assert.True(await WaitFor(() => Volatile.Read(ref lostCount) >= 1));
                await Task.Delay(100);
                Assert.Equal(1, Volatile.Read(ref lostCount));
                Assert.False(string.IsNullOrEmpty(lostReason));
                Assert.Equal(new[] { "ended:WorldStopping" }, sink.Snapshot());
                Assert.False(client.IsConnected);

                // After the loss every call fails soft.
                Assert.True(client.OpenSession("telnet", "k", new RecordingSink()).IsEmpty);
                Assert.Equal(LoginResult.TryAgainLater, client.Authenticate(new SessionId("x"), "Ann", "p"));
                Assert.False(client.IsThrottled("k"));
                Assert.Empty(client.GetOnlineNames());
            }
            finally
            {
                client.Dispose();
            }
        }

        [Fact]
        public async Task ACoreThatRestartedAndForgotTheConnectionEndsTheSessionsOnTheNextCall()
        {
            using (Host host = StartHost())
            using (GrpcCoreApi client = NewClient(host))
            {
                Assert.Null(client.Connect());
                var sink = new RecordingSink();
                SessionId id = client.OpenSession("telnet", "k", sink);
                bool lost = false;
                client.ConnectionLost += reason => lost = true;

                // Core forgets the connection without the stream ending, as a restarted core would for a stale client.
                FrontEndConnection connection = host.FrontEnds.Find(GetConnectionId(client));
                host.FrontEnds.Disconnect(connection, "test");

                Assert.True(await WaitFor(() => lost));
                Assert.Equal(new[] { "ended:WorldStopping" }, sink.Snapshot());
            }
        }

        private static string GetConnectionId(GrpcCoreApi client)
        {
            return (string)typeof(GrpcCoreApi).GetField("_connectionId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(client);
        }

        // ---- back-pressure (D42) ----

        [Fact]
        public void TheSinkNeverBlocksEvenWhenNobodyReadsTheStreamAndAFullQueueCutsTheFrontEndOff()
        {
            using (Host host = StartHost(queueCapacity: 10))
            using (GrpcChannel channel = GrpcCoreApi.OpenChannel(host.Directory.FrontEndsSocketPath))
            {
                var raw = new Session.SessionClient(channel);
                FrontEndWelcome welcome = raw.Connect(new FrontEndHello { Name = "slow", Token = Token, ContractVersion = ContractVersion.Current });
                Assert.True(welcome.Ok);
                FrontEndConnection connection = host.FrontEnds.Find(welcome.ConnectionId);
                OpenSessionReply opened = raw.OpenSession(new OpenSessionRequest { ConnectionId = welcome.ConnectionId, RemoteKey = "k" });
                IFrontEndSink sink = host.Core.Sinks[opened.SessionId];

                // No stream is open: the queue fills after 10 events. Calling a thousand times must still return at once.
                var watch = System.Diagnostics.Stopwatch.StartNew();
                for (int index = 0; index < 1000; index++) { sink.Output(new SessionId(opened.SessionId), OutputChannel.System, "text " + index, false); }
                watch.Stop();
                Assert.True(watch.ElapsedMilliseconds < 2000, "the sink blocked: " + watch.ElapsedMilliseconds + " ms");

                // The overflow ends the connection and closes its sessions in core (on another thread).
                Assert.True(WaitFor(() => host.Core.CallsSnapshot().Contains("close:session1:ClientClosed")).GetAwaiter().GetResult());
                Assert.Equal(0, host.FrontEnds.ConnectionCount);
                Assert.True(connection.IsClosed);
            }
        }

        // ---- the contract and the sockets ----

        [Fact]
        public async Task TheSessionServiceIsNotServedOnModulesSock()
        {
            using (Host host = StartHost())
            using (GrpcChannel channel = GrpcCoreApi.OpenChannel(host.Directory.ModulesSocketPath))
            {
                var client = new Session.SessionClient(channel);
                RpcException wrong = await Assert.ThrowsAsync<RpcException>(async () => await client.ConnectAsync(new FrontEndHello { Name = "telnet", Token = Token, ContractVersion = ContractVersion.Current }));
                Assert.Equal(StatusCode.Unimplemented, wrong.StatusCode);
            }
        }

        [Fact]
        public void WithNoFrontEndRegistryTheFrontendsSockServesNoSessionService()
        {
            string grants = Path.Combine(_directory, "grants.yaml");
            File.WriteAllText(grants, "modules: {}\n");
            File.SetUnixFileMode(grants, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var moduleGrants = new ModuleGrants(grants, _log.Add);
            Assert.True(moduleGrants.Reload());
            var registry = new ModuleRegistry(new ModuleTokenRegistry(), moduleGrants, ContractVersion.Current, new ModuleHostOptions(), _log.Add);
            using (RunDirectory directory = RunDirectory.Acquire(Path.Combine(_directory, "r"), _log.Add))
            using (var runner = new ModuleHostRunner(directory, registry, action => false, _log.Add))
            {
                runner.Start();
                using (GrpcChannel channel = GrpcCoreApi.OpenChannel(directory.FrontEndsSocketPath))
                {
                    var client = new Session.SessionClient(channel);
                    RpcException none = Assert.Throws<RpcException>(() => client.Connect(new FrontEndHello { Name = "telnet", Token = Token, ContractVersion = ContractVersion.Current }));
                    Assert.Equal(StatusCode.Unimplemented, none.StatusCode);
                }
            }
        }

        [Fact]
        public void TheWireConversionsCoverEveryValueOfTheCoreApiEnums()
        {
            foreach (LoginResult result in Enum.GetValues(typeof(LoginResult)))
            {
                Assert.Equal(result, GrpcCoreApi.FromWire(SessionService.ToWire(result)));
            }
            foreach (OutputChannel channel in Enum.GetValues(typeof(OutputChannel)))
            {
                Assert.Equal(channel, GrpcCoreApi.ParseChannel(RemoteFrontEndSink.ChannelName(channel)));
            }
            foreach (SessionEndReason reason in Enum.GetValues(typeof(SessionEndReason)))
            {
                Assert.Equal(reason, GrpcCoreApi.ParseReason(RemoteFrontEndSink.ReasonName(reason)));
            }
            Assert.Equal(OutputChannel.System, GrpcCoreApi.ParseChannel(""));
            Assert.Equal(OutputChannel.System, GrpcCoreApi.ParseChannel("unknown"));
            Assert.Equal(new[] { "say", "tell", "room", "combat", "system", "chat" }, Enum.GetValues(typeof(OutputChannel)).Cast<OutputChannel>().Select(RemoteFrontEndSink.ChannelName).OrderBy(name => Array.IndexOf(new[] { "say", "tell", "room", "combat", "system", "chat" }, name)));
        }

        [Theory]
        [InlineData("idle", SessionEndReason.Idle)]
        [InlineData("flood", SessionEndReason.Flood)]
        [InlineData("client_closed", SessionEndReason.ClientClosed)]
        [InlineData("", SessionEndReason.ClientClosed)]
        [InlineData("anything", SessionEndReason.ClientClosed)]
        public void ACloseReasonFromAFrontEndIsParsedOrDefaultsToClientClosed(string text, SessionEndReason expected)
        {
            Assert.Equal(expected, SessionService.ParseReason(text));
        }

        // ---- the token file (D66) ----

        [Fact]
        public void TheTokenFileIsCreatedWithMode600AndReadBack()
        {
            string path = Path.Combine(_directory, "sub", "frontend-token");
            FrontEndTokenFile created = FrontEndTokenFile.LoadOrCreate(path, _log.Add);
            Assert.True(File.Exists(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            string token = FrontEndTokenFile.ReadToken(path);
            Assert.False(string.IsNullOrEmpty(token));
            Assert.True(created.Validate(token));
            Assert.False(created.Validate(token + "x"));
            Assert.False(created.Validate(""));
            Assert.False(created.Validate(null));
            Assert.DoesNotContain(_log, line => line.Contains(token));

            // A second load keeps the same token.
            Assert.True(FrontEndTokenFile.LoadOrCreate(path, _log.Add).Validate(token));
        }

        [Fact]
        public void ATokenFileOpenToOthersIsUsedWithAWarning()
        {
            string path = Path.Combine(_directory, "open-token");
            File.WriteAllText(path, "abc\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            FrontEndTokenFile file = FrontEndTokenFile.LoadOrCreate(path, _log.Add);
            Assert.True(file.Validate("abc"));
            Assert.Contains(_log, line => line.Contains("should be mode 600"));
        }

        [Fact]
        public void AnEmptyTokenFileIsRefusedAndAMissingOneReadsAsNull()
        {
            string path = Path.Combine(_directory, "empty-token");
            File.WriteAllText(path, "\n");
            Assert.Throws<InvalidOperationException>(() => FrontEndTokenFile.LoadOrCreate(path, _log.Add));
            Assert.Null(FrontEndTokenFile.ReadToken(path));
            Assert.Null(FrontEndTokenFile.ReadToken(Path.Combine(_directory, "missing")));
        }

        [Fact]
        public void TheTokenFilePathComesFromConfigurationThenTheVariableThenTheDefault()
        {
            Assert.Equal("/x/y", FrontEndTokenFile.ResolvePath("/x/y"));
            string previous = Environment.GetEnvironmentVariable(FrontEndTokenFile.TokenFileVariable);
            try
            {
                Environment.SetEnvironmentVariable(FrontEndTokenFile.TokenFileVariable, "/from/variable");
                Assert.Equal("/from/variable", FrontEndTokenFile.ResolvePath(null));
                Assert.Equal("/x/y", FrontEndTokenFile.ResolvePath("/x/y"));
                Environment.SetEnvironmentVariable(FrontEndTokenFile.TokenFileVariable, null);
                Assert.EndsWith(Path.Combine(".config", "mud", "frontend-token"), FrontEndTokenFile.ResolvePath(""));
            }
            finally
            {
                Environment.SetEnvironmentVariable(FrontEndTokenFile.TokenFileVariable, previous);
            }
        }
    }
}
