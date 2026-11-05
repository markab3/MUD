using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using MongoDB.Bson;
using MUD.Contracts;
using MUD.Core.Components;
using MUD.Core.GameObjects;
using MUD.Core.Modules;
using MUD.CoreApi;
using MUD.Modules.Hello;
using MUD.Modules.Sdk;
using Xunit;
using Mover = MUD.Tests.ModuleTestHarness.Mover;

namespace MUD.Tests
{
    /// <summary>
    /// Card P4-5: the SDK, the Hello module and the harness, over a real Unix domain socket in a temporary directory with core's real
    /// module host. MongoDB is not involved (state documents are an in-memory fake repository).
    /// </summary>
    [Collection("World construction")] // World registers BSON class maps; constructing it in parallel races
    public class HelloModuleTests
    {
        private const string AllCapabilities = "emit:message.send state timer.schedule";

        // ---- the SDK payload helpers and the manifest ----

        [Fact]
        public void PayloadsBuildAndReadGenericMaps()
        {
            Struct map = Payloads.Map(("text", "hi"), ("count", 3), ("on", true), ("list", new[] { "a", "b" }), ("nested", Payloads.Map(("x", 1.5))), ("none", null));
            Assert.Equal("hi", Payloads.GetText(map, "text"));
            Assert.Equal(3, Payloads.GetNumber(map, "count"));
            Assert.True(map.Fields["on"].BoolValue);
            Assert.Equal(2, map.Fields["list"].ListValue.Values.Count);
            Assert.Equal(1.5, Payloads.GetNumber(Payloads.GetMap(map, "nested"), "x"));
            Assert.Equal(Value.KindOneofCase.NullValue, map.Fields["none"].KindCase);
            Assert.Null(Payloads.GetText(map, "count")); // not text
            Assert.Equal(7, Payloads.GetNumber(map, "missing", 7));
            Assert.Throws<ArgumentException>(() => Payloads.ToValue(new object()));
        }

        [Fact]
        public void TheManifestAndTheDescriptionComeFromTheDefinition()
        {
            ModuleDefinition definition = HelloModule.Define();
            Manifest manifest = definition.BuildManifest();
            Assert.Equal("hello", manifest.Name);
            BehaviorDecl behavior = Assert.Single(manifest.Behaviors);
            Assert.Equal("HelloGreeter", behavior.Type);
            Assert.Equal(new[] { "living.enter" }, behavior.Hooks.ToArray());
            Assert.Equal(new[] { "emit:message.send", "state", "timer.schedule" }, manifest.Capabilities.ToArray());
            Assert.Empty(manifest.RequiredCapabilities);

            // The description is exactly what core's scan parses (D67).
            ModuleDescription description = ModuleDescription.Parse(ModuleRunner.Describe(definition), out string problem);
            Assert.Null(problem);
            Assert.Equal("hello", description.Name);
            Assert.Equal("1.0.0", description.Version);
            Assert.Equal(ContractVersion.Current, description.ContractVersion);
            Assert.Equal(new[] { "HelloGreeter" }, description.BehaviorTypes.ToArray());
            Assert.False(description.Required);
        }

        [Fact]
        public void ARequiredCapabilityIsAlsoRequestedAndARequiredModuleSaysSo()
        {
            ModuleDefinition definition = ModuleDefinition.Create("m", "1").Request("state").RequireCapabilities("timer.schedule", "state").MarkRequired();
            Manifest manifest = definition.BuildManifest();
            Assert.Equal(new[] { "state", "timer.schedule" }, manifest.Capabilities.ToArray());
            Assert.Equal(new[] { "timer.schedule", "state" }, manifest.RequiredCapabilities.ToArray());
            Assert.Contains("\"required\":true", ModuleRunner.Describe(definition));
        }

        [Fact]
        public void TheEventBuildersMakeTheRecordsCoreCarriesOut()
        {
            EventRecord message = ModuleEvents.Message("living.ann", "hi", ModuleEvents.Combat);
            Assert.Equal("message.send", message.Name);
            Assert.Equal(new[] { "living.ann" }, message.Targets.ToArray());
            Assert.Equal("combat", Payloads.GetText(message.Payload, "channel"));

            Assert.Null(Payloads.GetText(ModuleEvents.Message("a", "b").Payload, "channel")); // null channel: core defaults to system (D44)

            EventRecord component = ModuleEvents.SetComponent("room.1", "desert.storm", Payloads.Map(("level", 2)));
            Assert.Equal("component.set", component.Name);
            Assert.Equal("desert.storm", Payloads.GetText(component.Payload, "component"));
            Assert.Equal(2, Payloads.GetNumber(Payloads.GetMap(component.Payload, "data"), "level"));

            EventRecord move = ModuleEvents.MoveEntity("living.ann", "room.2", "trap");
            Assert.Equal(new[] { "living.ann", "room.2" }, move.Targets.ToArray());
            Assert.Equal("trap", Payloads.GetText(move.Payload, "reason"));

            EventRecord feature = ModuleEvents.AddFeature("room.1", "a wall of sand", handles: new[] { "wall", "sand" });
            Assert.Equal("feature.add", feature.Name);
            Assert.Equal(2, feature.Payload.Fields["handles"].ListValue.Values.Count);
            Assert.Equal("feature.remove", ModuleEvents.RemoveFeature("room.1", "a wall of sand").Name);

            EventRecord behavior = ModuleEvents.AddBehavior("room.1", "living.enter", "temple", "Trap", 5);
            Assert.Equal("behavior.add", behavior.Name);
            Assert.Equal(5, Payloads.GetNumber(behavior.Payload, "priority"));
            Assert.Equal("behavior.remove", ModuleEvents.RemoveBehavior("room.1", "living.enter", "temple", "Trap").Name);
        }

        // ---- the Hello module against core's real host ----

        [Fact]
        public async Task HelloGreetsAMoverAndCountsTheMoveAndKeepsTheCountInItsStateDocument()
        {
            using (var harness = new ModuleTestHarness("hello", AllCapabilities))
            {
                await harness.StartAsync(HelloModule.Define());
                Room hall = harness.NewRoom("test.hall");
                harness.Attach(hall, BehaviorHooks.LivingEnter, HelloModule.BehaviorType, new BsonDocument { { "greeting", "Welcome" } });
                Mover ann = harness.Place(harness.NewRoom("test.start"), "ann");

                await harness.MoveAsync(ann, hall);
                await harness.WaitForAsync(() => harness.Pump() >= 0 && ann.MessagesSoFar().Any(m => m.Contains("move 1")), "the greeting");

                Assert.Contains("Welcome, ann. That is move 1 I have seen.", ann.MessagesSoFar());
                Assert.Equal(OutputChannel.System, ann.Channels.Last());
                Assert.Same(hall, harness.OnGameThread(() => ann.CurrentLocation));

                await harness.WaitForAsync(() => harness.StateRepository.Docs.ContainsKey("hello/moves"), "the state document");
                Assert.Equal(1, harness.StateRepository.Docs["hello/moves"]["Document"]["count"].ToInt64());
            }
        }

        [Fact]
        public async Task TheCountSurvivesARestartOfTheModule()
        {
            using (var harness = new ModuleTestHarness("hello", AllCapabilities))
            {
                var first = new HelloModule();
                await harness.StartAsync(HelloModule.Define(first));
                Room hall = harness.NewRoom("test.hall");
                harness.Attach(hall, BehaviorHooks.LivingEnter, HelloModule.BehaviorType);
                Room start = harness.NewRoom("test.start");
                Mover ann = harness.Place(start, "ann");
                await harness.MoveAsync(ann, hall);
                await harness.WaitForAsync(() => first.Moves == 1 && harness.StateRepository.Docs.ContainsKey("hello/moves"), "the first count");

                Assert.Equal(ModuleRunner.ExitOk, await harness.StopModuleAsync());
                await harness.WaitForAsync(() => harness.Connection == null, "core noticing the stream ended");

                var second = new HelloModule();
                await harness.StartAsync(HelloModule.Define(second));
                Assert.Equal(1, second.Moves); // restored from the state document in OnStart (D13)
                Assert.Contains(harness.LogLines(), line => line.Contains("Restored 1 counted moves."));
            }
        }

        [Fact]
        public async Task ATimerFiresToTheModuleAndTheModuleSchedulesTheNextOne()
        {
            using (var harness = new ModuleTestHarness("hello", AllCapabilities))
            {
                await harness.StartAsync(HelloModule.Define(new HelloModule(TimeSpan.FromSeconds(30))));
                await harness.WaitForAsync(() => harness.Dispatcher.TimerCount(harness.Connection) == 1, "the timer scheduled in OnStart");

                harness.AdvanceClock(TimeSpan.FromSeconds(31));
                await harness.WaitForAsync(() => harness.LogLines().Any(line => line.Contains("Hello has counted 0 moves.")), "timer.fired handled");
                await harness.WaitForAsync(() => harness.Dispatcher.TimerCount(harness.Connection) == 1, "the next timer");
            }
        }

        [Fact]
        public async Task WithAPartialGrantTheModuleStillRunsAndSkipsWhatIsNotAllowed()
        {
            using (var harness = new ModuleTestHarness("hello", "emit:message.send"))
            {
                await harness.StartAsync(HelloModule.Define());
                Assert.Contains(harness.LogLines(), line => line.Contains("WARNING") && line.Contains("state") && line.Contains("timer.schedule"));
                Room hall = harness.NewRoom("test.hall");
                harness.Attach(hall, BehaviorHooks.LivingEnter, HelloModule.BehaviorType);
                Mover ann = harness.Place(harness.NewRoom("test.start"), "ann");

                await harness.MoveAsync(ann, hall);
                await harness.WaitForAsync(() => harness.Pump() >= 0 && ann.MessagesSoFar().Any(m => m.Contains("move 1")), "the greeting");
                Assert.Empty(harness.StateRepository.Docs);
                Assert.Equal(0, harness.Dispatcher.TimerCount(harness.Connection));
            }
        }

        [Fact]
        public async Task AMissingCapabilityIsRefusedWithAnErrorThatNamesIt()
        {
            using (var harness = new ModuleTestHarness("hello", ""))
            {
                var context = await StartPlainAsync(harness);
                ModuleCallException ex = await Assert.ThrowsAsync<ModuleCallException>(() => context.SetStateAsync("k", Payloads.Map(("a", 1))));
                Assert.Contains("Missing capability 'state'", ex.Message);
                ex = await Assert.ThrowsAsync<ModuleCallException>(() => context.ScheduleTimerAsync("t", TimeSpan.FromSeconds(1)));
                Assert.Contains("Missing capability 'timer.schedule'", ex.Message);
                IReadOnlyList<EmitRefusal> refusals = await context.SendMessageAsync("living.nobody", "x");
                Assert.Contains("emit:message.send", Assert.Single(refusals).Reason);
            }
        }

        [Fact]
        public async Task AVerdictFromTheSdkCanDenyAMoveAndEmitEventsTogether()
        {
            using (var harness = new ModuleTestHarness("hello", "emit:message.send"))
            {
                ModuleDefinition definition = ModuleDefinition.Create("hello", "1")
                    .Request("emit:message.send")
                    .Behavior("Gate", new[] { "living.enter" }, (call, context) => Task.FromResult(
                        call.IsBefore
                            ? BehaviorResult.Deny("The gate is shut.").WithEmitted(ModuleEvents.Message(call.Party("mover").EntityId, "You hear a bolt slide.", ModuleEvents.Room))
                            : BehaviorResult.Allow()));
                await harness.StartAsync(definition);
                Room hall = harness.NewRoom("test.hall");
                harness.Attach(hall, BehaviorHooks.LivingEnter, "Gate");
                Room start = harness.NewRoom("test.start");
                Mover ann = harness.Place(start, "ann");

                harness.StartMove(ann, hall);
                await harness.WaitForAsync(() => harness.Pump() >= 0 && ann.MessagesSoFar().Contains("The gate is shut.") && ann.MessagesSoFar().Contains("You hear a bolt slide."), "the denial and the emitted message");
                Assert.Same(start, harness.OnGameThread(() => ann.CurrentLocation)); // a Deny vetoes the move (D6); the emitted event still applied (D8)
                Assert.Contains(OutputChannel.Room, ann.Channels);
            }
        }

        [Fact]
        public async Task AHandlerThatThrowsSendsNoVerdictAndTheModuleKeepsRunning()
        {
            using (var harness = new ModuleTestHarness("hello", "", options => options.GameThreadCallTimeout = TimeSpan.FromSeconds(5)))
            {
                int calls = 0;
                ModuleDefinition definition = ModuleDefinition.Create("hello", "1")
                    .Behavior("Broken", new[] { "living.enter" }, (call, context) =>
                    {
                        if (call.IsBefore && ++calls == 1) { throw new InvalidOperationException("boom"); }
                        return Task.FromResult(BehaviorResult.Allow());
                    });
                await harness.StartAsync(definition);
                Room hall = harness.NewRoom("test.hall");
                harness.Attach(hall, BehaviorHooks.LivingEnter, "Broken");
                Mover ann = harness.Place(harness.NewRoom("test.start"), "ann");

                harness.StartMove(ann, hall);
                await harness.WaitForAsync(() => harness.LogLines().Any(line => line.Contains("failed and sent no verdict: boom")), "the logged failure");
                // No verdict came, so the chain runs to its deadline and then follows the fail policy: living.enter fails open (D60).
                harness.Clock.Advance(TimeSpan.FromMilliseconds(200));
                await harness.WaitForAsync(() => { harness.Pump(); return ReferenceEquals(harness.OnGameThread(() => ann.CurrentLocation), hall); }, "the move after the deadline");

                // The module is still connected and answers the next delivery.
                Mover bob = harness.Place(harness.NewRoom("test.other"), "bob");
                await harness.MoveAsync(bob, hall);
                Assert.Equal(2, calls);
                Assert.NotNull(harness.Connection);
            }
        }

        [Fact]
        public async Task CoreRefusesAModuleWithTheWrongTokenAndTheRunnerReportsIt()
        {
            using (var harness = new ModuleTestHarness("hello", ""))
            {
                var settings = new ModuleRunnerSettings { ModuleName = "hello", Token = "not-the-token", SocketPath = harness.SocketPath, Log = harness.Log };
                harness.Tokens.Issue("hello");
                int exit = await new ModuleRunner(HelloModule.Define(), settings).RunAsync(System.Threading.CancellationToken.None);
                Assert.Equal(ModuleRunner.ExitRefused, exit);
                Assert.Contains(harness.LogLines(), line => line.Contains("Core refused this module: Authentication failed"));
            }
        }

        [Fact]
        public async Task TheRunnerExitsWhenCoreStopsTheStream()
        {
            using (var harness = new ModuleTestHarness("hello", ""))
            {
                await harness.StartAsync(HelloModule.Define());
                harness.Registry.DisconnectAll("core is stopping");
                Task finished = await Task.WhenAny(harness.ModuleExit, Task.Delay(TimeSpan.FromSeconds(10)));
                Assert.Same(harness.ModuleExit, finished);
                Assert.Equal(ModuleRunner.ExitConnectionLost, harness.ModuleExit.Result);
                Assert.Contains(harness.LogLines(), line => line.Contains("The connection to core was lost"));
            }
        }

        // ---- the Hello program as core starts it ----

        [Fact]
        public void TheHelloProgramPrintsItsDescriptionWithoutConnectingToCore()
        {
            string assembly = typeof(HelloModule).Assembly.Location;
            var startInfo = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            string dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrEmpty(dotnetRoot) && File.Exists(Path.Combine(dotnetRoot, "dotnet"))) { startInfo.FileName = Path.Combine(dotnetRoot, "dotnet"); }
            startInfo.ArgumentList.Add(assembly);
            startInfo.ArgumentList.Add("--describe");
            startInfo.Environment.Remove("MUD_MODULE_TOKEN");
            startInfo.Environment.Remove("MUD_CORE_SOCKET");
            using (Process process = Process.Start(startInfo))
            {
                string output = process.StandardOutput.ReadToEnd();
                Assert.True(process.WaitForExit(30000));
                Assert.Equal(0, process.ExitCode);
                using (JsonDocument document = JsonDocument.Parse(output))
                {
                    Assert.Equal("hello", document.RootElement.GetProperty("name").GetString());
                    Assert.Equal((int)ContractVersion.Current, document.RootElement.GetProperty("contract_version").GetInt32());
                }
            }
        }

        [Fact]
        public void TheHelloProgramRefusesToRunWithoutCore()
        {
            string assembly = typeof(HelloModule).Assembly.Location;
            var startInfo = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            string dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrEmpty(dotnetRoot) && File.Exists(Path.Combine(dotnetRoot, "dotnet"))) { startInfo.FileName = Path.Combine(dotnetRoot, "dotnet"); }
            startInfo.ArgumentList.Add(assembly);
            startInfo.Environment.Remove("MUD_MODULE_TOKEN");
            startInfo.Environment.Remove("MUD_CORE_SOCKET");
            using (Process process = Process.Start(startInfo))
            {
                string error = process.StandardError.ReadToEnd();
                Assert.True(process.WaitForExit(30000));
                Assert.Equal(ModuleRunner.ExitBadSettings, process.ExitCode);
                Assert.Contains("started by core", error);
            }
        }

        // ---- the whole path: core's supervisor starts the real Hello program as a child process ----

        [Fact]
        public async Task CoreStartsTheHelloProgramAsAChildProcessAndItGreetsAMoverOverTheRealSocket()
        {
            string modulesDirectory = Path.Combine(Path.GetTempPath(), "mm" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string folder = Path.Combine(modulesDirectory, "hello");
            Directory.CreateDirectory(folder);
            try
            {
                // What `dotnet publish` of MUD.Modules.Hello puts in modules/hello/: the program, named after the folder, and its libraries.
                string output = Path.GetDirectoryName(typeof(HelloModule).Assembly.Location);
                foreach (string file in Directory.GetFiles(output))
                {
                    string name = Path.GetFileName(file);
                    bool needed = name.StartsWith("hello") || name == "MUD.Contracts.dll" || name == "MUD.Modules.Sdk.dll" || name == "Google.Protobuf.dll"
                        || name.StartsWith("Grpc") || name.StartsWith("Microsoft.Extensions.");
                    if (needed) { File.Copy(file, Path.Combine(folder, name)); }
                }
                string executable = Path.Combine(folder, "hello");
                Assert.True(File.Exists(executable), "The published program must be named after its folder.");

                using (var harness = new ModuleTestHarness("hello", AllCapabilities))
                {
                    var options = new ModuleSupervisorOptions { ModulesDirectory = modulesDirectory, TickInterval = TimeSpan.FromMilliseconds(50), TerminateGrace = TimeSpan.FromSeconds(3) };
                    using (var supervisor = new ModuleSupervisor(harness.Registry, options, harness.SocketPath, harness.Log))
                    {
                        supervisor.Start();
                        await harness.WaitForAsync(() => supervisor.GetStatuses().Any(s => s.Name == "hello" && s.State == ModuleState.Ready), "the Hello program to be Ready", 60);

                        ModuleStatus status = supervisor.GetStatuses().Single(s => s.Name == "hello");
                        Assert.Equal(new[] { "HelloGreeter" }, status.BehaviorTypes.ToArray());
                        Assert.True(status.ProcessId > 0);

                        Room hall = harness.NewRoom("test.hall");
                        harness.Attach(hall, BehaviorHooks.LivingEnter, HelloModule.BehaviorType);
                        Mover ann = harness.Place(harness.NewRoom("test.start"), "ann");
                        await harness.MoveAsync(ann, hall);
                        await harness.WaitForAsync(() => harness.Pump() >= 0 && ann.MessagesSoFar().Any(m => m.Contains("Hello, ann. That is move 1")), "the greeting from the child process");
                        await harness.WaitForAsync(() => harness.StateRepository.Docs.ContainsKey("hello/moves"), "the state document");
                        Assert.Contains(harness.LogLines(), line => line.StartsWith("[module:hello]") || line.Contains("Module 'hello' started as process"));

                        supervisor.Stop();
                        Assert.True(ProcessHasEnded(status.ProcessId), "Core stopping the supervisor ends the module process.");
                    }
                }
            }
            finally
            {
                try { Directory.Delete(modulesDirectory, true); } catch (Exception) { }
            }
        }

        private static bool ProcessHasEnded(int processId)
        {
            for (int i = 0; i < 100; i++)
            {
                try
                {
                    string stat = File.ReadAllText("/proc/" + processId + "/stat");
                    int close = stat.LastIndexOf(')');
                    if (stat[close + 2] == 'Z') { return true; }
                }
                catch (Exception) { return true; }
                System.Threading.Thread.Sleep(50);
            }
            return false;
        }

        // Starts a module with no behaviors and hands back its context.
        private static async Task<ModuleContext> StartPlainAsync(ModuleTestHarness harness)
        {
            await harness.StartAsync(ModuleDefinition.Create("hello", "1"));
            return harness.Runner.Context;
        }
    }
}
