using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MUD.Contracts;
using MUD.Core;
using MUD.Core.Components;
using MUD.Core.Events;
using MUD.Core.GameObjects;
using MUD.Core.Modules;
using MUD.Core.Repositories.Interfaces;
using MUD.CoreApi;
using MUD.Main;
using MUD.Modules.Sdk;
using Xunit;
using BehaviorRef = MUD.Core.Components.BehaviorRef;

namespace MUD.Tests
{
    /// <summary>
    /// Drives a module the way core does, without a database (owner decision D69: no hand-run modules, so the harness runs the module in
    /// process, through the SDK's own ModuleRunner, against core's real module host on a real Unix domain socket in a temporary directory).
    /// What is real: the run directory, Kestrel on modules.sock, the token and Hello handshake, the grants file, the Subscribe stream, Ping,
    /// the (Module, Type) dictionary, the Before chain with its deadline, verdicts, Emit, state, timers and the game-thread queue discipline.
    /// What is faked: MongoDB (state documents and entities live in memory) and the clock (time moves only when the test moves it).
    ///
    /// Typical use in a test:
    ///   using (var harness = new ModuleTestHarness("hello", "emit:message.send state timer.schedule"))
    ///   {
    ///       await harness.StartAsync(HelloModule.Define());
    ///       Room room = harness.NewRoom("test.hall");
    ///       harness.Attach(room, BehaviorHooks.LivingEnter, "HelloGreeter");
    ///       Mover ann = harness.Place(harness.NewRoom("test.start"), "ann");
    ///       await harness.MoveAsync(ann, room);
    ///       await harness.WaitForAsync(() => ann.Messages.Count > 0);
    ///   }
    ///
    /// A harness collects its own log lines (Log). The class lives in MUD.Tests because it needs core internals; module projects that want
    /// it reference MUD.Tests or copy it. Tests that use it belong in the "World construction" collection (World registers BSON class maps).
    /// </summary>
    public sealed class ModuleTestHarness : IDisposable
    {
        /// <summary>A living that records what it is told.</summary>
        public sealed class Mover : Living
        {
            public readonly List<string> Messages = new List<string>();
            public readonly List<OutputChannel> Channels = new List<OutputChannel>();
            public Mover(World world, string name) : base(world) { ShortDescription = name; Id = "living." + name; }
            public override void ReceiveMessage(string message) { lock (Messages) { Messages.Add(message); Channels.Add(DeliveryChannel); } }
            public string[] MessagesSoFar() { lock (Messages) { return Messages.ToArray(); } }
        }

        public sealed class FakeStateRepository : FakeRepo<ModuleStateDocument>, IModuleStateRepository { }

        private sealed class GameThread : IDisposable
        {
            private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new System.Collections.Concurrent.BlockingCollection<Action>();
            private readonly Thread _thread;

            public GameThread()
            {
                _thread = new Thread(() => { foreach (Action action in _queue.GetConsumingEnumerable()) { try { action(); } catch (Exception) { } } }) { IsBackground = true, Name = "harness-game-thread" };
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

            public void Dispose() { _queue.CompleteAdding(); }
        }

        private readonly string _directory;
        private readonly string _moduleName;
        private readonly List<string> _log = new List<string>();
        private readonly GameThread _game = new GameThread();
        private readonly Dictionary<string, GameObject> _entities = new Dictionary<string, GameObject>();
        private CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly ModuleHostOptions _options = new ModuleHostOptions();
        private ModuleHostRunner _runner;
        private RunDirectory _runDirectory;
        private Task<int> _moduleTask;

        /// <param name="moduleName">The module's name, which is also the grants-file key.</param>
        /// <param name="grantedCapabilities">The capabilities the grants file allows for the module, separated by spaces (D65).</param>
        public ModuleTestHarness(string moduleName, string grantedCapabilities, Action<ModuleHostOptions> configure = null)
        {
            _moduleName = moduleName;
            // Short path: a Unix socket path is limited to 107 bytes.
            _directory = Path.Combine(Path.GetTempPath(), "mt" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_directory);
            if (configure != null) { configure(_options); }

            string grantsPath = Path.Combine(_directory, "grants.yaml");
            File.WriteAllText(grantsPath, "modules:\n  " + moduleName + ":\n"
                + string.Join("", (grantedCapabilities ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(capability => "    - " + capability + "\n")));
            File.SetUnixFileMode(grantsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            // The fake clock starts at the real current time: a module computes a timer's due time from real time (ScheduleTimerAsync),
            // and core compares it with its own clock, so the two must agree at the start. After that only the test moves it.
            Clock = new FakeClock();
            Clock.Advance(DateTime.Now - Clock.Now);
            Tokens = new ModuleTokenRegistry();
            var grants = new ModuleGrants(grantsPath, Log);
            Assert.True(grants.Reload(), "The grants file was not read: " + string.Join("; ", LogLines()));
            Registry = new ModuleRegistry(Tokens, grants, ContractVersion.Current, _options, Log);

            World = new World(new MongoDB.Driver.MongoClient("mongodb://127.0.0.1:1"), new WorldSettings(), Clock);
            World.Events.Resolver = NoEventHandlers.Instance;
            StateRepository = new FakeStateRepository();
            var store = new ModuleStateStore(StateRepository, _options, Clock);
            Dispatcher = new ModuleDispatcher(Registry, World.Events, store, _options, Clock, id => { GameObject found; return _entities.TryGetValue(id, out found) ? found : null; }, Log);
            World.Events.Resolver = Dispatcher;
            ModuleEmitActions.Register(World.Events);

            _runDirectory = RunDirectory.Acquire(Path.Combine(_directory, "r"), Log);
            _runner = new ModuleHostRunner(_runDirectory, Registry, _game.Post, Log, Dispatcher);
            _runner.Start();
        }

        public FakeClock Clock { get; }
        public World World { get; }
        public ModuleTokenRegistry Tokens { get; }
        public ModuleRegistry Registry { get; }
        public ModuleDispatcher Dispatcher { get; }
        public FakeStateRepository StateRepository { get; }
        public string SocketPath { get { return _runDirectory.ModulesSocketPath; } }

        /// <summary>The module runner, once StartAsync has been called.</summary>
        public ModuleRunner Runner { get; private set; }

        /// <summary>Completes with the runner's exit code when the module's connection loop ends.</summary>
        public Task<int> ModuleExit { get { return _moduleTask; } }

        /// <summary>The module's connection on the core side, once it has said Hello.</summary>
        public ModuleConnection Connection { get { return Registry.FindByModule(_moduleName); } }

        public void Log(string line) { lock (_log) { _log.Add(line); } }

        public string[] LogLines() { lock (_log) { return _log.ToArray(); } }

        // ---- the module ----

        /// <summary>Issues the per-spawn token (D17), starts the module in process, and waits until core has accepted its Ready call.</summary>
        public async Task StartAsync(ModuleDefinition definition, TimeSpan? pingInterval = null)
        {
            var settings = new ModuleRunnerSettings
            {
                ModuleName = definition.Name,
                Token = Tokens.Issue(definition.Name),
                SocketPath = SocketPath,
                PingInterval = pingInterval ?? TimeSpan.FromSeconds(1),
                Log = line => Log("[module:" + definition.Name + "] " + line)
            };
            Runner = new ModuleRunner(definition, settings);
            if (_stop.IsCancellationRequested) { _stop = new CancellationTokenSource(); } // a restart after StopModuleAsync
            _moduleTask = Runner.RunAsync(_stop.Token);
            Task finished = await Task.WhenAny(Runner.ReadyTask, _moduleTask, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(Runner.ReadyTask.IsCompletedSuccessfully, "The module did not become ready. Log: " + string.Join(" | ", LogLines()));
            await WaitForAsync(() => Connection != null && Connection.IsReady, "core marking the module ready");
        }

        /// <summary>Stops the module and returns its exit code (ModuleRunner.ExitOk when it was stopped on purpose).</summary>
        public async Task<int> StopModuleAsync()
        {
            _stop.Cancel();
            return await _moduleTask;
        }

        // ---- the world ----

        public Room NewRoom(string id)
        {
            var room = new Room(new FakeRoomRepo()) { Id = id, ShortDescription = id, LongDescription = id + " long" };
            _entities[id] = room;
            return room;
        }

        /// <summary>Puts a living with the given name into the room (id "living.&lt;name&gt;").</summary>
        public Mover Place(Room room, string name)
        {
            var mover = new Mover(World, name);
            room.EnterRoom(mover, name + " arrives");
            mover.CurrentLocation = room;
            mover.CurrentLocationId = room.Id;
            _entities[mover.Id] = mover;
            return mover;
        }

        /// <summary>Adds a behavior entry for the module under test to the entity.</summary>
        public void Attach(GameObject entity, string hook, string type, BsonDocument config = null, int priority = 0)
        {
            ((IBehaviorHolder)entity).Behaviors.Add(new BehaviorRef(hook, _moduleName, type, priority, config));
        }

        /// <summary>Starts a move on the game thread, as a walk would (the Before chain runs when an entry is present).</summary>
        public void StartMove(Mover mover, Room destination)
        {
            _game.Run(() => { mover.MoveToRoom(destination, null, null); return 0; });
        }

        /// <summary>Moves the living, waits for the move to commit, then delivers the After notifications.</summary>
        public async Task MoveAsync(Mover mover, Room destination)
        {
            StartMove(mover, destination);
            await WaitForAsync(() => ReferenceEquals(_game.Run(() => mover.CurrentLocation), destination), "the move to commit");
            Pump();
        }

        /// <summary>Processes queued events on the game thread (After notifications, emitted events).</summary>
        public int Pump() { return _game.Run(() => World.Events.Pump(1000)); }

        /// <summary>Moves the fake clock and runs the module timers' tick on the game thread.</summary>
        public void AdvanceClock(TimeSpan by)
        {
            Clock.Advance(by);
            _game.Run(() => { Dispatcher.Tick(); return 0; });
        }

        public T OnGameThread<T>(Func<T> work) { return _game.Run(work); }

        public async Task WaitForAsync(Func<bool> condition, string what = "the condition", int seconds = 10)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) { Assert.True(false, "Timed out waiting for " + what + ". Log: " + string.Join(" | ", LogLines())); }
                await Task.Delay(20);
            }
        }

        public void Dispose()
        {
            try { _stop.Cancel(); } catch (Exception) { }
            try { if (_moduleTask != null) { _moduleTask.Wait(TimeSpan.FromSeconds(5)); } } catch (Exception) { }
            try { _runner?.Dispose(); } catch (Exception) { }
            try { _runDirectory?.Dispose(); } catch (Exception) { }
            _game.Dispose();
            try { Directory.Delete(_directory, true); } catch (Exception) { }
        }
    }
}
