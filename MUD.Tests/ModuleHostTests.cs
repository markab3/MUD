using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using MUD.Contracts;
using MUD.Core;
using MUD.Core.Modules;
using MUD.Data;
using MUD.Main;
using Xunit;

namespace MUD.Tests
{
    /// <summary>Card P4-2: run directory, tokens, grants, the Hello handshake, the module-call queue and the Kestrel host over real sockets.</summary>
    public class ModuleHostTests : IDisposable
    {
        private readonly string _directory;
        private readonly List<string> _log = new List<string>();

        public ModuleHostTests()
        {
            // Short path: a Unix socket path is limited to 107 bytes.
            _directory = Path.Combine(Path.GetTempPath(), "mh" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_directory);
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, true); } catch (Exception) { }
        }

        private string GrantsFile(string content)
        {
            string path = Path.Combine(_directory, "grants.yaml");
            File.WriteAllText(path, content);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return path;
        }

        private ModuleRegistry NewRegistry(string grantsYaml, out ModuleTokenRegistry tokens, uint version = 1)
        {
            tokens = new ModuleTokenRegistry();
            var grants = new ModuleGrants(GrantsFile(grantsYaml), _log.Add);
            Assert.True(grants.Reload());
            return new ModuleRegistry(tokens, grants, version, new ModuleHostOptions(), _log.Add);
        }

        private static HelloRequest HelloFor(string module, string token, uint version = 1, params string[] capabilities)
        {
            var request = new HelloRequest { Module = module, Token = token, ContractVersion = version, ModuleVersion = "1.0" };
            request.Manifest = new Manifest { Name = module };
            request.Manifest.Capabilities.AddRange(capabilities);
            return request;
        }

        // ---- run directory ----

        [Fact]
        public void RunDirectoryIsOwnerOnlyAndHoldsAnExclusiveLock()
        {
            string path = Path.Combine(_directory, "run");
            using (RunDirectory first = RunDirectory.Acquire(path, _log.Add))
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(path));
                Assert.Throws<RunDirectoryLockedException>(() => RunDirectory.Acquire(path, _log.Add));
            }
            // Released on dispose: a new core can take it.
            using (RunDirectory second = RunDirectory.Acquire(path, _log.Add)) { Assert.Equal(Path.GetFullPath(path), second.Path); }
        }

        [Fact]
        public void RunDirectoryRemovesStaleSocketFilesOnlyWhileHoldingTheLock()
        {
            string path = Path.Combine(_directory, "run");
            Directory.CreateDirectory(path);
            string stale = Path.Combine(path, "modules.sock");
            File.WriteAllText(stale, "left behind by a killed core");
            using (RunDirectory directory = RunDirectory.Acquire(path, _log.Add))
            {
                Assert.False(File.Exists(stale));
                // While locked, a refused second core must not touch a (new) socket file.
                File.WriteAllText(stale, "in use");
                Assert.Throws<RunDirectoryLockedException>(() => RunDirectory.Acquire(path, _log.Add));
                Assert.True(File.Exists(stale));
            }
        }

        [Fact]
        public void RunDirectoryRefusesASocketPathThatIsTooLong()
        {
            string path = Path.Combine(_directory, new string('a', 120));
            Assert.Throws<InvalidOperationException>(() => RunDirectory.Acquire(path, _log.Add));
        }

        // ---- tokens ----

        [Fact]
        public void TokensAreRandomPerIssueAndOnlyTheLatestIsValid()
        {
            var tokens = new ModuleTokenRegistry();
            string first = tokens.Issue("desert");
            string second = tokens.Issue("desert");
            long sequence;
            Assert.NotEqual(first, second);
            Assert.False(tokens.Validate("desert", first, out sequence));
            Assert.True(tokens.Validate("desert", second, out sequence));
            Assert.False(tokens.Validate("temple", second, out sequence));
            Assert.False(tokens.Validate("desert", "", out sequence));
            tokens.Revoke("desert");
            Assert.False(tokens.Validate("desert", second, out sequence));
        }

        // ---- grants ----

        [Fact]
        public void GrantsAreReadVerbatimPerModuleAndReloadable()
        {
            string path = GrantsFile("modules:\n  desert:\n    - emit:entity.move\n    - timer.schedule\n");
            var grants = new ModuleGrants(path, _log.Add);
            Assert.True(grants.Reload());
            Assert.True(grants.IsGranted("desert", "emit:entity.move"));
            Assert.True(grants.IsGranted("desert", "timer.schedule"));
            Assert.False(grants.IsGranted("desert", "state"));
            Assert.False(grants.IsGranted("temple", "timer.schedule"));

            File.WriteAllText(path, "modules:\n  desert:\n    - state\n");
            Assert.True(grants.Reload());
            Assert.True(grants.IsGranted("desert", "state"));
            Assert.False(grants.IsGranted("desert", "timer.schedule"));
        }

        [Fact]
        public void AMissingGrantsFileGrantsNothingAndABrokenOneKeepsTheEarlierGrants()
        {
            var missing = new ModuleGrants(Path.Combine(_directory, "none.yaml"), _log.Add);
            Assert.True(missing.Reload());
            Assert.False(missing.IsGranted("desert", "state"));

            string path = GrantsFile("modules:\n  desert:\n    - state\n");
            var grants = new ModuleGrants(path, _log.Add);
            grants.Reload();
            File.WriteAllText(path, "modules: [this is: not valid");
            Assert.False(grants.Reload());
            Assert.True(grants.IsGranted("desert", "state"));
        }

        // ---- Hello ----

        [Fact]
        public void HelloAcceptsTheIssuedTokenAndSplitsGrantedFromRejected()
        {
            ModuleTokenRegistry tokens;
            ModuleRegistry registry = NewRegistry("modules:\n  desert:\n    - timer.schedule\n", out tokens);
            string token = tokens.Issue("desert");
            HelloReply reply = registry.Hello(HelloFor("desert", token, 1, "timer.schedule", "state"));
            Assert.True(reply.Ok, reply.Refusal);
            Assert.False(string.IsNullOrEmpty(reply.ConnectionId));
            Assert.Equal(new[] { "timer.schedule" }, reply.Granted);
            Assert.Equal(new[] { "state" }, reply.Rejected);
            Assert.Equal(1u, reply.CoreContractVersion);
            Assert.Contains(_log, line => line.Contains("partial grant") && line.Contains("state"));
            Assert.Same(registry.Find(reply.ConnectionId), registry.FindByModule("desert"));
        }

        [Fact]
        public void HelloRefusesAWrongTokenWithoutSayingWhichPartWasWrong()
        {
            ModuleTokenRegistry tokens;
            ModuleRegistry registry = NewRegistry("modules: {}\n", out tokens);
            tokens.Issue("desert");
            HelloReply reply = registry.Hello(HelloFor("desert", "not-the-token"));
            Assert.False(reply.Ok);
            Assert.Contains("Authentication failed", reply.Refusal);
            Assert.Empty(registry.Connections());
            Assert.False(registry.Hello(HelloFor("unknown", "x")).Ok);
        }

        [Fact]
        public void HelloRefusesAContractVersionMismatchAndLogsBothNumbers()
        {
            ModuleTokenRegistry tokens;
            ModuleRegistry registry = NewRegistry("modules: {}\n", out tokens, 3);
            string token = tokens.Issue("desert");
            HelloReply reply = registry.Hello(HelloFor("desert", token, 2));
            Assert.False(reply.Ok);
            Assert.Equal(3u, reply.CoreContractVersion);
            Assert.Contains("version 2", reply.Refusal);
            Assert.Contains("version 3", reply.Refusal);
            Assert.Contains(_log, line => line.Contains("version 2") && line.Contains("version 3"));
            Assert.Empty(registry.Connections());
        }

        [Fact]
        public void HelloRefusesAMissingManifestAndAManifestNamedForAnotherModule()
        {
            ModuleTokenRegistry tokens;
            ModuleRegistry registry = NewRegistry("modules: {}\n", out tokens);
            string token = tokens.Issue("desert");
            var noManifest = new HelloRequest { Module = "desert", Token = token, ContractVersion = 1 };
            Assert.Contains("manifest", registry.Hello(noManifest).Refusal);
            HelloRequest wrongName = HelloFor("desert", token);
            wrongName.Manifest.Name = "temple";
            Assert.Contains("does not match", registry.Hello(wrongName).Refusal);
        }

        [Fact]
        public void HelloRefusesWhenARequiredCapabilityIsNotGranted()
        {
            ModuleTokenRegistry tokens;
            ModuleRegistry registry = NewRegistry("modules:\n  desert:\n    - state\n", out tokens);
            string token = tokens.Issue("desert");
            HelloRequest request = HelloFor("desert", token, 1, "state");
            request.Manifest.RequiredCapabilities.Add("timer.schedule");
            HelloReply reply = registry.Hello(request);
            Assert.False(reply.Ok);
            Assert.Contains("timer.schedule", reply.Refusal);
            Assert.Empty(registry.Connections());
        }

        [Fact]
        public void ATokenWorksForOneConnectionAndANewerSpawnReplacesTheOlderConnection()
        {
            ModuleTokenRegistry tokens;
            ModuleRegistry registry = NewRegistry("modules: {}\n", out tokens);
            string first = tokens.Issue("desert");
            HelloReply one = registry.Hello(HelloFor("desert", first));
            Assert.True(one.Ok);
            Assert.False(registry.Hello(HelloFor("desert", first)).Ok); // same token again

            var disconnected = new List<string>();
            registry.Disconnected += connection => disconnected.Add(connection.ConnectionId);
            string second = tokens.Issue("desert");
            HelloReply two = registry.Hello(HelloFor("desert", second));
            Assert.True(two.Ok);
            Assert.Equal(new[] { one.ConnectionId }, disconnected);
            Assert.Null(registry.Find(one.ConnectionId));
            Assert.NotNull(registry.Find(two.ConnectionId));
        }

        // ---- outbound queue ----

        [Fact]
        public void DeliveriesGetGapFreeSequenceNumbersAndAFullQueueRefusesWithoutBlocking()
        {
            ModuleTokenRegistry tokens;
            ModuleRegistry registry = NewRegistry("modules: {}\n", out tokens);
            registry.Options.OutboundQueueCapacity = 2;
            string token = tokens.Issue("desert");
            ModuleConnection connection = registry.Find(registry.Hello(HelloFor("desert", token)).ConnectionId);
            var a = new CoreEvent { Name = "a" };
            var b = new CoreEvent { Name = "b" };
            var c = new CoreEvent { Name = "c" };
            Assert.True(connection.TryEnqueue(a));
            Assert.True(connection.TryEnqueue(b));
            Assert.False(connection.TryEnqueue(c));
            Assert.Equal(1ul, a.Seq);
            Assert.Equal(2ul, b.Seq);
            connection.Reader.TryRead(out CoreEvent read);
            Assert.True(connection.TryEnqueue(c));
            Assert.Equal(3ul, c.Seq); // the refused attempt used no number
        }

        // ---- the separate module-call queue in the game loop ----

        private sealed class FixedClock : IClock
        {
            public DateTime Now { get { return new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc); } }
        }

        [Fact]
        public void ModuleCallsAreBoundedAndRunOnlyTheirOwnShareOfAPass()
        {
            var loop = new GameLoop(new FixedClock(), item => { }, null, null, _log.Add);
            loop.ModuleCallCapacity = 5;
            loop.ModuleCallsPerPass = 2;
            int ran = 0;
            for (int i = 0; i < 5; i++) { Assert.True(loop.PostModuleCall(() => ran++)); }
            Assert.False(loop.PostModuleCall(() => ran++)); // queue full: refused, not blocked
            Assert.Equal(5, loop.PendingModuleCalls);

            loop.RunOnce(TimeSpan.FromMilliseconds(50));
            Assert.Equal(2, ran);
            loop.RunOnce(TimeSpan.FromMilliseconds(50));
            Assert.Equal(4, ran);
            loop.RunOnce(TimeSpan.FromMilliseconds(50));
            Assert.Equal(5, ran);
            Assert.Equal(0, loop.PendingModuleCalls);
            Assert.True(loop.PostModuleCall(() => ran++)); // room again
        }

        [Fact]
        public void AFloodOfModuleCallsDoesNotStarvePlayerItems()
        {
            var handled = new List<string>();
            var loop = new GameLoop(new FixedClock(), item => handled.Add(item.Line), null, null, _log.Add);
            loop.ModuleCallsPerPass = 3;
            int moduleRuns = 0;
            for (int i = 0; i < 50; i++) { loop.PostModuleCall(() => moduleRuns++); }
            loop.Post(InboundItem.ForLine(new MUD.CoreApi.SessionId("s1"), "look"));
            loop.RunOnce(TimeSpan.FromMilliseconds(50));
            Assert.Contains("look", handled);
            Assert.True(moduleRuns <= 3);
        }

        [Fact]
        public void AModuleCallThatThrowsIsLoggedAndTheLoopContinues()
        {
            var loop = new GameLoop(new FixedClock(), item => { }, null, null, _log.Add);
            int ran = 0;
            loop.PostModuleCall(() => { throw new InvalidOperationException("boom"); });
            loop.PostModuleCall(() => ran++);
            loop.RunOnce(TimeSpan.FromMilliseconds(50));
            Assert.Equal(1, ran);
            Assert.Contains(_log, line => line.Contains("module call") && line.Contains("boom"));
        }

        // ---- Kestrel on the two sockets, over real Unix domain sockets ----

        private static GrpcChannel ChannelFor(string socketPath)
        {
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (context, cancellation) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellation);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };
            return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler });
        }

        [Fact]
        public async Task HandshakeSubscribeReadyAndPingWorkOverTheModulesSocket()
        {
            string runPath = Path.Combine(_directory, "r");
            using (RunDirectory directory = RunDirectory.Acquire(runPath, _log.Add))
            {
                ModuleTokenRegistry tokens;
                ModuleRegistry registry = NewRegistry("modules:\n  hello:\n    - state\n", out tokens);
                var readyModules = new List<string>();
                registry.Ready += connection => readyModules.Add(connection.ModuleName);
                // Stands in for the game thread: runs the call at once on a pool thread.
                using (var runner = new ModuleHostRunner(directory, registry, action => { Task.Run(action); return true; }, _log.Add))
                {
                    runner.Start();
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(directory.ModulesSocketPath));
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(directory.FrontEndsSocketPath));

                    string token = tokens.Issue("hello");
                    using (GrpcChannel channel = ChannelFor(directory.ModulesSocketPath))
                    {
                        var client = new ModuleHost.ModuleHostClient(channel);

                        // A call before Hello is refused.
                        var early = await Assert.ThrowsAsync<RpcException>(async () => await client.PingAsync(new PingRequest { ConnectionId = "nope", N = 1 }));
                        Assert.Equal(StatusCode.Unauthenticated, early.StatusCode);

                        HelloReply bad = await client.HelloAsync(HelloFor("hello", "wrong"));
                        Assert.False(bad.Ok);

                        HelloReply reply = await client.HelloAsync(HelloFor("hello", token, 1, "state", "read"));
                        Assert.True(reply.Ok, reply.Refusal);
                        Assert.Equal(new[] { "state" }, reply.Granted);
                        Assert.Equal(new[] { "read" }, reply.Rejected);

                        Assert.False((await client.ReadyAsync(new ReadyRequest { ConnectionId = reply.ConnectionId })).Ok); // no stream yet

                        PingReply pong = await client.PingAsync(new PingRequest { ConnectionId = reply.ConnectionId, N = 7 });
                        Assert.Equal(7ul, pong.N);

                        using (AsyncServerStreamingCall<CoreEvent> stream = client.Subscribe(new SubscribeRequest { ConnectionId = reply.ConnectionId }))
                        {
                            ModuleConnection connection = registry.Find(reply.ConnectionId);
                            await WaitFor(() => connection.IsSubscribed);
                            Assert.True(connection.TryEnqueue(new CoreEvent { Name = "heartbeat", EventId = "e1" }));
                            Assert.True(await stream.ResponseStream.MoveNext(CancellationToken.None));
                            Assert.Equal("heartbeat", stream.ResponseStream.Current.Name);
                            Assert.Equal(1ul, stream.ResponseStream.Current.Seq);

                            Assert.True((await client.ReadyAsync(new ReadyRequest { ConnectionId = reply.ConnectionId })).Ok);
                            await WaitFor(() => readyModules.Contains("hello"));
                            Assert.True(connection.IsReady);

                            // Not part of this card: the other calls answer Unimplemented.
                            var verdict = await Assert.ThrowsAsync<RpcException>(async () => await client.SubmitVerdictAsync(new VerdictRequest { ConnectionId = reply.ConnectionId }));
                            Assert.Equal(StatusCode.Unimplemented, verdict.StatusCode);

                            // Stopping the host ends the stream at once.
                            registry.DisconnectAll("test");
                            Assert.False(await stream.ResponseStream.MoveNext(CancellationToken.None));
                        }
                        await WaitFor(() => registry.Find(reply.ConnectionId) == null);
                    }

                    // The module service is not served on frontends.sock (D22).
                    using (GrpcChannel channel = ChannelFor(directory.FrontEndsSocketPath))
                    {
                        var client = new ModuleHost.ModuleHostClient(channel);
                        var wrongSocket = await Assert.ThrowsAsync<RpcException>(async () => await client.HelloAsync(HelloFor("hello", token)));
                        Assert.Equal(StatusCode.Unimplemented, wrongSocket.StatusCode);
                    }
                }
                // Stopping removes the sockets.
            }
            Assert.False(File.Exists(Path.Combine(runPath, "modules.sock")));
        }

        [Fact]
        public async Task AFullGameThreadQueueAnswersReadyWithResourceExhausted()
        {
            string runPath = Path.Combine(_directory, "r");
            using (RunDirectory directory = RunDirectory.Acquire(runPath, _log.Add))
            {
                ModuleTokenRegistry tokens;
                ModuleRegistry registry = NewRegistry("modules: {}\n", out tokens);
                using (var runner = new ModuleHostRunner(directory, registry, action => false, _log.Add))
                {
                    runner.Start();
                    string token = tokens.Issue("hello");
                    using (GrpcChannel channel = ChannelFor(directory.ModulesSocketPath))
                    {
                        var client = new ModuleHost.ModuleHostClient(channel);
                        HelloReply reply = await client.HelloAsync(HelloFor("hello", token));
                        using (AsyncServerStreamingCall<CoreEvent> stream = client.Subscribe(new SubscribeRequest { ConnectionId = reply.ConnectionId }))
                        {
                            await WaitFor(() => registry.Find(reply.ConnectionId).IsSubscribed);
                            var exhausted = await Assert.ThrowsAsync<RpcException>(async () => await client.ReadyAsync(new ReadyRequest { ConnectionId = reply.ConnectionId }));
                            Assert.Equal(StatusCode.ResourceExhausted, exhausted.StatusCode);
                        }
                    }
                }
            }
        }

        private static async Task WaitFor(Func<bool> condition)
        {
            for (int i = 0; i < 100 && !condition(); i++) { await Task.Delay(50); }
            Assert.True(condition(), "The condition was not met within 5 seconds.");
        }
    }
}
