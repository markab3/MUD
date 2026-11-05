using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using MUD.Contracts;
using MUD.Core;
using MUD.Core.Commands;
using MUD.Core.Modules;
using MUD.Main;
using Xunit;

namespace MUD.Tests
{
    /// <summary>
    /// Card P4-3: the module supervisor (D20, D21, D67, D68, D69, D70). The modules here are small shell scripts in a temporary
    /// modules directory, so spawning, the environment, --describe, exits, stale processes and the required gate run for real.
    /// Mongo is not involved. The Ready step is played by the test over a real Unix socket using the token the script wrote out.
    /// </summary>
    public class ModuleSupervisorTests : IDisposable
    {
        private readonly string _directory;
        private readonly string _modulesDirectory;
        private readonly List<string> _log = new List<string>();
        private readonly List<IDisposable> _cleanup = new List<IDisposable>();

        public ModuleSupervisorTests()
        {
            // Short path: a Unix socket path is limited to 107 bytes.
            _directory = Path.Combine(Path.GetTempPath(), "ms" + Guid.NewGuid().ToString("N").Substring(0, 8));
            _modulesDirectory = Path.Combine(_directory, "modules");
            Directory.CreateDirectory(_modulesDirectory);
        }

        public void Dispose()
        {
            foreach (IDisposable item in _cleanup) { try { item.Dispose(); } catch (Exception) { } }
            try { Directory.Delete(_directory, true); } catch (Exception) { }
        }

        private static string Describe(string name, uint contractVersion = 1, bool required = false, string extra = "")
        {
            return "{\"name\":\"" + name + "\",\"version\":\"1.0\",\"contract_version\":" + contractVersion + ",\"required\":" + (required ? "true" : "false") + extra + "}";
        }

        /// <summary>Writes an executable shell script as modules/&lt;name&gt;/&lt;name&gt;. The body runs for every start that is not --describe.</summary>
        private string WriteModule(string name, string describeJson, string runBody)
        {
            string folder = Path.Combine(_modulesDirectory, name);
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, name);
            string script = "#!/bin/bash\n"
                + "if [ \"$1\" = \"--describe\" ]; then\n"
                + "  echo x >> \"" + Path.Combine(_directory, name + ".describe-count") + "\"\n"
                + "  cat <<'JSON'\n" + describeJson + "\nJSON\n  exit 0\nfi\n"
                + runBody + "\n";
            File.WriteAllText(path, script);
            File.SetUnixFileMode(path, (UnixFileMode)0x1ED); // 0755
            return path;
        }

        private int DescribeRuns(string name)
        {
            string path = Path.Combine(_directory, name + ".describe-count");
            return File.Exists(path) ? File.ReadAllLines(path).Length : 0;
        }

        private ModuleSupervisorOptions FastOptions()
        {
            return new ModuleSupervisorOptions
            {
                ModulesDirectory = _modulesDirectory,
                StartupTimeout = TimeSpan.FromSeconds(30),
                DescribeTimeout = TimeSpan.FromSeconds(10),
                TerminateGrace = TimeSpan.FromSeconds(2),
                RestartBackoff = new[] { TimeSpan.FromMilliseconds(50) },
                CrashLoopMaxCrashes = 3,
                CrashLoopWindow = TimeSpan.FromMinutes(5),
                PingTimeout = TimeSpan.FromSeconds(30),
                TickInterval = TimeSpan.FromMilliseconds(20)
            };
        }

        private ModuleSupervisor NewSupervisor(ModuleSupervisorOptions options, out ModuleRegistry registry, string grantsYaml = "modules: {}\n")
        {
            string grantsPath = Path.Combine(_directory, "grants.yaml");
            File.WriteAllText(grantsPath, grantsYaml);
            File.SetUnixFileMode(grantsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var grants = new ModuleGrants(grantsPath, _log.Add);
            grants.Reload();
            registry = new ModuleRegistry(new ModuleTokenRegistry(), grants, ContractVersion.Current, new ModuleHostOptions(), _log.Add);
            var supervisor = new ModuleSupervisor(registry, options, Path.Combine(_directory, "modules.sock"), _log.Add);
            _cleanup.Add(supervisor);
            return supervisor;
        }

        private static bool WaitFor(Func<bool> condition, int seconds = 15)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) { return false; }
                Thread.Sleep(20);
            }
            return true;
        }

        private static ModuleStatus StatusOf(ModuleSupervisor supervisor, string name)
        {
            return supervisor.GetStatuses().FirstOrDefault(s => s.Name == name);
        }

        private string LogText() { lock (_log) { return string.Join("\n", _log.ToArray()); } }

        // ---- --describe parsing (D67) ----

        [Fact]
        public void DescribeOutputIsParsedAndRequiredDefaultsToFalse()
        {
            string problem;
            ModuleDescription full = ModuleDescription.Parse("{\"name\":\"desert\",\"version\":\"2.1\",\"contract_version\":1,\"arguments\":[\"--quiet\"],\"behavior_types\":[\"DesertStorm\"],\"required\":true}", out problem);
            Assert.Null(problem);
            Assert.Equal("desert", full.Name);
            Assert.Equal("2.1", full.Version);
            Assert.Equal(1u, full.ContractVersion);
            Assert.Equal(new[] { "--quiet" }, full.Arguments);
            Assert.Equal(new[] { "DesertStorm" }, full.BehaviorTypes);
            Assert.True(full.Required);

            ModuleDescription minimal = ModuleDescription.Parse(Describe("temple"), out problem);
            Assert.False(minimal.Required);
            Assert.Empty(minimal.Arguments);
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("[1,2]")]
        [InlineData("{\"version\":\"1\",\"contract_version\":1}")]
        [InlineData("{\"name\":\"a\"}")]
        [InlineData("{\"name\":\"a\",\"contract_version\":\"1\"}")]
        [InlineData("{\"name\":\"a\",\"contract_version\":1,\"required\":\"yes\"}")]
        [InlineData("{\"name\":\"a\",\"contract_version\":1,\"arguments\":\"x\"}")]
        public void BadDescribeOutputIsRefusedWithAReason(string json)
        {
            string problem;
            Assert.Null(ModuleDescription.Parse(json, out problem));
            Assert.False(string.IsNullOrEmpty(problem));
        }

        // ---- stale processes (D68) ----

        [Fact]
        public void StaleMatchingUsesTheFullPathAndIgnoresADeletedSuffix()
        {
            string module = "/srv/mud/modules/desert/desert";
            Assert.True(StaleModuleProcesses.Matches(module, new[] { module }, module));
            Assert.True(StaleModuleProcesses.Matches(module + " (deleted)", new[] { module }, module));
            Assert.False(StaleModuleProcesses.Matches("/other/desert", new[] { "/other/desert" }, module)); // bare name is not enough
            Assert.False(StaleModuleProcesses.Matches("/srv/mud/modules/desert/desert2", new string[0], module));
        }

        [Fact]
        public void StaleMatchingFindsADotnetHostedModuleByTheFullPathOnTheCommandLine()
        {
            string module = "/srv/mud/modules/desert/desert";
            string dotnet = "/usr/share/dotnet/dotnet";
            Assert.True(StaleModuleProcesses.Matches(dotnet, new[] { "dotnet", module + ".dll" }, module));
            Assert.False(StaleModuleProcesses.Matches(dotnet, new[] { "dotnet", "exec", module + ".dll" }, module)); // "exec" is not a path: not matched
            Assert.False(StaleModuleProcesses.Matches(dotnet, new[] { "dotnet", "desert.dll" }, module)); // relative path never matches
            Assert.False(StaleModuleProcesses.Matches(dotnet, new[] { "dotnet", "/elsewhere/desert.dll" }, module));
            // Only the dotnet host gets the command-line test: an editor with the file open is not a module.
            Assert.False(StaleModuleProcesses.Matches("/usr/bin/vim", new[] { "vim", module + ".dll" }, module));
        }

        [Fact]
        public void AStaleProcessRunningTheSameExecutableIsEndedAndOthersAreLeftAlone()
        {
            string stalePath = Path.Combine(_directory, "stalesleep");
            string otherPath = Path.Combine(_directory, "othersleep");
            File.Copy("/usr/bin/sleep", stalePath);
            File.Copy("/usr/bin/sleep", otherPath);
            File.SetUnixFileMode(stalePath, (UnixFileMode)0x1ED);
            File.SetUnixFileMode(otherPath, (UnixFileMode)0x1ED);
            Process stale = Process.Start(stalePath, "60");
            Process other = Process.Start(otherPath, "60");
            try
            {
                Assert.Contains(stale.Id, StaleModuleProcesses.Find(stalePath, Environment.ProcessId));
                int count = StaleModuleProcesses.Terminate(stalePath, TimeSpan.FromSeconds(3), _log.Add);
                Assert.Equal(1, count);
                Assert.True(stale.WaitForExit(5000));
                Assert.False(other.HasExited);
            }
            finally
            {
                try { stale.Kill(); } catch (Exception) { }
                try { other.Kill(); } catch (Exception) { }
                stale.Dispose();
                other.Dispose();
            }
        }

        [Fact]
        public void AStaleProcessThatIgnoresTheTerminateSignalIsKilledAfterTheGracePeriod()
        {
            // An ignored signal stays ignored across exec, so this copy of sleep never obeys the terminate signal.
            string path = Path.Combine(_directory, "stubbornsleep");
            File.Copy("/usr/bin/sleep", path);
            File.SetUnixFileMode(path, (UnixFileMode)0x1ED);
            var launch = new ProcessStartInfo("/bin/bash") { UseShellExecute = false };
            launch.ArgumentList.Add("-c");
            launch.ArgumentList.Add("trap '' TERM; exec \"$0\" 60");
            launch.ArgumentList.Add(path);
            Process stale = Process.Start(launch);
            try
            {
                Assert.True(WaitFor(() => StaleModuleProcesses.Find(path, Environment.ProcessId).Contains(stale.Id)));
                Assert.Equal(1, StaleModuleProcesses.Terminate(path, TimeSpan.FromMilliseconds(400), _log.Add));
                Assert.True(stale.WaitForExit(5000));
                Assert.Contains("killing it", LogText());
            }
            finally { try { stale.Kill(true); } catch (Exception) { } stale.Dispose(); }
        }

        // ---- scanning and the cache (D67) ----

        [Fact]
        public void AScanRunsDescribeOnlyOnNewOrChangedExecutables()
        {
            string path = WriteModule("quiet", Describe("quiet"), "sleep 60");
            ModuleRegistry registry;
            ModuleSupervisor supervisor = NewSupervisor(FastOptions(), out registry);

            supervisor.RequestRescan();
            supervisor.Start();
            Assert.True(WaitFor(() => DescribeRuns("quiet") == 1 && StatusOf(supervisor, "quiet") != null && StatusOf(supervisor, "quiet").Version == "1.0"));

            supervisor.RequestRescan();
            Thread.Sleep(500);
            Assert.Equal(1, DescribeRuns("quiet")); // cached: untouched

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5)); // modified time differs
            supervisor.RequestRescan();
            Assert.True(WaitFor(() => DescribeRuns("quiet") == 2));
        }

        [Fact]
        public void AContractMismatchIsRefusedAtScanTimeAndTheModuleIsNotStarted()
        {
            WriteModule("old", Describe("old", contractVersion: 7), "sleep 60");
            ModuleRegistry registry;
            ModuleSupervisor supervisor = NewSupervisor(FastOptions(), out registry);
            supervisor.Start();
            Assert.True(WaitFor(() => StatusOf(supervisor, "old") != null && StatusOf(supervisor, "old").Problem != null));
            ModuleStatus status = StatusOf(supervisor, "old");
            Assert.Contains("reports version 7", status.Problem);
            Assert.Contains("core speaks version 1", status.Problem);
            Assert.Equal(ModuleState.Stopped, status.State);
            Assert.Equal(0, status.ProcessId);
        }

        [Fact]
        public void ANameThatDiffersFromTheFolderAndABrokenDescribeAreReported()
        {
            WriteModule("alpha", Describe("beta"), "sleep 60");
            WriteModule("broken", "garbage", "sleep 60");
            string folder = Path.Combine(_modulesDirectory, "empty");
            Directory.CreateDirectory(folder);
            ModuleRegistry registry;
            ModuleSupervisor supervisor = NewSupervisor(FastOptions(), out registry);
            supervisor.Start();
            Assert.True(WaitFor(() => supervisor.GetStatuses().Count == 3 && supervisor.GetStatuses().All(s => s.Problem != null)));
            Assert.Contains("names 'beta'", StatusOf(supervisor, "alpha").Problem);
            Assert.Contains("not valid JSON", StatusOf(supervisor, "broken").Problem);
            Assert.Contains("no executable", StatusOf(supervisor, "empty").Problem);
        }

        [Fact]
        public void ADescribeThatNeverEndsOrPrintsTooMuchIsStopped()
        {
            WriteModule("slow", Describe("slow"), "sleep 60");
            // Replace the script so --describe hangs.
            string slow = Path.Combine(_modulesDirectory, "slow", "slow");
            File.WriteAllText(slow, "#!/bin/bash\nif [ \"$1\" = \"--describe\" ]; then sleep 30; fi\nsleep 60\n");
            WriteModule("chatty", Describe("chatty"), "sleep 60");
            string chatty = Path.Combine(_modulesDirectory, "chatty", "chatty");
            File.WriteAllText(chatty, "#!/bin/bash\nif [ \"$1\" = \"--describe\" ]; then yes aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa; fi\nsleep 60\n");

            ModuleSupervisorOptions options = FastOptions();
            options.DescribeTimeout = TimeSpan.FromMilliseconds(700);
            options.DescribeMaxOutputCharacters = 1000;
            ModuleRegistry registry;
            ModuleSupervisor supervisor = NewSupervisor(options, out registry);
            supervisor.Start();
            Assert.True(WaitFor(() => supervisor.GetStatuses().Count == 2 && supervisor.GetStatuses().All(s => s.Problem != null)));
            Assert.Contains("did not finish within", StatusOf(supervisor, "slow").Problem);
            Assert.Contains("printed more than 1000", StatusOf(supervisor, "chatty").Problem);
        }

        [Fact]
        public void AFolderThatDisappearsIsReportedAndItsRunningModuleIsNotStopped()
        {
            WriteModule("steady", Describe("steady"), "sleep 60");
            ModuleRegistry registry;
            ModuleSupervisor supervisor = NewSupervisor(FastOptions(), out registry);
            supervisor.Start();
            Assert.True(WaitFor(() => StatusOf(supervisor, "steady") != null && StatusOf(supervisor, "steady").ProcessId > 0));
            int processId = StatusOf(supervisor, "steady").ProcessId;

            Directory.Move(Path.Combine(_modulesDirectory, "steady"), Path.Combine(_directory, "moved-away"));
            supervisor.RequestRescan();
            Assert.True(WaitFor(() => StatusOf(supervisor, "steady").FolderMissing));
            Assert.Equal(processId, StatusOf(supervisor, "steady").ProcessId);
            Assert.False(Process.GetProcessById(processId).HasExited);
        }

        // ---- spawning (D17, D69) ----

        [Fact]
        public void AModuleIsSpawnedWithItsTokenInTheEnvironmentAndWithoutTheDatabaseConnectionString()
        {
            string environmentFile = Path.Combine(_directory, "env.txt");
            WriteModule("envcheck", Describe("envcheck", extra: ",\"arguments\":[\"one\",\"two words\"]"),
                "{ echo \"NAME=$MUD_MODULE_NAME\"; echo \"TOKEN=$MUD_MODULE_TOKEN\"; echo \"SOCKET=$MUD_CORE_SOCKET\"; echo \"MONGO=$MUD_MONGO_URI\"; echo \"ARGS=$1|$2\"; } > \"" + environmentFile + "\"\nsleep 60");

            string previous = Environment.GetEnvironmentVariable("MUD_MONGO_URI");
            Environment.SetEnvironmentVariable("MUD_MONGO_URI", "mongodb://secret.example/never-pass-this-on");
            try
            {
                ModuleRegistry registry;
                ModuleSupervisor supervisor = NewSupervisor(FastOptions(), out registry);
                supervisor.Start();
                Assert.True(WaitFor(() => File.Exists(environmentFile) && File.ReadAllLines(environmentFile).Length == 5));
                string[] lines = File.ReadAllLines(environmentFile);
                Assert.Equal("NAME=envcheck", lines[0]);
                Assert.True(lines[1].Length > "TOKEN=".Length + 20);
                Assert.Equal("SOCKET=" + Path.Combine(_directory, "modules.sock"), lines[2]);
                Assert.Equal("MONGO=", lines[3]);
                Assert.Equal("ARGS=one|two words", lines[4]);
                Assert.DoesNotContain("secret.example", LogText());
            }
            finally { Environment.SetEnvironmentVariable("MUD_MONGO_URI", previous); }
        }

        [Fact]
        public void EachStartGetsANewToken()
        {
            string tokenFile = Path.Combine(_directory, "tokens.txt");
            WriteModule("tokens", Describe("tokens"), "echo \"$MUD_MODULE_TOKEN\" >> \"" + tokenFile + "\"\nsleep 60");
            ModuleRegistry registry;
            ModuleSupervisor supervisor = NewSupervisor(FastOptions(), out registry);
            supervisor.Start();
            Assert.True(WaitFor(() => File.Exists(tokenFile) && File.ReadAllLines(tokenFile).Length == 1));
            supervisor.RequestRestart("tokens");
            Assert.True(WaitFor(() => File.ReadAllLines(tokenFile).Length == 2));
            string[] tokens = File.ReadAllLines(tokenFile);
            Assert.NotEqual(tokens[0], tokens[1]);
        }

        // ---- crash handling ----

        [Fact]
        public void ACrashingModuleIsRestartedWithBackoffThenMarkedFailedAndAnAdminStartClearsIt()
        {
            string runs = Path.Combine(_directory, "runs.txt");
            WriteModule("crashy", Describe("crashy"), "echo x >> \"" + runs + "\"\nexit 3");
            ModuleRegistry registry;
            ModuleSupervisor supervisor = NewSupervisor(FastOptions(), out registry);
            supervisor.Start();

            Assert.True(WaitFor(() => StatusOf(supervisor, "crashy") != null && StatusOf(supervisor, "crashy").State == ModuleState.Failed));
            Assert.Equal(3, File.ReadAllLines(runs).Length); // CrashLoopMaxCrashes = 3
            Assert.Contains("exit code 3", LogText());
            Assert.Contains("marked failed", LogText());
            Thread.Sleep(400);
            Assert.Equal(3, File.ReadAllLines(runs).Length); // Failed: no more restarts

            supervisor.RequestStart("crashy");
            Assert.True(WaitFor(() => File.ReadAllLines(runs).Length > 3)); // admin start clears Failed
        }

        [Fact]
        public void TheBackoffGrowsAsConfiguredAndTheLastValueRepeats()
        {
            WriteModule("slowcrash", Describe("slowcrash"), "exit 1");
            ModuleSupervisorOptions options = FastOptions();
            options.RestartBackoff = new[] { TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(700) };
            options.CrashLoopMaxCrashes = 10;
            ModuleRegistry registry;
            ModuleSupervisor supervisor = NewSupervisor(options, out registry);
            supervisor.Start();
            Assert.True(WaitFor(() => StatusOf(supervisor, "slowcrash") != null && StatusOf(supervisor, "slowcrash").State == ModuleState.Backoff));
            Assert.True(WaitFor(() => LogText().Contains("in 0.1 s") && LogText().Contains("in 0.7 s")));
        }

        [Fact]
        public void AnAdminStopEndsTheProcessAndIsNotACrash()
        {
            WriteModule("calm", Describe("calm"), "sleep 60");
            ModuleRegistry registry;
            ModuleSupervisor supervisor = NewSupervisor(FastOptions(), out registry);
            supervisor.Start();
            Assert.True(WaitFor(() => StatusOf(supervisor, "calm") != null && StatusOf(supervisor, "calm").ProcessId > 0));
            int processId = StatusOf(supervisor, "calm").ProcessId;

            supervisor.RequestStop("calm");
            Assert.True(WaitFor(() => StatusOf(supervisor, "calm").State == ModuleState.Stopped));
            Assert.True(WaitFor(() => { try { return Process.GetProcessById(processId).HasExited; } catch (ArgumentException) { return true; } }));
            Assert.Equal(0, StatusOf(supervisor, "calm").CrashesInWindow);
            Thread.Sleep(300);
            Assert.Equal(ModuleState.Stopped, StatusOf(supervisor, "calm").State); // not restarted
        }

        [Fact]
        public void StoppingTheSupervisorEndsEveryModuleProcess()
        {
            WriteModule("one", Describe("one"), "sleep 60");
            WriteModule("two", Describe("two"), "trap '' TERM\nwhile true; do sleep 0.1; done"); // ignores terminate: killed after the grace period
            ModuleSupervisorOptions options = FastOptions();
            options.TerminateGrace = TimeSpan.FromMilliseconds(500);
            ModuleRegistry registry;
            ModuleSupervisor supervisor = NewSupervisor(options, out registry);
            supervisor.Start();
            Assert.True(WaitFor(() => supervisor.GetStatuses().Count == 2 && supervisor.GetStatuses().All(s => s.ProcessId > 0)));
            Thread.Sleep(300);
            int[] processIds = supervisor.GetStatuses().Select(s => s.ProcessId).ToArray();

            supervisor.Stop();
            foreach (int processId in processIds)
            {
                Assert.True(WaitFor(() => { try { return Process.GetProcessById(processId).HasExited; } catch (ArgumentException) { return true; } }, 5));
            }
        }

        [Fact]
        public void ARestartOfARunningModuleReplacesTheProcess()
        {
            WriteModule("again", Describe("again"), "sleep 60");
            ModuleRegistry registry;
            ModuleSupervisor supervisor = NewSupervisor(FastOptions(), out registry);
            supervisor.Start();
            Assert.True(WaitFor(() => StatusOf(supervisor, "again") != null && StatusOf(supervisor, "again").ProcessId > 0));
            int first = StatusOf(supervisor, "again").ProcessId;
            supervisor.RequestRestart("again");
            Assert.True(WaitFor(() => { int now = StatusOf(supervisor, "again").ProcessId; return now > 0 && now != first; }));
        }

        [Fact]
        public void AdminRequestsForAnUnknownModuleAreAnsweredWithAClearMessage()
        {
            ModuleRegistry registry;
            ModuleSupervisor supervisor = NewSupervisor(FastOptions(), out registry);
            Assert.Contains("No module named 'nope'", supervisor.RequestStart("nope"));
            Assert.Contains("No module named 'nope'", supervisor.RequestStop("nope"));
            Assert.Contains("No module named 'nope'", supervisor.RequestRestart("nope"));
        }

        // ---- the required gate (D21, D70) ----

        [Fact]
        public void WithNoModulesTheGateOpensAtOnce()
        {
            ModuleRegistry registry;
            ModuleSupervisor supervisor = NewSupervisor(FastOptions(), out registry);
            supervisor.Start();
            Assert.Null(supervisor.WaitForRequiredModules());
        }

        [Fact]
        public void ANonRequiredModuleThatNeverConnectsDoesNotHoldTheGate()
        {
            WriteModule("optional", Describe("optional", required: false), "sleep 60");
            ModuleSupervisorOptions options = FastOptions();
            options.StartupTimeout = TimeSpan.FromMilliseconds(300);
            ModuleRegistry registry;
            ModuleSupervisor supervisor = NewSupervisor(options, out registry);
            supervisor.Start();
            Assert.Null(supervisor.WaitForRequiredModules());
        }

        [Fact]
        public void ARequiredModuleThatIsNotReadyInTimeFailsTheGateWithAClearError()
        {
            WriteModule("needed", Describe("needed", required: true), "sleep 60");
            ModuleSupervisorOptions options = FastOptions();
            options.StartupTimeout = TimeSpan.FromMilliseconds(600);
            ModuleRegistry registry;
            ModuleSupervisor supervisor = NewSupervisor(options, out registry);
            supervisor.Start();
            var watch = Stopwatch.StartNew();
            string error = supervisor.WaitForRequiredModules();
            Assert.NotNull(error);
            Assert.Contains("Required module 'needed' is not ready", error);
            Assert.Contains("start-up timeout of 0.6 s", error);
            Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(500));
            supervisor.Stop();
        }

        [Fact]
        public void ARequiredModuleWithAContractMismatchFailsTheGateAtOnce()
        {
            WriteModule("stale", Describe("stale", contractVersion: 9, required: true), "sleep 60");
            ModuleRegistry registry;
            ModuleSupervisor supervisor = NewSupervisor(FastOptions(), out registry);
            supervisor.Start();
            string error = supervisor.WaitForRequiredModules();
            Assert.Contains("Required module 'stale' is not ready", error);
            Assert.Contains("contract version mismatch", error);
        }

        [Fact]
        public async Task ARequiredModuleThatReportsReadyOpensTheGate()
        {
            string tokenFile = Path.Combine(_directory, "token.txt");
            WriteModule("core_needed", Describe("core_needed", required: true), "echo \"$MUD_MODULE_TOKEN\" > \"" + tokenFile + "\"\nsleep 60");
            var runDirectory = RunDirectory.Acquire(Path.Combine(_directory, "run"), _log.Add);
            _cleanup.Add(runDirectory);
            ModuleRegistry registry;
            ModuleSupervisor supervisor = NewSupervisor(FastOptions(), out registry);
            var supervisorOnRunDirectory = new ModuleSupervisor(registry, FastOptions(), runDirectory.ModulesSocketPath, _log.Add);
            _cleanup.Add(supervisorOnRunDirectory);
            var runner = new ModuleHostRunner(runDirectory, registry, action => { Task.Run(action); return true; }, _log.Add);
            _cleanup.Add(runner);
            runner.Start();
            supervisorOnRunDirectory.Start();

            Assert.True(WaitFor(() => File.Exists(tokenFile) && File.ReadAllText(tokenFile).Trim().Length > 20));
            string token = File.ReadAllText(tokenFile).Trim();

            Assert.False(supervisorOnRunDirectory.IsRequiredGateOpen()); // not Ready yet: the gate is closed
            using (GrpcChannel channel = ChannelFor(runDirectory.ModulesSocketPath))
            {
                var client = new ModuleHost.ModuleHostClient(channel);
                var hello = new HelloRequest { Module = "core_needed", Token = token, ContractVersion = ContractVersion.Current, ModuleVersion = "1.0", Manifest = new Manifest { Name = "core_needed" } };
                HelloReply reply = await client.HelloAsync(hello);
                Assert.True(reply.Ok, reply.Refusal);
                using (AsyncServerStreamingCall<CoreEvent> stream = client.Subscribe(new SubscribeRequest { ConnectionId = reply.ConnectionId }))
                {
                    Assert.True(WaitFor(() => registry.Find(reply.ConnectionId).IsSubscribed));
                    Assert.True(WaitFor(() => StatusOf(supervisorOnRunDirectory, "core_needed").State == ModuleState.Connected));
                    Assert.False(supervisorOnRunDirectory.IsRequiredGateOpen()); // connected is not Ready (D21)
                    Assert.True((await client.ReadyAsync(new ReadyRequest { ConnectionId = reply.ConnectionId })).Ok);
                    Assert.Null(supervisorOnRunDirectory.WaitForRequiredModules());
                    Assert.Equal(ModuleState.Ready, StatusOf(supervisorOnRunDirectory, "core_needed").State);
                }
            }
        }

        [Fact]
        public async Task AConnectionLostFromAReadyModuleStopsItAndSendsItToBackoff()
        {
            string tokenFile = Path.Combine(_directory, "token.txt");
            WriteModule("dropper", Describe("dropper"), "echo \"$MUD_MODULE_TOKEN\" > \"" + tokenFile + "\"\nsleep 60");
            var runDirectory = RunDirectory.Acquire(Path.Combine(_directory, "run"), _log.Add);
            _cleanup.Add(runDirectory);
            ModuleRegistry registry;
            ModuleSupervisor unused = NewSupervisor(FastOptions(), out registry);
            var supervisor = new ModuleSupervisor(registry, FastOptions(), runDirectory.ModulesSocketPath, _log.Add);
            _cleanup.Add(supervisor);
            var runner = new ModuleHostRunner(runDirectory, registry, action => { Task.Run(action); return true; }, _log.Add);
            _cleanup.Add(runner);
            runner.Start();
            supervisor.Start();
            Assert.True(WaitFor(() => File.Exists(tokenFile) && File.ReadAllText(tokenFile).Trim().Length > 20));
            string token = File.ReadAllText(tokenFile).Trim();
            int firstProcess = 0;

            using (GrpcChannel channel = ChannelFor(runDirectory.ModulesSocketPath))
            {
                var client = new ModuleHost.ModuleHostClient(channel);
                HelloReply reply = await client.HelloAsync(new HelloRequest { Module = "dropper", Token = token, ContractVersion = ContractVersion.Current, ModuleVersion = "1.0", Manifest = new Manifest { Name = "dropper" } });
                Assert.True(reply.Ok);
                using (AsyncServerStreamingCall<CoreEvent> stream = client.Subscribe(new SubscribeRequest { ConnectionId = reply.ConnectionId }))
                {
                    Assert.True(WaitFor(() => registry.Find(reply.ConnectionId).IsSubscribed));
                    await client.ReadyAsync(new ReadyRequest { ConnectionId = reply.ConnectionId });
                    Assert.True(WaitFor(() => StatusOf(supervisor, "dropper").State == ModuleState.Ready));
                    firstProcess = StatusOf(supervisor, "dropper").ProcessId;
                }
                // The stream is disposed: the module "lost" its connection. The supervisor treats it as a crash and starts a new instance.
                Assert.True(WaitFor(() => LogText().Contains("the connection to core was lost")));
                Assert.True(WaitFor(() => { ModuleStatus s = StatusOf(supervisor, "dropper"); return s.ProcessId > 0 && s.ProcessId != firstProcess; }));
            }
        }

        [Fact]
        public async Task ARefusedRequiredCapabilityMarksTheModuleRefusedAndItIsNotRestarted()
        {
            string tokenFile = Path.Combine(_directory, "token.txt");
            WriteModule("greedy", Describe("greedy", required: true), "echo \"$MUD_MODULE_TOKEN\" >> \"" + tokenFile + "\"\nsleep 60");
            var runDirectory = RunDirectory.Acquire(Path.Combine(_directory, "run"), _log.Add);
            _cleanup.Add(runDirectory);
            ModuleRegistry registry;
            ModuleSupervisor unused = NewSupervisor(FastOptions(), out registry);
            var supervisor = new ModuleSupervisor(registry, FastOptions(), runDirectory.ModulesSocketPath, _log.Add);
            _cleanup.Add(supervisor);
            var runner = new ModuleHostRunner(runDirectory, registry, action => { Task.Run(action); return true; }, _log.Add);
            _cleanup.Add(runner);
            runner.Start();
            supervisor.Start();
            Assert.True(WaitFor(() => File.Exists(tokenFile) && File.ReadAllText(tokenFile).Trim().Length > 20));
            string token = File.ReadAllText(tokenFile).Trim();

            using (GrpcChannel channel = ChannelFor(runDirectory.ModulesSocketPath))
            {
                var client = new ModuleHost.ModuleHostClient(channel);
                var manifest = new Manifest { Name = "greedy" };
                manifest.RequiredCapabilities.Add("state");
                HelloReply reply = await client.HelloAsync(new HelloRequest { Module = "greedy", Token = token, ContractVersion = ContractVersion.Current, ModuleVersion = "1.0", Manifest = manifest });
                Assert.False(reply.Ok);
            }
            Assert.True(WaitFor(() => StatusOf(supervisor, "greedy").State == ModuleState.Refused));
            string error = supervisor.WaitForRequiredModules();
            Assert.Contains("core refused it", error);
            Assert.Contains("requires capabilities that the grants file does not allow: state", error);
            Thread.Sleep(300);
            Assert.Equal(1, File.ReadAllLines(tokenFile).Length); // not started again
        }

        // ---- settings ----

        [Fact]
        public void TheSettingsReaderKeepsTheDefaultsAndFlagsBadValues()
        {
            string settingsPath = Path.Combine(_directory, "settings.json");
            File.WriteAllText(settingsPath, "{ \"ModuleHost\": { \"StartupTimeoutSeconds\": 12, \"RestartBackoffSeconds\": \"1, 3,9\", \"CrashLoopMaxCrashes\": 4, \"PingTimeoutSeconds\": \"abc\", \"ModulesDirectory\": \"mods\" } }");
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddJsonFile(settingsPath).Build();
            ModuleSupervisorOptions options = ModuleSupervisorSettings.Read(config, "/base", _log.Add);
            Assert.Equal(TimeSpan.FromSeconds(12), options.StartupTimeout);
            Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(9) }, options.RestartBackoff.ToArray());
            Assert.Equal(4, options.CrashLoopMaxCrashes);
            Assert.Equal(new ModuleSupervisorOptions().PingTimeout, options.PingTimeout);
            Assert.Contains("PingTimeoutSeconds", LogText());
            Assert.Equal("/base/mods", options.ModulesDirectory);
            Assert.Equal(TimeSpan.FromSeconds(30), new ModuleSupervisorOptions().StartupTimeout); // D70
        }

        // ---- the modules command ----

        private ModulesCommand CommandWithSupervisor(ModuleSupervisor supervisor, out RecordingLiving admin, out RecordingLiving plain)
        {
            World world = TestWorld.Create();
            world.ModuleSupervisor = supervisor;
            admin = new RecordingLiving(world);
            admin.Roles.Admin = true;
            plain = new RecordingLiving(world);
            return new ModulesCommand(world);
        }

        [Fact]
        public void TheModulesCommandListsStartsStopsAndRefusesNonAdmins()
        {
            WriteModule("listed", Describe("listed", required: false), "sleep 60");
            ModuleRegistry registry;
            ModuleSupervisor supervisor = NewSupervisor(FastOptions(), out registry);
            supervisor.Start();
            Assert.True(WaitFor(() => StatusOf(supervisor, "listed") != null && StatusOf(supervisor, "listed").State == ModuleState.Starting && StatusOf(supervisor, "listed").ProcessId > 0));

            RecordingLiving admin, plain;
            ModulesCommand command = CommandWithSupervisor(supervisor, out admin, out plain);

            command.DoCommand(plain, new object[] { "list" });
            Assert.Equal("This is an admin only command.", Assert.Single(plain.Messages));

            command.DoCommand(admin, new object[] { "list" });
            Assert.Contains("listed: Starting version 1.0 process", Assert.Single(admin.Messages));
            admin.Messages.Clear();

            command.DoCommand(admin, new object[] { "stop listed" });
            Assert.Equal("Stop of 'listed' requested.", Assert.Single(admin.Messages));
            Assert.True(WaitFor(() => StatusOf(supervisor, "listed").State == ModuleState.Stopped));
            admin.Messages.Clear();

            command.DoCommand(admin, new object[] { "start listed" });
            Assert.True(WaitFor(() => StatusOf(supervisor, "listed").State == ModuleState.Starting && StatusOf(supervisor, "listed").ProcessId > 0));
            admin.Messages.Clear();

            command.DoCommand(admin, new object[] { "start nothere" });
            Assert.Contains("No module named 'nothere'", Assert.Single(admin.Messages));
            admin.Messages.Clear();

            command.DoCommand(admin, new object[] { "token listed" }); // D69: there is no token command
            command.DoCommand(admin, new object[] { "reload listed" }); // D67: replaced by restart
            command.DoCommand(admin, new object[] { "" });
            Assert.All(admin.Messages, m => Assert.StartsWith("Syntax: modules list", m));
            Assert.DoesNotContain("token", ModulesCommand.Syntax);
            Assert.DoesNotContain("reload <", ModulesCommand.Syntax);
        }

        [Fact]
        public void TheModulesCommandReloadsTheGrantsFile()
        {
            ModuleRegistry registry;
            ModuleSupervisor supervisor = NewSupervisor(FastOptions(), out registry);
            RecordingLiving admin, plain;
            ModulesCommand command = CommandWithSupervisor(supervisor, out admin, out plain);

            Assert.False(registry.Grants.IsGranted("late", "state"));
            File.WriteAllText(registry.Grants.FilePath, "modules:\n  late:\n    - state\n");
            command.DoCommand(admin, new object[] { "grants reload" });
            Assert.Contains("Grants reloaded", Assert.Single(admin.Messages));
            Assert.True(registry.Grants.IsGranted("late", "state"));
        }

        [Fact]
        public void TheModulesCommandSaysSoWhenThereIsNoSupervisorOrNoModule()
        {
            World world = TestWorld.Create();
            var admin = new RecordingLiving(world);
            admin.Roles.Admin = true;
            new ModulesCommand(world).DoCommand(admin, new object[] { "list" });
            Assert.Equal("The module supervisor is not running.", Assert.Single(admin.Messages));

            ModuleRegistry registry;
            ModuleSupervisor supervisor = NewSupervisor(FastOptions(), out registry);
            Assert.Contains("No modules found", ModulesCommand.Describe(supervisor)[0]);
        }

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
    }
}
