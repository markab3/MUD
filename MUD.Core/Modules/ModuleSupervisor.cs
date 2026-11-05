using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MUD.Contracts;

namespace MUD.Core.Modules
{
    /// <summary>Where a supervised module is in its life (MODULES section 3).</summary>
    public enum ModuleState
    {
        /// <summary>Known, not running, and no start is wanted (never started, stopped by an admin, or its folder is gone).</summary>
        Stopped,
        /// <summary>The process was spawned; no Hello has been accepted yet.</summary>
        Starting,
        /// <summary>Hello was accepted; the module has not called Ready.</summary>
        Connected,
        /// <summary>The module called Ready.</summary>
        Ready,
        /// <summary>The module ended unexpectedly; it is started again when the wait is over.</summary>
        Backoff,
        /// <summary>Too many crashes in the crash-loop window; not started again until an admin runs `modules start`.</summary>
        Failed,
        /// <summary>Core refused the module's Hello because of a contract mismatch or a required capability the grants file does not allow.</summary>
        Refused,
        /// <summary>An admin stop or a restart is in progress.</summary>
        Stopping
    }

    /// <summary>A copy of one module's state, for the `modules list` command and for tests.</summary>
    public sealed class ModuleStatus
    {
        public string Name { get; internal set; }
        public ModuleState State { get; internal set; }
        public string Problem { get; internal set; }
        public string Version { get; internal set; }
        public bool Required { get; internal set; }
        public uint ContractVersion { get; internal set; }
        public string ExecutablePath { get; internal set; }
        public int ProcessId { get; internal set; }
        public bool FolderMissing { get; internal set; }
        public int CrashesInWindow { get; internal set; }
        public DateTime? NextStartUtc { get; internal set; }
        public string Detail { get; internal set; }
        public IReadOnlyList<string> BehaviorTypes { get; internal set; }
    }

    /// <summary>
    /// Core's child-process supervisor for modules (owner decisions D20, D21, D67, D68, D69, D70). It discovers modules by scanning
    /// one directory (D67), spawns each one with a random per-spawn token in its environment (D17), watches the connection and the
    /// process, restarts a crashed module with backoff, marks a crash-looping module Failed, ends stale processes of the same
    /// executable before every start (D68), and tells the start-up code whether every required module became Ready (D21).
    ///
    /// All process work (describe, spawn, terminate) happens on the supervisor's own thread, never on the game thread: the admin
    /// command only queues a request and returns. There is no development token and no hand-run module (D69).
    /// </summary>
    public sealed class ModuleSupervisor : IDisposable
    {
        private sealed class Record
        {
            public string Name;
            public string Directory;
            public string ExecutablePath;
            public ModuleDescription Description;
            public string Problem;
            public DateTime ExecutableModifiedUtc;     // the in-memory scan cache (D67)
            public bool FolderMissing;
            public ModuleState State = ModuleState.Stopped;
            public Process Process;
            public int ProcessId;
            public DateTime StartedUtc;
            public DateTime? FirstStartUtc;            // start of the D70 timeout for this module; cleared by an admin start
            public DateTime? NextStartUtc;
            public readonly List<DateTime> Crashes = new List<DateTime>();
            public string ConnectionId;
            public bool ConnectionLost;
            public string RefusalReason;
            public string Detail = "";
            public bool RestartAfterStop;
        }

        private readonly ModuleRegistry _registry;
        private readonly ModuleSupervisorOptions _options;
        private readonly string _modulesSocketPath;
        private readonly Action<string> _log;
        private readonly Func<DateTime> _utcNow;
        private readonly object _lock = new object();
        private readonly Dictionary<string, Record> _records = new Dictionary<string, Record>(StringComparer.Ordinal);
        private readonly Queue<Action> _requests = new Queue<Action>();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private Thread _thread;
        private volatile bool _stopRequested;

        public ModuleSupervisor(ModuleRegistry registry, ModuleSupervisorOptions options, string modulesSocketPath, Action<string> log = null, Func<DateTime> utcNow = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _modulesSocketPath = modulesSocketPath ?? throw new ArgumentNullException(nameof(modulesSocketPath));
            _log = log ?? Console.WriteLine;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _registry.Connected += onConnected;
            _registry.Ready += onReady;
            _registry.Disconnected += onDisconnected;
            _registry.Refused += onRefused;
        }

        public ModuleRegistry Registry { get { return _registry; } }

        public ModuleSupervisorOptions Options { get { return _options; } }

        // ---- starting and stopping the supervisor ----

        /// <summary>
        /// Starts the supervisor thread. It scans the modules directory and starts every valid module (D67: every module always runs;
        /// non-required modules start in the background and nothing waits for them).
        /// </summary>
        public void Start()
        {
            if (_thread != null) { throw new InvalidOperationException("The module supervisor is already running."); }
            Enqueue(() => { Scan(); StartAllEligible(); });
            _thread = new Thread(run) { IsBackground = true, Name = "module-supervisor" };
            _thread.Start();
        }

        /// <summary>Stops watching, then ends every module process (terminate signal, grace period, kill). Safe to call twice.</summary>
        public void Stop()
        {
            _stopRequested = true;
            _wake.Set();
            Thread thread = _thread;
            if (thread != null && thread != Thread.CurrentThread) { thread.Join(TimeSpan.FromSeconds(30)); }
            _thread = null;
            StopAllProcesses();
        }

        public void Dispose()
        {
            Stop();
        }

        private void run()
        {
            while (!_stopRequested)
            {
                try
                {
                    Action request;
                    while ((request = Dequeue()) != null && !_stopRequested) { request(); }
                    Tick();
                }
                catch (Exception ex)
                {
                    _log("Module supervisor error: " + ex);
                }
                _wake.WaitOne(_options.TickInterval);
            }
        }

        private void Enqueue(Action request)
        {
            lock (_lock) { _requests.Enqueue(request); }
            _wake.Set();
        }

        private Action Dequeue()
        {
            lock (_lock) { return _requests.Count == 0 ? null : _requests.Dequeue(); }
        }

        // ---- the admin API (returns at once; the work happens on the supervisor thread) ----

        /// <summary>Queues a scan of the modules directory. Running modules are never touched (D67).</summary>
        public string RequestRescan()
        {
            Enqueue(() => { Scan(); });
            return "Rescan requested. Use `modules list` to see the result.";
        }

        /// <summary>Queues a start. Clears Failed, Refused and the crash history (MODULES section 3).</summary>
        public string RequestStart(string name)
        {
            string problem = CheckName(name);
            if (problem != null) { return problem; }
            Enqueue(() => { StartByAdmin(name); });
            return "Start of '" + name + "' requested.";
        }

        public string RequestStop(string name)
        {
            string problem = CheckName(name);
            if (problem != null) { return problem; }
            Enqueue(() => { StopByAdmin(name, false); });
            return "Stop of '" + name + "' requested.";
        }

        public string RequestRestart(string name)
        {
            string problem = CheckName(name);
            if (problem != null) { return problem; }
            Enqueue(() => { StopByAdmin(name, true); });
            return "Restart of '" + name + "' requested.";
        }

        private string CheckName(string name)
        {
            lock (_lock)
            {
                if (!string.IsNullOrEmpty(name) && _records.ContainsKey(name)) { return null; }
            }
            return "No module named '" + name + "'. Use `modules rescan`, then `modules list`.";
        }

        /// <summary>The state of every module core knows, by name.</summary>
        public IReadOnlyList<ModuleStatus> GetStatuses()
        {
            lock (_lock)
            {
                DateTime now = _utcNow();
                return _records.Values.OrderBy(r => r.Name, StringComparer.Ordinal).Select(r => new ModuleStatus
                {
                    Name = r.Name,
                    State = r.State,
                    Problem = r.Problem,
                    Version = r.Description == null ? "" : r.Description.Version,
                    Required = r.Description != null && r.Description.Required,
                    ContractVersion = r.Description == null ? 0 : r.Description.ContractVersion,
                    ExecutablePath = r.ExecutablePath,
                    ProcessId = r.State == ModuleState.Stopped || r.State == ModuleState.Backoff || r.State == ModuleState.Failed ? 0 : r.ProcessId,
                    FolderMissing = r.FolderMissing,
                    CrashesInWindow = r.Crashes.Count(t => now - t <= _options.CrashLoopWindow),
                    NextStartUtc = r.State == ModuleState.Backoff ? r.NextStartUtc : null,
                    Detail = r.Detail,
                    BehaviorTypes = r.Description == null ? new string[0] : r.Description.BehaviorTypes
                }).ToList();
            }
        }

        // ---- the required gate (D21, D70) ----

        /// <summary>
        /// Blocks until every module whose `--describe` says required is Ready. Returns null when they all are (also when there are none).
        /// Returns a clear error message when a required module is invalid, Failed, Refused, or is not Ready within the start-up timeout
        /// counted from its first start (D70: per required module). The caller then exits with that message and stops the supervisor.
        /// Call after <see cref="Start"/>; the telnet front end is started only after this returns null.
        /// </summary>
        public string WaitForRequiredModules(CancellationToken cancel = default(CancellationToken))
        {
            while (!cancel.IsCancellationRequested)
            {
                string error;
                bool done = CheckRequiredModules(out error);
                if (error != null) { return error; }
                if (done) { return null; }
                cancel.WaitHandle.WaitOne(50);
            }
            return "Waiting for the required modules was cancelled.";
        }

        /// <summary>True when the first scan is done and every required module is Ready (a failed gate also answers false). Does not wait.</summary>
        public bool IsRequiredGateOpen()
        {
            string error;
            return CheckRequiredModules(out error);
        }

        /// <summary>One look at the gate. Returns true when nothing required is outstanding; <paramref name="error"/> is set when the gate has failed.</summary>
        internal bool CheckRequiredModules(out string error)
        {
            error = null;
            bool allReady = true;
            lock (_lock)
            {
                // The first scan has not run yet: nothing is known, so nothing can be declared ready.
                if (!_scanned) { return false; }
                DateTime now = _utcNow();
                foreach (Record record in _records.Values.OrderBy(r => r.Name, StringComparer.Ordinal))
                {
                    if (record.Description == null || !record.Description.Required) { continue; }
                    if (record.State == ModuleState.Ready) { continue; }
                    allReady = false;
                    string why = null;
                    if (record.Problem != null) { why = record.Problem; }
                    else if (record.State == ModuleState.Failed) { why = "it crashed " + _options.CrashLoopMaxCrashes + " times within " + Format(_options.CrashLoopWindow) + " and was marked failed" + Suffix(record.Detail); }
                    else if (record.State == ModuleState.Refused) { why = "core refused it: " + record.RefusalReason; }
                    else if (record.FirstStartUtc.HasValue && now - record.FirstStartUtc.Value >= _options.StartupTimeout)
                    {
                        why = "it was not Ready within the start-up timeout of " + Format(_options.StartupTimeout) + " (state: " + record.State + ")" + Suffix(record.Detail);
                    }
                    if (why != null)
                    {
                        error = "Required module '" + record.Name + "' is not ready: " + why + ". Core cannot open the game without it and is exiting. "
                            + "Fix the module, or remove its folder from " + _options.ModulesDirectory + ".";
                        return false;
                    }
                }
            }
            return allReady;
        }

        private static string Suffix(string detail) { return string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")"; }

        private static string Format(TimeSpan span) { return span.TotalSeconds + " s"; }

        // ---- scanning (D67) ----

        private bool _scanned;

        private void Scan()
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            string[] directories = new string[0];
            if (!string.IsNullOrWhiteSpace(_options.ModulesDirectory) && Directory.Exists(_options.ModulesDirectory))
            {
                try { directories = Directory.GetDirectories(_options.ModulesDirectory); }
                catch (Exception ex) { _log("Could not read the modules directory " + _options.ModulesDirectory + ": " + ex.Message); }
                Array.Sort(directories, StringComparer.Ordinal);
            }
            else
            {
                _log("Modules directory " + (_options.ModulesDirectory ?? "(not set)") + " does not exist: no modules.");
            }

            foreach (string directory in directories)
            {
                string name = Path.GetFileName(directory);
                found.Add(name);
                string executable = Path.Combine(directory, name);
                DateTime modified;
                try { modified = File.Exists(executable) ? File.GetLastWriteTimeUtc(executable) : DateTime.MinValue; }
                catch (Exception) { modified = DateTime.MinValue; }

                Record record;
                bool isNew;
                lock (_lock)
                {
                    isNew = !_records.TryGetValue(name, out record);
                    if (isNew)
                    {
                        record = new Record { Name = name };
                        _records[name] = record;
                    }
                    record.Directory = directory;
                    record.ExecutablePath = executable;
                    record.FolderMissing = false;
                }

                if (modified == DateTime.MinValue)
                {
                    SetProblem(record, "the folder has no executable named '" + name + "'");
                    continue;
                }
                // The cache: --describe runs only for an executable that is new or whose modified time changed (D67).
                if (!isNew && record.ExecutableModifiedUtc == modified && (record.Description != null || record.Problem != null)) { continue; }
                record.ExecutableModifiedUtc = modified;
                DescribeInto(record);
            }

            lock (_lock)
            {
                foreach (Record record in _records.Values)
                {
                    if (!found.Contains(record.Name) && !record.FolderMissing)
                    {
                        record.FolderMissing = true;
                        _log("Module folder '" + record.Name + "' is gone from " + _options.ModulesDirectory + ". "
                            + (record.State == ModuleState.Stopped ? "It is not running." : "Its process keeps running; stop it with `modules stop " + record.Name + "`."));
                    }
                }
                _scanned = true;
            }
        }

        private void SetProblem(Record record, string problem)
        {
            lock (_lock)
            {
                if (record.Problem != problem) { _log("Module '" + record.Name + "' cannot be started: " + problem + "."); }
                record.Problem = problem;
                record.Description = null;
            }
        }

        private void DescribeInto(Record record)
        {
            string output, failure;
            if (!RunDescribe(record, out output, out failure))
            {
                SetProblem(record, failure);
                return;
            }
            string parseProblem;
            ModuleDescription description = ModuleDescription.Parse(output, out parseProblem);
            if (description == null)
            {
                SetProblem(record, parseProblem);
                return;
            }
            if (!string.Equals(description.Name, record.Name, StringComparison.Ordinal))
            {
                SetProblem(record, "its --describe output names '" + description.Name + "' but the folder is '" + record.Name + "'");
                return;
            }
            if (description.ContractVersion != ContractVersion.Current)
            {
                string message = "contract version mismatch: module '" + record.Name + "' reports version " + description.ContractVersion
                    + ", core speaks version " + ContractVersion.Current + ". Strict equality is required; rebuild the module against the current contracts";
                _log("Module scan refused: " + message + ".");
                lock (_lock) { record.Problem = message; record.Description = description; }
                return;
            }
            lock (_lock)
            {
                record.Description = description;
                record.Problem = null;
            }
            _log("Module '" + record.Name + "' (version " + description.Version + ", " + (description.Required ? "required" : "not required") + ") found at " + record.ExecutablePath + ".");
        }

        /// <summary>Runs `executable --describe` with a timeout and an output limit; never connects to core (D67).</summary>
        private bool RunDescribe(Record record, out string output, out string failure)
        {
            output = null;
            failure = null;
            var startInfo = new ProcessStartInfo(record.ExecutablePath)
            {
                WorkingDirectory = record.Directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            };
            startInfo.ArgumentList.Add("--describe");
            ModuleProcessSupport.ApplyCleanEnvironment(startInfo);

            Process process;
            try { process = Process.Start(startInfo); }
            catch (Exception ex)
            {
                failure = "the executable could not be run for --describe: " + ex.Message;
                return false;
            }

            using (process)
            {
                try { process.StandardInput.Close(); } catch (Exception) { }
                var buffer = new StringBuilder();
                bool tooMuch = false;
                Task reader = Task.Run(() =>
                {
                    var chunk = new char[4096];
                    int count;
                    try
                    {
                        while ((count = process.StandardOutput.Read(chunk, 0, chunk.Length)) > 0)
                        {
                            buffer.Append(chunk, 0, count);
                            if (buffer.Length > _options.DescribeMaxOutputCharacters)
                            {
                                tooMuch = true;
                                try { process.Kill(true); } catch (Exception) { }
                                return;
                            }
                        }
                    }
                    catch (Exception) { }
                });
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) { _log("[module:" + record.Name + " --describe] " + e.Data); } };
                process.BeginErrorReadLine();

                if (!process.WaitForExit((int)_options.DescribeTimeout.TotalMilliseconds))
                {
                    try { process.Kill(true); } catch (Exception) { }
                    reader.Wait(TimeSpan.FromSeconds(1));
                    failure = tooMuch
                        ? "--describe printed more than " + _options.DescribeMaxOutputCharacters + " characters"
                        : "--describe did not finish within " + Format(_options.DescribeTimeout);
                    return false;
                }
                reader.Wait(TimeSpan.FromSeconds(2));
                if (tooMuch)
                {
                    failure = "--describe printed more than " + _options.DescribeMaxOutputCharacters + " characters";
                    return false;
                }
                if (process.ExitCode != 0)
                {
                    failure = "--describe exited with code " + process.ExitCode;
                    return false;
                }
                output = buffer.ToString();
                return true;
            }
        }

        // ---- starting and stopping modules ----

        private void StartAllEligible()
        {
            List<string> names;
            lock (_lock) { names = _records.Values.Where(r => r.State == ModuleState.Stopped && r.Problem == null && !r.FolderMissing).Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).ToList(); }
            foreach (string name in names) { StartModule(name, true); }
        }

        private void StartByAdmin(string name)
        {
            lock (_lock)
            {
                Record record;
                if (!_records.TryGetValue(name, out record)) { return; }
                if (record.State == ModuleState.Starting || record.State == ModuleState.Connected || record.State == ModuleState.Ready || record.State == ModuleState.Stopping)
                {
                    _log("Module '" + name + "' is already " + record.State + "; start ignored.");
                    return;
                }
                record.Crashes.Clear();
                record.FirstStartUtc = null;
                record.RefusalReason = null;
            }
            StartModule(name, true);
        }

        private void StopByAdmin(string name, bool restart)
        {
            Record record;
            lock (_lock)
            {
                if (!_records.TryGetValue(name, out record)) { return; }
                if (record.Process == null)
                {
                    // Nothing runs: a stop only cancels a pending restart; a restart is a plain start.
                    if (record.State == ModuleState.Backoff || record.State == ModuleState.Failed || record.State == ModuleState.Refused) { record.State = ModuleState.Stopped; record.NextStartUtc = null; }
                }
                else
                {
                    record.State = ModuleState.Stopping;
                    record.RestartAfterStop = restart;
                }
            }
            if (record.Process != null)
            {
                EndProcess(record, "stopped by an admin");
                lock (_lock) { record.State = ModuleState.Stopped; record.RestartAfterStop = false; }
                _log("Module '" + name + "' stopped.");
            }
            if (restart) { StartByAdmin(name); }
        }

        /// <summary>Starts one module: stale-process check (D68), new token (D17), spawn with the token in the environment.</summary>
        private void StartModule(string name, bool firstAttempt)
        {
            Record record;
            lock (_lock)
            {
                if (!_records.TryGetValue(name, out record)) { return; }
                if (record.Problem != null || record.FolderMissing || record.Description == null)
                {
                    _log("Module '" + name + "' is not started: " + (record.Problem ?? "its folder is gone") + ".");
                    return;
                }
                if (!record.FirstStartUtc.HasValue) { record.FirstStartUtc = _utcNow(); }
                record.State = ModuleState.Starting;
                record.StartedUtc = _utcNow();
                record.ConnectionId = null;
                record.ConnectionLost = false;
                record.Detail = "";
            }

            try
            {
                int stale = StaleModuleProcesses.Terminate(record.ExecutablePath, _options.TerminateGrace, _log);
                if (stale > 0) { _log("Ended " + stale + " stale process(es) of module '" + name + "' before starting it."); }
            }
            catch (Exception ex)
            {
                _log("The stale-process check for module '" + name + "' failed: " + ex.Message);
            }

            string token = _registry.Tokens.Issue(name);
            var startInfo = new ProcessStartInfo(record.ExecutablePath)
            {
                WorkingDirectory = record.Directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            };
            foreach (string argument in record.Description.Arguments) { startInfo.ArgumentList.Add(argument); }
            ModuleProcessSupport.ApplyCleanEnvironment(startInfo);
            startInfo.Environment["MUD_MODULE_NAME"] = name;
            startInfo.Environment["MUD_MODULE_TOKEN"] = token;
            startInfo.Environment["MUD_CORE_SOCKET"] = _modulesSocketPath;

            Process process;
            try
            {
                process = Process.Start(startInfo);
                // Module logs: the child's standard output and error are re-logged by core with a [module:name] prefix, so they
                // appear in core's own log (the systemd journal). Flagged for the owner in the pull request.
                process.OutputDataReceived += (s, e) => { if (e.Data != null) { _log("[module:" + name + "] " + e.Data); } };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) { _log("[module:" + name + "] " + e.Data); } };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                try { process.StandardInput.Close(); } catch (Exception) { }
            }
            catch (Exception ex)
            {
                _log("Module '" + name + "' could not be started: " + ex.Message);
                OnProcessEnded(record, "could not be started: " + ex.Message);
                return;
            }

            lock (_lock)
            {
                record.Process = process;
                record.ProcessId = process.Id;
            }
            _log("Module '" + name + "' started as process " + process.Id + ".");
        }

        /// <summary>Terminates the record's process (terminate signal, grace, kill) and forgets its token.</summary>
        private void EndProcess(Record record, string reason)
        {
            Process process;
            lock (_lock) { process = record.Process; }
            if (process != null)
            {
                int processId = record.ProcessId;
                try
                {
                    if (!ModuleProcessSupport.TerminateProcess(processId, _options.TerminateGrace))
                    {
                        _log("WARNING: module '" + record.Name + "' (process " + processId + ") is still running after the kill signal.");
                    }
                }
                catch (Exception ex) { _log("Could not end module '" + record.Name + "': " + ex.Message); }
                try { process.WaitForExit(2000); } catch (Exception) { }
                try { process.Dispose(); } catch (Exception) { }
            }
            lock (_lock)
            {
                record.Process = null;
                record.ProcessId = 0;
                record.ConnectionId = null;
                _registry.Tokens.Revoke(record.Name);
            }
        }

        private void StopAllProcesses()
        {
            List<Record> running;
            lock (_lock) { running = _records.Values.Where(r => r.Process != null).ToList(); }
            if (running.Count == 0) { return; }
            // Signal everyone first so the grace periods run side by side, then wait for each.
            foreach (Record record in running)
            {
                lock (_lock) { record.State = ModuleState.Stopping; }
                ModuleProcessSupport.SendTerminate(record.ProcessId);
            }
            foreach (Record record in running)
            {
                EndProcess(record, "core is stopping");
                lock (_lock) { record.State = ModuleState.Stopped; }
            }
            _log("All module processes stopped.");
        }

        // ---- the watch ----

        private void Tick()
        {
            List<Record> records;
            lock (_lock) { records = _records.Values.ToList(); }
            foreach (Record record in records)
            {
                if (_stopRequested) { return; }
                ModuleState state;
                Process process;
                bool lost;
                DateTime startedUtc;
                string refusal;
                lock (_lock) { state = record.State; process = record.Process; lost = record.ConnectionLost; startedUtc = record.StartedUtc; refusal = record.RefusalReason; }
                DateTime now = _utcNow();

                switch (state)
                {
                    case ModuleState.Starting:
                    case ModuleState.Connected:
                    case ModuleState.Ready:
                        if (refusal != null)
                        {
                            EndProcess(record, "refused");
                            lock (_lock) { record.State = ModuleState.Refused; }
                            _log("Module '" + record.Name + "' was refused and is not restarted: " + refusal);
                        }
                        else if (process != null && HasExited(process))
                        {
                            string code = ExitDescription(process);
                            EndProcess(record, "exited");
                            OnProcessEnded(record, "the process exited (" + code + ")");
                        }
                        else if (lost)
                        {
                            EndProcess(record, "connection lost");
                            OnProcessEnded(record, "the connection to core was lost");
                        }
                        else if (state != ModuleState.Ready && now - startedUtc >= _options.StartupTimeout)
                        {
                            EndProcess(record, "start-up timeout");
                            OnProcessEnded(record, "it was not Ready within the start-up timeout of " + Format(_options.StartupTimeout));
                        }
                        else if (state == ModuleState.Ready && IsHung(record, now))
                        {
                            EndProcess(record, "hung");
                            OnProcessEnded(record, "it stopped answering Ping for " + Format(_options.PingTimeout));
                        }
                        break;

                    case ModuleState.Backoff:
                        DateTime? next;
                        lock (_lock) { next = record.NextStartUtc; }
                        if (next.HasValue && now >= next.Value) { StartModule(record.Name, false); }
                        break;
                }
            }
        }

        private bool IsHung(Record record, DateTime now)
        {
            ModuleConnection connection = _registry.FindByModule(record.Name);
            if (connection == null) { return false; }
            return now - connection.LastPingUtc >= _options.PingTimeout;
        }

        private static bool HasExited(Process process)
        {
            try { return process.HasExited; } catch (Exception) { return true; }
        }

        private static string ExitDescription(Process process)
        {
            try { return "exit code " + process.ExitCode; } catch (Exception) { return "exit code unknown"; }
        }

        /// <summary>The module ended without an admin asking (crash, lost connection, hung, start-up timeout): backoff or Failed.</summary>
        private void OnProcessEnded(Record record, string reason)
        {
            lock (_lock)
            {
                DateTime now = _utcNow();
                record.Process = null;
                record.ProcessId = 0;
                record.ConnectionId = null;
                record.ConnectionLost = false;
                _registry.Tokens.Revoke(record.Name);
                record.Crashes.Add(now);
                record.Crashes.RemoveAll(t => now - t > _options.CrashLoopWindow);
                record.Detail = reason;
                if (record.Crashes.Count >= _options.CrashLoopMaxCrashes)
                {
                    record.State = ModuleState.Failed;
                    record.NextStartUtc = null;
                    _log("Module '" + record.Name + "' ended (" + reason + ") and has now crashed " + record.Crashes.Count + " times within " + Format(_options.CrashLoopWindow)
                        + ": marked failed. It is not restarted until an admin runs `modules start " + record.Name + "`.");
                    return;
                }
                IReadOnlyList<TimeSpan> steps = _options.RestartBackoff;
                TimeSpan delay = steps.Count == 0 ? TimeSpan.Zero : steps[Math.Min(record.Crashes.Count - 1, steps.Count - 1)];
                record.State = ModuleState.Backoff;
                record.NextStartUtc = now + delay;
                _log("Module '" + record.Name + "' ended (" + reason + "). Restart " + record.Crashes.Count + " of " + (_options.CrashLoopMaxCrashes - 1) + " allowed in " + Format(_options.CrashLoopWindow) + ", in " + Format(delay) + ".");
            }
        }

        // ---- registry events (any thread) ----

        private void onConnected(ModuleConnection connection)
        {
            lock (_lock)
            {
                Record record;
                if (!_records.TryGetValue(connection.ModuleName, out record)) { return; }
                if (record.State != ModuleState.Starting) { return; }
                record.State = ModuleState.Connected;
                record.ConnectionId = connection.ConnectionId;
            }
        }

        private void onReady(ModuleConnection connection)
        {
            lock (_lock)
            {
                Record record;
                if (!_records.TryGetValue(connection.ModuleName, out record)) { return; }
                if (record.ConnectionId != connection.ConnectionId) { return; }
                record.State = ModuleState.Ready;
                record.Detail = "";
            }
        }

        private void onDisconnected(ModuleConnection connection)
        {
            if (_stopRequested) { return; }
            lock (_lock)
            {
                Record record;
                if (!_records.TryGetValue(connection.ModuleName, out record)) { return; }
                if (record.ConnectionId != connection.ConnectionId) { return; }
                if (record.State == ModuleState.Stopping) { return; }
                record.ConnectionLost = true;
            }
            _wake.Set();
        }

        private void onRefused(string moduleName, string reason)
        {
            lock (_lock)
            {
                Record record;
                if (!_records.TryGetValue(moduleName, out record)) { return; }
                if (record.State != ModuleState.Starting) { return; }
                record.RefusalReason = reason;
            }
            _wake.Set();
        }
    }
}
