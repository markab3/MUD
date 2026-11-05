using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using MongoDB.Bson;
using MUD.Contracts;
using MUD.Core;
using MUD.Core.Components;
using BehaviorRef = MUD.Core.Components.BehaviorRef;
using MUD.Core.Events;
using MUD.Core.GameObjects;
using MUD.Core.Modules;
using MUD.Core.Repositories.Interfaces;
using MUD.CoreApi;
using MUD.Main;
using Xunit;

namespace MUD.Tests
{
    public class FakeModuleStateRepo : FakeRepo<ModuleStateDocument>, IModuleStateRepository { }

    /// <summary>
    /// Card P4-4: the (Module, Type) dictionary wired to connected modules, over a real Unix socket. A dedicated thread stands in for
    /// the game thread (module calls are posted to it, as World.PostModuleCall does); the world's clock is a fake clock, so deadlines and
    /// timers move only when a test moves them. No Mongo: the state repository is an in-memory fake.
    /// </summary>
    [Collection("World construction")] // World registers BSON class maps; constructing it in parallel races
    public class ModuleDispatchTests : IDisposable
    {
        private const string ModuleName = "temple";

        // Stands in for the game thread: runs posted actions one at a time, in order.
        private sealed class GameThread : IDisposable
        {
            private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
            private readonly Thread _thread;

            public GameThread()
            {
                _thread = new Thread(() => { foreach (Action action in _queue.GetConsumingEnumerable()) { try { action(); } catch (Exception) { } } }) { IsBackground = true };
                _thread.Start();
            }

            public bool Post(Action action) { return _queue.TryAdd(action); }

            public T Run<T>(Func<T> work)
            {
                var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                Post(() => { try { done.SetResult(work()); } catch (Exception ex) { done.SetException(ex); } });
                Assert.True(done.Task.Wait(TimeSpan.FromSeconds(10)), "The game thread did not run the work.");
                return done.Task.Result;
            }

            public void Run(Action work) { Run<int>(() => { work(); return 0; }); }

            public void Dispose() { _queue.CompleteAdding(); }
        }

        private sealed class Mover : Living
        {
            public readonly List<string> Messages = new List<string>();
            public readonly List<OutputChannel> Channels = new List<OutputChannel>();
            public Mover(World world, string name) : base(world) { ShortDescription = name; Id = "living." + name; }
            public override void ReceiveMessage(string message) { Messages.Add(message); Channels.Add(DeliveryChannel); }
        }

        private readonly string _directory;
        private readonly List<string> _log = new List<string>();
        private readonly FakeClock _clock = new FakeClock();
        private readonly GameThread _game = new GameThread();
        private readonly Dictionary<string, GameObject> _entities = new Dictionary<string, GameObject>();
        private readonly List<IDisposable> _disposables = new List<IDisposable>();
        private ModuleHostOptions _options = new ModuleHostOptions();
        private ModuleTokenRegistry _tokens;
        private ModuleRegistry _registry;
        private ModuleDispatcher _dispatcher;
        private FakeModuleStateRepo _stateRepo;
        private World _world;
        private RunDirectory _runDirectory;

        public ModuleDispatchTests()
        {
            // Short path: a Unix socket path is limited to 107 bytes.
            _directory = Path.Combine(Path.GetTempPath(), "md" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_directory);
        }

        public void Dispose()
        {
            foreach (IDisposable item in _disposables) { try { item.Dispose(); } catch (Exception) { } }
            _game.Dispose();
            try { _runDirectory?.Dispose(); } catch (Exception) { }
            try { Directory.Delete(_directory, true); } catch (Exception) { }
        }

        // ---- set-up ----

        private void StartHost(string grantedCapabilities, Action<ModuleHostOptions> configure = null)
        {
            if (configure != null) { configure(_options); }
            string grantsPath = Path.Combine(_directory, "grants.yaml");
            File.WriteAllText(grantsPath, "modules:\n  " + ModuleName + ":\n" + string.Join("", grantedCapabilities.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(c => "    - " + c + "\n")));
            File.SetUnixFileMode(grantsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            _tokens = new ModuleTokenRegistry();
            var grants = new ModuleGrants(grantsPath, _log.Add);
            Assert.True(grants.Reload());
            _registry = new ModuleRegistry(_tokens, grants, 1, _options, _log.Add);

            _world = new World(new MongoDB.Driver.MongoClient("mongodb://127.0.0.1:1"), new WorldSettings(), _clock);
            _world.Events.Resolver = NoEventHandlers.Instance;
            _stateRepo = new FakeModuleStateRepo();
            var store = new ModuleStateStore(_stateRepo, _options, _clock);
            _dispatcher = new ModuleDispatcher(_registry, _world.Events, store, _options, _clock, id => { GameObject found; return _entities.TryGetValue(id, out found) ? found : null; }, _log.Add);
            _world.Events.Resolver = _dispatcher;
            ModuleEmitActions.Register(_world.Events);

            _runDirectory = RunDirectory.Acquire(Path.Combine(_directory, "r"), _log.Add);
            var runner = new ModuleHostRunner(_runDirectory, _registry, _game.Post, _log.Add, _dispatcher);
            runner.Start();
            _disposables.Add(runner);
        }

        private sealed class Connected
        {
            public ModuleHost.ModuleHostClient Client;
            public string ConnectionId;
            public AsyncServerStreamingCall<CoreEvent> Stream;
            public ModuleConnection Connection;
        }

        private GrpcChannel ChannelFor(string socketPath)
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
                    catch { socket.Dispose(); throw; }
                }
            };
            GrpcChannel channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler });
            _disposables.Add(channel);
            return channel;
        }

        // Hello, Subscribe and Ready, as a module would. The manifest declares the behavior type "Trap".
        private async Task<Connected> ConnectAsync(string requestedCapabilities, bool ready = true)
        {
            var client = new ModuleHost.ModuleHostClient(ChannelFor(_runDirectory.ModulesSocketPath));
            var hello = new HelloRequest { Module = ModuleName, Token = _tokens.Issue(ModuleName), ContractVersion = 1, ModuleVersion = "1.0", Manifest = new Manifest { Name = ModuleName } };
            hello.Manifest.Capabilities.AddRange(requestedCapabilities.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            hello.Manifest.Behaviors.Add(new BehaviorDecl { Type = "Trap" });
            HelloReply reply = await client.HelloAsync(hello);
            Assert.True(reply.Ok, reply.Refusal);
            var connected = new Connected { Client = client, ConnectionId = reply.ConnectionId, Connection = _registry.Find(reply.ConnectionId) };
            connected.Stream = client.Subscribe(new SubscribeRequest { ConnectionId = reply.ConnectionId });
            _disposables.Add(connected.Stream);
            await WaitFor(() => connected.Connection.IsSubscribed);
            if (ready)
            {
                Assert.True((await client.ReadyAsync(new ReadyRequest { ConnectionId = reply.ConnectionId })).Ok);
                await WaitFor(() => connected.Connection.IsReady);
            }
            return connected;
        }

        private static async Task<CoreEvent> NextAsync(Connected connected)
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                Assert.True(await connected.Stream.ResponseStream.MoveNext(timeout.Token), "The stream ended.");
                return connected.Stream.ResponseStream.Current;
            }
        }

        private static async Task WaitFor(Func<bool> condition)
        {
            for (int i = 0; i < 100 && !condition(); i++) { await Task.Delay(50); }
            Assert.True(condition(), "The condition was not met within 5 seconds.");
        }

        private Room NewRoom(string id)
        {
            var room = new Room(new FakeRoomRepo()) { Id = id, ShortDescription = id, LongDescription = id + " long" };
            _entities[id] = room;
            return room;
        }

        private Mover Place(Room room, string name)
        {
            var mover = new Mover(_world, name);
            room.EnterRoom(mover, name + " arrives");
            mover.CurrentLocation = room;
            mover.CurrentLocationId = room.Id;
            _entities[mover.Id] = mover;
            return mover;
        }

        private static void Hook(GameObject entity, string hook, string type, BsonDocument config = null)
        {
            ((IBehaviorHolder)entity).Behaviors.Add(new BehaviorRef(hook, ModuleName, type, 0, config));
        }

        // Starts a Before chain on the game thread and returns the task that completes with its outcome.
        private Task<BeforeOutcome> BeginBefore(string name, string hook, GameObject carrier, bool sequential = false, params EventParty[] others)
        {
            var done = new TaskCompletionSource<BeforeOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            var parties = new List<EventParty> { new EventParty("to", carrier, new[] { hook }) };
            parties.AddRange(others);
            _game.Run(() => _world.Events.BeginBefore(name, parties, new Dictionary<string, object> { { "reason", "walk" } }, sequential, outcome => done.TrySetResult(outcome)));
            return done.Task;
        }

        private static EventRecord Record(string name, Struct payload, params string[] targets)
        {
            var record = new EventRecord { Name = name, Phase = Phase.After, Payload = payload };
            record.Targets.AddRange(targets);
            return record;
        }

        private static Struct Map(params (string Key, Value Value)[] fields)
        {
            var result = new Struct();
            foreach (var field in fields) { result.Fields[field.Key] = field.Value; }
            return result;
        }

        private int Pump() { return _game.Run(() => _world.Events.Pump(1000)); }

        // ---- the dictionary and deliveries ----

        [Fact]
        public async Task ABeforeDeliveryReachesTheModuleStreamAndADenyVerdictEndsTheChain()
        {
            StartHost("");
            Connected module = await ConnectAsync("");
            Room room = NewRoom("t.room");
            Hook(room, BehaviorHooks.LivingEnter, "Trap", new BsonDocument { { "damage", 5 } });

            Task<BeforeOutcome> outcome = BeginBefore("entity.move", BehaviorHooks.LivingEnter, room);

            CoreEvent delivery = await NextAsync(module);
            Assert.Equal("entity.move", delivery.Name);
            Assert.Equal(Phase.Before, delivery.Phase);
            Assert.Equal("living.enter", delivery.Hook);
            Assert.Equal(150u, delivery.DeadlineMs);
            Assert.Equal(1ul, delivery.Seq);
            Assert.NotEmpty(delivery.EventId);
            Assert.Equal("Trap", delivery.Behavior.Type);
            Assert.Equal(ModuleName, delivery.Behavior.Module);
            Assert.Equal(5, delivery.Behavior.Config.Fields["damage"].NumberValue); // numbers arrive as floating point (D52)
            Assert.Equal("walk", delivery.Payload.Fields["reason"].StringValue);
            PartySnapshot party = Assert.Single(delivery.Parties);
            Assert.Equal("to", party.Role);
            Assert.Equal("room", party.Kind);
            Assert.Equal("t.room", party.EntityId);
            Assert.False(party.Components.Fields.ContainsKey("temple.trap")); // core components only (D57)
            Assert.False(outcome.IsCompleted);

            VerdictReply reply = await module.Client.SubmitVerdictAsync(new VerdictRequest { ConnectionId = module.ConnectionId, EventId = delivery.EventId, Decision = Decision.Deny, Message = "A trap springs!" });
            Assert.True(reply.Ok, reply.Error);
            BeforeOutcome result = await outcome;
            Assert.Equal(BeforeResult.Denied, result.Result);
            Assert.Equal("A trap springs!", result.Message);
            Assert.Equal(0, _dispatcher.PendingVerdictCount);
        }

        [Fact]
        public async Task AnAllowVerdictLetsTheActionGoAhead()
        {
            StartHost("");
            Connected module = await ConnectAsync("");
            Room room = NewRoom("t.room");
            Hook(room, BehaviorHooks.LivingEnter, "Trap");

            Task<BeforeOutcome> outcome = BeginBefore("entity.move", BehaviorHooks.LivingEnter, room);
            CoreEvent delivery = await NextAsync(module);
            Assert.True((await module.Client.SubmitVerdictAsync(new VerdictRequest { ConnectionId = module.ConnectionId, EventId = delivery.EventId, Decision = Decision.Allow })).Ok);

            Assert.Equal(BeforeResult.Allowed, (await outcome).Result);
        }

        [Fact]
        public async Task ALateVerdictIsDroppedAndTheChainFollowsTheFailPolicy()
        {
            StartHost("");
            Connected module = await ConnectAsync("");
            Room room = NewRoom("t.room");
            Hook(room, BehaviorHooks.LivingEnter, "Trap");

            Task<BeforeOutcome> outcome = BeginBefore("entity.move", BehaviorHooks.LivingEnter, room);
            CoreEvent delivery = await NextAsync(module);

            _clock.Advance(TimeSpan.FromMilliseconds(200)); // past the 150 ms chain deadline
            VerdictReply late = await module.Client.SubmitVerdictAsync(new VerdictRequest { ConnectionId = module.ConnectionId, EventId = delivery.EventId, Decision = Decision.Deny, Message = "too late" });

            Assert.False(late.Ok);
            Assert.Contains("late", late.Error);
            BeforeOutcome result = await outcome;
            Assert.Equal(BeforeResult.Allowed, result.Result); // fails open (D60): the late Deny did not count
            Assert.Equal(1, result.TimedOut);
            Assert.True(_dispatcher.DroppedVerdicts >= 1);
        }

        [Fact]
        public async Task AnUnknownOrRepeatedVerdictIsDropped()
        {
            StartHost("");
            Connected module = await ConnectAsync("");
            Room room = NewRoom("t.room");
            Hook(room, BehaviorHooks.LivingEnter, "Trap");

            Task<BeforeOutcome> outcome = BeginBefore("entity.move", BehaviorHooks.LivingEnter, room);
            CoreEvent delivery = await NextAsync(module);
            var request = new VerdictRequest { ConnectionId = module.ConnectionId, EventId = delivery.EventId, Decision = Decision.Allow };
            Assert.True((await module.Client.SubmitVerdictAsync(request)).Ok);
            await outcome;

            Assert.False((await module.Client.SubmitVerdictAsync(request)).Ok); // answered already
            Assert.False((await module.Client.SubmitVerdictAsync(new VerdictRequest { ConnectionId = module.ConnectionId, EventId = "999", Decision = Decision.Allow })).Ok);
        }

        [Fact]
        public async Task AVerdictForAnotherModulesDeliveryIsDropped()
        {
            StartHost("");
            Connected module = await ConnectAsync("");
            Room room = NewRoom("t.room");
            Hook(room, BehaviorHooks.LivingEnter, "Trap");
            Task<BeforeOutcome> outcome = BeginBefore("entity.move", BehaviorHooks.LivingEnter, room);
            CoreEvent delivery = await NextAsync(module);

            // A second connection of the same name replaces the first (restart overlap); it cannot answer the first one's delivery.
            Connected newer = await ConnectAsync("", ready: false);
            VerdictReply reply = await newer.Client.SubmitVerdictAsync(new VerdictRequest { ConnectionId = newer.ConnectionId, EventId = delivery.EventId, Decision = Decision.Deny, Message = "not mine" });
            Assert.False(reply.Ok);
            _clock.Advance(TimeSpan.FromMilliseconds(200));
            _game.Run(() => _world.Events.Pump());
            Assert.Equal(BeforeResult.Allowed, (await outcome).Result);
        }

        [Fact]
        public async Task CommandEnteredHandledFlagStopsTheSequentialChain()
        {
            StartHost("");
            Connected module = await ConnectAsync("");
            Room room = NewRoom("t.room");
            Hook(room, BehaviorHooks.CommandEntered, "Trap");

            Task<BeforeOutcome> outcome = BeginBefore("command.entered", BehaviorHooks.CommandEntered, room, sequential: true);
            CoreEvent delivery = await NextAsync(module);
            Assert.Equal("command.entered", delivery.Hook);
            Assert.True((await module.Client.SubmitVerdictAsync(new VerdictRequest { ConnectionId = module.ConnectionId, EventId = delivery.EventId, Decision = Decision.Allow, Handled = true, Message = "You pull the lever." })).Ok);

            BeforeOutcome result = await outcome;
            Assert.True(result.Handled);
            Assert.Equal("You pull the lever.", result.Message);
        }

        [Fact]
        public async Task AnEntryIsDormantUntilTheModuleIsReadyAndDeclaresTheType()
        {
            StartHost("");
            Connected module = await ConnectAsync("", ready: false);
            IEventHandler handler;
            Assert.False(_dispatcher.TryResolve(ModuleName, "Trap", out handler)); // connected but not ready
            Assert.Equal(BeforeResult.Allowed, await BeforeOnDormantRoom());

            Assert.True((await module.Client.ReadyAsync(new ReadyRequest { ConnectionId = module.ConnectionId })).Ok);
            await WaitFor(() => module.Connection.IsReady);
            Assert.True(_dispatcher.TryResolve(ModuleName, "Trap", out handler));
            Assert.False(_dispatcher.TryResolve(ModuleName, "Undeclared", out handler)); // not in the manifest
            Assert.False(_dispatcher.TryResolve("elsewhere", "Trap", out handler));
        }

        private async Task<BeforeResult> BeforeOnDormantRoom()
        {
            Room room = NewRoom("t.dormant");
            Hook(room, BehaviorHooks.LivingEnter, "Trap");
            BeforeOutcome outcome = await BeginBefore("entity.move", BehaviorHooks.LivingEnter, room);
            Assert.True(_world.Events.BeforeMisses >= 1);
            return outcome.Result;
        }

        [Fact]
        public async Task AClosedConnectionMakesItsEntriesDormantAgain()
        {
            StartHost("");
            Connected module = await ConnectAsync("");
            IEventHandler handler;
            Assert.True(_dispatcher.TryResolve(ModuleName, "Trap", out handler));

            _registry.Disconnect(module.Connection, "test");

            Assert.False(_dispatcher.TryResolve(ModuleName, "Trap", out handler));
        }

        [Fact]
        public async Task AnAfterNotificationIsDeliveredWithoutAVerdict()
        {
            StartHost("");
            Connected module = await ConnectAsync("");
            Room room = NewRoom("t.room");
            Hook(room, BehaviorHooks.LivingEnter, "Trap");

            _game.Run(() => _world.Events.EmitAfter("entity.move", new[] { new EventParty("to", room, new[] { BehaviorHooks.LivingEnter }) }, new Dictionary<string, object> { { "reason", "walk" } }));
            Pump();

            CoreEvent delivery = await NextAsync(module);
            Assert.Equal(Phase.After, delivery.Phase);
            Assert.Equal("entity.move", delivery.Name);
            Assert.Equal(0u, delivery.DeadlineMs);
            Assert.Equal("Trap", delivery.Behavior.Type);
        }

        // ---- verdict emitted events (D61) ----

        [Fact]
        public async Task AVerdictAppliesTheValidEmittedEventsAndListsTheRefusedOnes()
        {
            StartHost("emit:message.send");
            Connected module = await ConnectAsync("emit:message.send");
            Room room = NewRoom("t.room");
            Mover ann = Place(room, "ann");
            Hook(room, BehaviorHooks.LivingEnter, "Trap");

            Task<BeforeOutcome> outcome = BeginBefore("entity.move", BehaviorHooks.LivingEnter, room);
            CoreEvent delivery = await NextAsync(module);
            var request = new VerdictRequest { ConnectionId = module.ConnectionId, EventId = delivery.EventId, Decision = Decision.Deny, Message = "Blocked." };
            request.Emitted.Add(Record("message.send", Map(("text", Value.ForString("Dust falls.")), ("channel", Value.ForString("combat"))), "living.ann"));
            request.Emitted.Add(Record("component.set", Map(("component", Value.ForString("temple.trap")), ("data", Value.ForStruct(new Struct()))), "t.room")); // not granted
            VerdictReply reply = await module.Client.SubmitVerdictAsync(request);

            Assert.True(reply.Ok, reply.Error);
            EmitRefusal refusal = Assert.Single(reply.Refusals);
            Assert.Equal(1u, refusal.Index);
            Assert.Equal("component.set", refusal.EventName);
            Assert.Contains("emit:component.set", refusal.Reason);
            Assert.Equal(BeforeResult.Denied, (await outcome).Result); // the verdict counts although one event was refused
            Pump();
            Assert.Equal(new[] { "Dust falls." }, ann.Messages);
            Assert.Equal(OutputChannel.Combat, ann.Channels[0]);
            Assert.False(room.HasComponent("temple.trap"));
        }

        // ---- Emit (capability checks) ----

        [Fact]
        public async Task EmitWithoutTheCapabilityIsRefusedWithAnErrorNamingIt()
        {
            StartHost("");
            Connected module = await ConnectAsync("");
            Room room = NewRoom("t.room");

            var request = new EmitRequest { ConnectionId = module.ConnectionId };
            request.Events.Add(Record("message.send", Map(("text", Value.ForString("hi"))), "t.room"));
            EmitReply reply = await module.Client.EmitAsync(request);

            EmitRefusal refusal = Assert.Single(reply.Refusals);
            Assert.Equal(0u, refusal.Index);
            Assert.Contains("emit:message.send", refusal.Reason);
            Assert.Contains(_log, line => line.Contains("emit:message.send"));
        }

        [Fact]
        public async Task EmitAppliesAGrantedEventAndOnlyTheModulesOwnComponentPrefix()
        {
            StartHost("emit:component.set emit:component.remove");
            Connected module = await ConnectAsync("emit:component.set emit:component.remove");
            Room room = NewRoom("t.room");

            var request = new EmitRequest { ConnectionId = module.ConnectionId };
            request.Events.Add(Record("component.set", Map(("component", Value.ForString("temple.trap")), ("data", Value.ForStruct(Map(("armed", Value.ForBool(true)))))), "t.room"));
            request.Events.Add(Record("component.set", Map(("component", Value.ForString("desert.storm")), ("data", Value.ForStruct(new Struct()))), "t.room")); // another module's prefix (D50)
            request.Events.Add(Record("component.set", Map(("component", Value.ForString("temple.trap")), ("data", Value.ForStruct(new Struct()))), "t.missing")); // no such entity
            EmitReply reply = await module.Client.EmitAsync(request);

            Assert.Equal(new uint[] { 1, 2 }, reply.Refusals.Select(r => r.Index).ToArray());
            Assert.Contains("temple.*", reply.Refusals[0].Reason);
            Assert.Contains("not found", reply.Refusals[1].Reason);
            Pump();
            BsonDocument stored;
            Assert.True(room.TryGetComponent("temple.trap", out stored));
            Assert.True(stored["armed"].AsBoolean);
            Assert.False(room.HasComponent("desert.storm"));

            var removal = new EmitRequest { ConnectionId = module.ConnectionId };
            removal.Events.Add(Record("component.remove", Map(("component", Value.ForString("temple.trap"))), "t.room"));
            removal.Events.Add(Record("component.remove", Map(("component", Value.ForString("temple.trap"))), "t.room")); // idempotent undo (D11)
            Assert.Empty((await module.Client.EmitAsync(removal)).Refusals);
            Pump();
            Assert.False(room.HasComponent("temple.trap"));
        }

        [Fact]
        public async Task EmitCanRedirectAMoverWithAnEntityMoveRequest()
        {
            StartHost("emit:entity.move");
            Connected module = await ConnectAsync("emit:entity.move");
            Room from = NewRoom("t.from");
            Room to = NewRoom("t.to");
            Mover ann = Place(from, "ann");

            var request = new EmitRequest { ConnectionId = module.ConnectionId };
            request.Events.Add(Record("entity.move", Map(("reason", Value.ForString("trap"))), "living.ann", "t.to"));
            Assert.Empty((await module.Client.EmitAsync(request)).Refusals);
            Pump();

            Assert.Same(to, ann.CurrentLocation);
            Assert.Contains(ann, to.Occupants);
        }

        [Fact]
        public async Task EmitFeatureAddAndRemoveWorkOnARoomByShortText()
        {
            StartHost("emit:feature.add emit:feature.remove");
            Connected module = await ConnectAsync("emit:feature.add emit:feature.remove");
            Room room = NewRoom("t.room");

            var add = new EmitRequest { ConnectionId = module.ConnectionId };
            add.Events.Add(Record("feature.add", Map(("short", Value.ForString("a wall of sand")), ("roomLine", Value.ForString("Sand rolls over everything."))), "t.room"));
            Assert.Empty((await module.Client.EmitAsync(add)).Refusals);
            Pump();
            Feature feature = Assert.Single(room.Features);
            Assert.Equal("a wall of sand", feature.ShortDescription);
            Assert.Equal("Sand rolls over everything.", feature.RoomLine);

            var remove = new EmitRequest { ConnectionId = module.ConnectionId };
            remove.Events.Add(Record("feature.remove", Map(("short", Value.ForString("A Wall Of Sand"))), "t.room"));
            Assert.Empty((await module.Client.EmitAsync(remove)).Refusals);
            Pump();
            Assert.Empty(room.Features);
        }

        [Fact]
        public async Task EmitBehaviorAddNeedsOnlyTheEmitCapabilityAndNoModuleNameCheck()
        {
            StartHost("emit:behavior.add emit:behavior.remove");
            Connected module = await ConnectAsync("emit:behavior.add emit:behavior.remove");
            Room room = NewRoom("t.room");

            var add = new EmitRequest { ConnectionId = module.ConnectionId };
            add.Events.Add(Record("behavior.add", Map(("hook", Value.ForString("living.enter")), ("module", Value.ForString("someone.else")), ("type", Value.ForString("Thing"))), "t.room"));
            Assert.Empty((await module.Client.EmitAsync(add)).Refusals);
            Pump();
            Assert.NotNull(room.FindBehavior("living.enter", "someone.else", "Thing")); // D55: any module name

            var remove = new EmitRequest { ConnectionId = module.ConnectionId };
            remove.Events.Add(Record("behavior.remove", Map(("hook", Value.ForString("living.enter")), ("module", Value.ForString("someone.else")), ("type", Value.ForString("Thing"))), "t.room"));
            Assert.Empty((await module.Client.EmitAsync(remove)).Refusals);
            Pump();
            Assert.Null(room.FindBehavior("living.enter", "someone.else", "Thing"));
        }

        [Fact]
        public async Task EmitIsHeldToThePerModuleQuota()
        {
            StartHost("emit:message.send", options => { options.EmitBurst = 2; options.EmitPerSecond = 1; });
            Connected module = await ConnectAsync("emit:message.send");
            NewRoom("t.room");

            var request = new EmitRequest { ConnectionId = module.ConnectionId };
            for (int index = 0; index < 3; index++) { request.Events.Add(Record("message.send", Map(("text", Value.ForString("x"))), "t.room")); }
            EmitReply reply = await module.Client.EmitAsync(request);

            EmitRefusal refusal = Assert.Single(reply.Refusals);
            Assert.Equal(2u, refusal.Index);
            Assert.Contains("quota", refusal.Reason);
            _clock.Advance(TimeSpan.FromSeconds(2));
            var later = new EmitRequest { ConnectionId = module.ConnectionId };
            later.Events.Add(Record("message.send", Map(("text", Value.ForString("y"))), "t.room"));
            Assert.Empty((await module.Client.EmitAsync(later)).Refusals); // the bucket refilled
        }

        [Fact]
        public async Task EmitRefusesAMessageWithAnUnknownChannelOrAnItemTarget()
        {
            StartHost("emit:message.send");
            Connected module = await ConnectAsync("emit:message.send");
            Room room = NewRoom("t.room");
            _entities["t.item"] = new InventoryItem { Id = "t.item" };

            var request = new EmitRequest { ConnectionId = module.ConnectionId };
            request.Events.Add(Record("message.send", Map(("text", Value.ForString("x")), ("channel", Value.ForString("shout"))), "t.room"));
            request.Events.Add(Record("message.send", Map(("text", Value.ForString("x"))), "t.item"));
            EmitReply reply = await module.Client.EmitAsync(request);

            Assert.Equal(new uint[] { 0, 1 }, reply.Refusals.Select(r => r.Index).ToArray());
        }

        // ---- state (D12, D73) ----

        [Fact]
        public async Task StateIsReplacedAsAWholeDocumentPerKeyAndReadBack()
        {
            StartHost("state");
            Connected module = await ConnectAsync("state");

            Assert.False((await module.Client.GetStateAsync(new GetStateRequest { ConnectionId = module.ConnectionId, Key = "schedule" })).Found);

            var first = new SetStateRequest { ConnectionId = module.ConnectionId, Key = "schedule", Doc = Map(("a", Value.ForNumber(1)), ("b", Value.ForString("x"))) };
            Assert.True((await module.Client.SetStateAsync(first)).Ok);
            var second = new SetStateRequest { ConnectionId = module.ConnectionId, Key = "schedule", Doc = Map(("a", Value.ForNumber(2))) };
            Assert.True((await module.Client.SetStateAsync(second)).Ok);

            GetStateReply reply = await module.Client.GetStateAsync(new GetStateRequest { ConnectionId = module.ConnectionId, Key = "schedule" });
            Assert.True(reply.Found);
            Assert.Equal(2, reply.Doc.Fields["a"].NumberValue);
            Assert.False(reply.Doc.Fields.ContainsKey("b")); // whole-document replace, last write wins (D73)

            // Stored through the repository path, in the module_state collection, under the module's own id.
            BsonDocument stored = _stateRepo.Docs["temple/schedule"];
            Assert.Equal("temple", stored["Module"].AsString);
            Assert.Equal(2, stored["Document"].AsBsonDocument["a"].ToDouble());
        }

        [Fact]
        public async Task StateOfOneModuleIsInvisibleUnderAnotherModulesName()
        {
            StartHost("state");
            Connected module = await ConnectAsync("state");
            Assert.True((await module.Client.SetStateAsync(new SetStateRequest { ConnectionId = module.ConnectionId, Key = "k", Doc = Map(("a", Value.ForNumber(1))) })).Ok);

            Assert.True(_stateRepo.Docs.ContainsKey("temple/k"));
            Assert.False(_stateRepo.Docs.ContainsKey("k"));
            Assert.False(_stateRepo.Docs.ContainsKey("desert/k"));
        }

        [Fact]
        public async Task StateCallsWithoutTheStateCapabilityAreRefused()
        {
            StartHost("");
            Connected module = await ConnectAsync("");

            Ack set = await module.Client.SetStateAsync(new SetStateRequest { ConnectionId = module.ConnectionId, Key = "k", Doc = new Struct() });
            Assert.False(set.Ok);
            Assert.Contains("'state'", set.Error);
            var denied = await Assert.ThrowsAsync<RpcException>(async () => await module.Client.GetStateAsync(new GetStateRequest { ConnectionId = module.ConnectionId, Key = "k" }));
            Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
            Assert.Contains("'state'", denied.Status.Detail);
            Assert.Empty(_stateRepo.Docs);
        }

        [Fact]
        public async Task AStateDocumentOverTheSizeLimitIsRefused()
        {
            StartHost("state", options => options.StateMaxDocumentBytes = 200);
            Connected module = await ConnectAsync("state");

            Ack tooBig = await module.Client.SetStateAsync(new SetStateRequest { ConnectionId = module.ConnectionId, Key = "k", Doc = Map(("text", Value.ForString(new string('x', 500)))) });
            Assert.False(tooBig.Ok);
            Assert.Contains("200", tooBig.Error);
            Assert.True((await module.Client.SetStateAsync(new SetStateRequest { ConnectionId = module.ConnectionId, Key = "k", Doc = Map(("text", Value.ForString("small"))) })).Ok);
        }

        [Fact]
        public async Task StateWritesAreHeldToThePerModuleRateAndRefill()
        {
            StartHost("state", options => { options.StateWriteBurst = 2; options.StateWritesPerSecond = 1; });
            Connected module = await ConnectAsync("state");
            Func<Task<Ack>> write = () => module.Client.SetStateAsync(new SetStateRequest { ConnectionId = module.ConnectionId, Key = "k", Doc = new Struct() }).ResponseAsync;

            Assert.True((await write()).Ok);
            Assert.True((await write()).Ok);
            Ack refused = await write();
            Assert.False(refused.Ok);
            Assert.Contains("Too many state writes", refused.Error);

            _clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True((await write()).Ok);
        }

        [Fact]
        public async Task StateKeyIsChecked()
        {
            StartHost("state");
            Connected module = await ConnectAsync("state");

            Ack empty = await module.Client.SetStateAsync(new SetStateRequest { ConnectionId = module.ConnectionId, Key = "", Doc = new Struct() });
            Assert.False(empty.Ok);
            Ack longKey = await module.Client.SetStateAsync(new SetStateRequest { ConnectionId = module.ConnectionId, Key = new string('k', 200), Doc = new Struct() });
            Assert.False(longKey.Ok);
        }

        // ---- timers (D74) ----

        private long UnixMilliseconds(DateTime moment) { return new DateTimeOffset(moment).ToUnixTimeMilliseconds(); }

        [Fact]
        public async Task ATimerFiresOnceToTheModuleThatScheduledItWhenDue()
        {
            StartHost("timer.schedule");
            Connected module = await ConnectAsync("timer.schedule");

            var request = new TimerRequest { ConnectionId = module.ConnectionId, TimerId = "storm.end", AtUnixMs = UnixMilliseconds(_clock.Now.AddSeconds(5)), Payload = Map(("room", Value.ForString("t.room"))) };
            Assert.True((await module.Client.ScheduleTimerAsync(request)).Ok);
            Assert.Equal(1, _dispatcher.TimerCount(module.Connection));

            _clock.Advance(TimeSpan.FromSeconds(4));
            _game.Run(() => _dispatcher.Tick());
            Assert.Equal(1, _dispatcher.TimerCount(module.Connection)); // not due yet

            _clock.Advance(TimeSpan.FromSeconds(2));
            _game.Run(() => _dispatcher.Tick());
            CoreEvent fired = await NextAsync(module);
            Assert.Equal("timer.fired", fired.Name);
            Assert.Equal("storm.end", fired.Payload.Fields["timer_id"].StringValue);
            Assert.Equal("t.room", fired.Payload.Fields["payload"].StructValue.Fields["room"].StringValue);
            Assert.Null(fired.Behavior); // no behavior entry for a timer (CORE_API 2.2)
            Assert.Equal(0, _dispatcher.TimerCount(module.Connection));

            _game.Run(() => _dispatcher.Tick()); // fires once, not again
            Assert.Equal(0, module.Connection.PendingDeliveries);
        }

        [Fact]
        public async Task ATimerSetForThePastFiresAfterTheMinimumDelay()
        {
            StartHost("timer.schedule");
            Connected module = await ConnectAsync("timer.schedule");

            Assert.True((await module.Client.ScheduleTimerAsync(new TimerRequest { ConnectionId = module.ConnectionId, TimerId = "now", AtUnixMs = 0 })).Ok);
            _game.Run(() => _dispatcher.Tick());
            Assert.Equal(1, _dispatcher.TimerCount(module.Connection)); // the minimum delay (100 ms) has not passed
            _clock.Advance(TimeSpan.FromMilliseconds(100));
            _game.Run(() => _dispatcher.Tick());
            Assert.Equal("timer.fired", (await NextAsync(module)).Name);
        }

        [Fact]
        public async Task AScheduledTimerWithTheSameIdIsReplacedAndACancelledTimerDoesNotFire()
        {
            StartHost("timer.schedule");
            Connected module = await ConnectAsync("timer.schedule");
            Func<string, int, Task<Ack>> schedule = (id, seconds) => module.Client.ScheduleTimerAsync(new TimerRequest { ConnectionId = module.ConnectionId, TimerId = id, AtUnixMs = UnixMilliseconds(_clock.Now.AddSeconds(seconds)) }).ResponseAsync;

            await schedule("a", 1);
            await schedule("a", 10); // replaces
            await schedule("b", 1);
            Assert.True((await module.Client.CancelTimerAsync(new CancelTimerRequest { ConnectionId = module.ConnectionId, TimerId = "b" })).Ok);
            Assert.Equal(1, _dispatcher.TimerCount(module.Connection));

            _clock.Advance(TimeSpan.FromSeconds(2));
            _game.Run(() => _dispatcher.Tick());
            Assert.Equal(0, module.Connection.PendingDeliveries);
        }

        [Fact]
        public async Task TimersNeedTheCapabilityAndAreLimitedPerModule()
        {
            StartHost("", options => options.TimersPerModule = 2);
            Connected without = await ConnectAsync("");
            Ack denied = await without.Client.ScheduleTimerAsync(new TimerRequest { ConnectionId = without.ConnectionId, TimerId = "a", AtUnixMs = 1 });
            Assert.False(denied.Ok);
            Assert.Contains("'timer.schedule'", denied.Error);
            Assert.False((await without.Client.CancelTimerAsync(new CancelTimerRequest { ConnectionId = without.ConnectionId, TimerId = "a" })).Ok);
        }

        [Fact]
        public async Task TimersPerModuleAreLimited()
        {
            StartHost("timer.schedule", options => options.TimersPerModule = 2);
            Connected module = await ConnectAsync("timer.schedule");
            Func<string, Task<Ack>> schedule = id => module.Client.ScheduleTimerAsync(new TimerRequest { ConnectionId = module.ConnectionId, TimerId = id, AtUnixMs = UnixMilliseconds(_clock.Now.AddSeconds(5)) }).ResponseAsync;

            Assert.True((await schedule("a")).Ok);
            Assert.True((await schedule("b")).Ok);
            Ack refused = await schedule("c");
            Assert.False(refused.Ok);
            Assert.Contains("limit is 2", refused.Error);
            Assert.True((await schedule("a")).Ok); // replacing an existing id is not a new timer
            Assert.False((await module.Client.ScheduleTimerAsync(new TimerRequest { ConnectionId = module.ConnectionId, TimerId = "", AtUnixMs = 1 })).Ok);
        }

        [Fact]
        public async Task TimersDoNotSurviveAReconnect()
        {
            StartHost("timer.schedule");
            Connected first = await ConnectAsync("timer.schedule");
            Assert.True((await first.Client.ScheduleTimerAsync(new TimerRequest { ConnectionId = first.ConnectionId, TimerId = "a", AtUnixMs = UnixMilliseconds(_clock.Now.AddSeconds(1)) })).Ok);
            Assert.Equal(1, _dispatcher.TimerCount(first.Connection));

            Connected second = await ConnectAsync("timer.schedule"); // a newer spawn replaces the first connection
            _clock.Advance(TimeSpan.FromSeconds(5));
            _game.Run(() => _dispatcher.Tick());

            Assert.Equal(0, _dispatcher.TimerCount(first.Connection));
            Assert.Equal(0, _dispatcher.TimerCount(second.Connection));
            Assert.Equal(0, second.Connection.PendingDeliveries); // nothing was fired to the new connection (D74)
        }

        // ---- host wiring ----

        [Fact]
        public async Task TheOtherCallsStillAnswerUnimplemented()
        {
            StartHost("read");
            Connected module = await ConnectAsync("read");

            var read = await Assert.ThrowsAsync<RpcException>(async () => await module.Client.GetEntityAsync(new GetEntityRequest { ConnectionId = module.ConnectionId, EntityId = "t.room" }));
            Assert.Equal(StatusCode.Unimplemented, read.StatusCode);
        }

        [Fact]
        public async Task ACallWhenTheGameThreadQueueIsFullIsRefusedNotBlocked()
        {
            StartHost("");
            Connected module = await ConnectAsync("");
            // Swap in a host that has a full queue: a new runner on the same registry is not possible (sockets), so use the dispatcher directly.
            var service = new ModuleHostService(_registry, action => false, _dispatcher);
            var exhausted = await Assert.ThrowsAsync<RpcException>(async () => await service.SubmitVerdict(new VerdictRequest { ConnectionId = module.ConnectionId, EventId = "1", Decision = Decision.Allow }, new TestServerCallContext()));
            Assert.Equal(StatusCode.ResourceExhausted, exhausted.StatusCode);
        }

        private sealed class TestServerCallContext : ServerCallContext
        {
            protected override string MethodCore { get { return "test"; } }
            protected override string HostCore { get { return "test"; } }
            protected override string PeerCore { get { return "test"; } }
            protected override DateTime DeadlineCore { get { return DateTime.UtcNow.AddMinutes(1); } }
            protected override Metadata RequestHeadersCore { get { return new Metadata(); } }
            protected override CancellationToken CancellationTokenCore { get { return CancellationToken.None; } }
            protected override Metadata ResponseTrailersCore { get { return new Metadata(); } }
            protected override Status StatusCore { get; set; }
            protected override WriteOptions WriteOptionsCore { get; set; }
            protected override AuthContext AuthContextCore { get { return new AuthContext(null, new Dictionary<string, List<AuthProperty>>()); } }
            protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions options) { throw new NotSupportedException(); }
            protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) { return Task.CompletedTask; }
        }
    }

    /// <summary>Card P4-4: the conversions, the token bucket and the state store, without a socket.</summary>
    public class ModuleDispatchUnitTests
    {
        [Fact]
        public void StructsConvertToDictionariesAndBackWithNumbersAsDoubles()
        {
            var source = new Struct();
            source.Fields["name"] = Value.ForString("x");
            source.Fields["count"] = Value.ForNumber(3);
            source.Fields["on"] = Value.ForBool(true);
            source.Fields["nothing"] = Value.ForNull();
            source.Fields["inner"] = Value.ForStruct(new Struct { Fields = { { "a", Value.ForNumber(1.5) } } });
            source.Fields["list"] = Value.ForList(Value.ForString("p"), Value.ForNumber(2));

            Dictionary<string, object> map = StructConversion.ToDictionary(source);

            Assert.Equal("x", map["name"]);
            Assert.Equal(3.0, map["count"]);
            Assert.Equal(true, map["on"]);
            Assert.Null(map["nothing"]);
            Assert.Equal(1.5, ((Dictionary<string, object>)map["inner"])["a"]);
            Assert.Equal(new object[] { "p", 2.0 }, ((List<object>)map["list"]).ToArray());
            Struct again = StructConversion.FromDictionary(map);
            Assert.Equal(source, again);
        }

        [Fact]
        public void StructsConvertToBsonAndBack()
        {
            var source = new Struct();
            source.Fields["a"] = Value.ForNumber(2);
            source.Fields["b"] = Value.ForList(Value.ForString("x"), Value.ForBool(false));
            source.Fields["c"] = Value.ForStruct(new Struct { Fields = { { "d", Value.ForString("e") } } });

            BsonDocument document = StructConversion.ToBson(source);

            Assert.Equal(2.0, document["a"].AsDouble);
            Assert.Equal(2, document["b"].AsBsonArray.Count);
            Assert.Equal("e", document["c"]["d"].AsString);
            Assert.Equal(source, StructConversion.FromBson(document));
        }

        [Fact]
        public void TheTokenBucketAllowsABurstThenRefillsWithTime()
        {
            var clock = new FakeClock();
            var bucket = new TokenBucket(2, 3);

            Assert.True(bucket.TryTake(clock));
            Assert.True(bucket.TryTake(clock));
            Assert.True(bucket.TryTake(clock));
            Assert.False(bucket.TryTake(clock));
            clock.Advance(TimeSpan.FromSeconds(1)); // two tokens
            Assert.True(bucket.TryTake(clock));
            Assert.True(bucket.TryTake(clock));
            Assert.False(bucket.TryTake(clock));
            clock.Advance(TimeSpan.FromSeconds(60)); // capped at the burst
            Assert.True(bucket.TryTake(clock));
            Assert.True(bucket.TryTake(clock));
            Assert.True(bucket.TryTake(clock));
            Assert.False(bucket.TryTake(clock));
        }

        [Fact]
        public void AStoreReadsADocumentThatIsOnlyInTheRepository()
        {
            var repository = new FakeModuleStateRepo();
            repository.Docs["temple/k"] = new BsonDocument { { "_id", "temple/k" }, { "Document", new BsonDocument("a", 1) } };
            var store = new ModuleStateStore(repository, new ModuleHostOptions(), new FakeClock());

            BsonDocument document;
            Assert.True(store.TryGet("temple", "k", out document));
            Assert.Equal(1, document["a"].AsInt32);
            Assert.False(store.TryGet("temple", "missing", out document));
            Assert.False(store.TryGet("desert", "k", out document)); // another module's namespace
        }

        [Fact]
        public void AStoreWriteIsVisibleToTheNextReadBeforeTheDatabaseHasIt()
        {
            var repository = new FakeModuleStateRepo();
            var store = new ModuleStateStore(repository, new ModuleHostOptions(), new FakeClock());

            Assert.Null(store.Set("temple", "k", new BsonDocument("a", 1)));
            Assert.Null(store.Set("temple", "k", new BsonDocument("a", 2)));

            BsonDocument document;
            Assert.True(store.TryGet("temple", "k", out document));
            Assert.Equal(2, document["a"].AsInt32);
            Assert.Equal(2, repository.Docs["temple/k"]["Document"]["a"].AsInt32);
        }

        [Fact]
        public void ADefaultOptionsObjectCarriesTheFlaggedDefaults()
        {
            var options = new ModuleHostOptions();

            Assert.Equal(65536, options.StateMaxDocumentBytes);
            Assert.Equal(10, options.StateWritesPerSecond);
            Assert.Equal(20, options.StateWriteBurst);
            Assert.Equal(50, options.EmitPerSecond);
            Assert.Equal(100, options.EmitBurst);
            Assert.Equal(100, options.TimersPerModule);
            Assert.Equal(TimeSpan.FromMilliseconds(100), options.TimerMinimumDelay);
        }
    }
}
