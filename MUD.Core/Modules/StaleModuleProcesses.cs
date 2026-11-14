using System;
using System.Collections.Generic;
using System.IO;

namespace MUD.Core.Modules
{
    /// <summary>
    /// The stale-process check (owner decision D68). Just before core starts a module it looks in /proc for processes owned by the
    /// same user whose resolved executable path (/proc/&lt;pid&gt;/exe) equals the module's full executable path, ignoring a trailing
    /// " (deleted)" (a file replaced on disk), asks each to terminate, waits a short grace period and force-kills what remains.
    /// Matching is on the full path, never the bare name.
    ///
    /// A module published for .NET without its own launcher runs as `dotnet Module.dll`; there /proc/&lt;pid&gt;/exe is the dotnet binary
    /// and the module is recognised by the full path on the command line instead. Only a process whose executable is called
    /// "dotnet" gets that second test, so an editor that merely has the module file open is never matched.
    ///
    /// Consequence of D68 to remember: a module started by hand from the same path is killed the next time core starts that module.
    /// </summary>
    public static class StaleModuleProcesses
    {
        private const string DeletedSuffix = " (deleted)";

        /// <summary>
        /// The pure matching rule. <paramref name="processExecutable"/> is the target of /proc/pid/exe and
        /// <paramref name="commandLine"/> the process's arguments, program first.
        /// </summary>
        public static bool Matches(string processExecutable, IReadOnlyList<string> commandLine, string moduleExecutablePath)
        {
            if (string.IsNullOrEmpty(processExecutable) || string.IsNullOrEmpty(moduleExecutablePath)) { return false; }
            string exe = processExecutable.EndsWith(DeletedSuffix, StringComparison.Ordinal)
                ? processExecutable.Substring(0, processExecutable.Length - DeletedSuffix.Length)
                : processExecutable;
            if (string.Equals(exe, moduleExecutablePath, StringComparison.Ordinal)) { return true; }

            if (string.Equals(Path.GetFileName(exe), "dotnet", StringComparison.Ordinal) && commandLine != null)
            {
                for (int index = 1; index < commandLine.Count; index++)
                {
                    string argument = commandLine[index];
                    if (string.IsNullOrEmpty(argument) || argument.StartsWith("-", StringComparison.Ordinal)) { continue; }
                    // The first argument that is not a host option is the program being run. It must be a full path.
                    if (!Path.IsPathRooted(argument)) { return false; }
                    return string.Equals(argument, moduleExecutablePath, StringComparison.Ordinal)
                        || string.Equals(argument, moduleExecutablePath + ".dll", StringComparison.Ordinal);
                }
            }
            return false;
        }

        /// <summary>Process ids of the same user (other than <paramref name="excludeProcessId"/>) that match the module executable.</summary>
        public static IReadOnlyList<int> Find(string moduleExecutablePath, int excludeProcessId)
        {
            var found = new List<int>();
            string userId = ReadUserId("/proc/self/status");
            if (userId == null) { return found; }

            string[] entries;
            try { entries = Directory.GetDirectories("/proc"); }
            catch (Exception) { return found; }

            foreach (string entry in entries)
            {
                int processId;
                if (!int.TryParse(Path.GetFileName(entry), out processId) || processId == excludeProcessId) { continue; }
                try
                {
                    if (!string.Equals(ReadUserId(entry + "/status"), userId, StringComparison.Ordinal)) { continue; }
                    string target = new FileInfo(entry + "/exe").LinkTarget;
                    if (target == null) { continue; }
                    if (Matches(target, ReadCommandLine(entry + "/cmdline"), moduleExecutablePath)) { found.Add(processId); }
                }
                catch (Exception)
                {
                    // The process ended while we looked, or it is not ours to read: not a candidate.
                }
            }
            return found;
        }

        /// <summary>Finds and ends stale processes for the module. Returns how many were found. Never throws for a process that vanishes.</summary>
        public static int Terminate(string moduleExecutablePath, TimeSpan grace, Action<string> log)
        {
            IReadOnlyList<int> stale = Find(moduleExecutablePath, Environment.ProcessId);
            foreach (int processId in stale)
            {
                log("Stale process " + processId + " runs " + moduleExecutablePath + ": asking it to terminate.");
                SendAndWait(processId, grace, log);
            }
            return stale.Count;
        }

        private static void SendAndWait(int processId, TimeSpan grace, Action<string> log)
        {
            if (ModuleProcessSupport.IsGone(processId)) { return; }
            ModuleProcessSupport.SendTerminate(processId);
            if (ModuleProcessSupport.WaitUntilGone(processId, grace)) { return; }
            log("Stale process " + processId + " did not end within " + grace.TotalSeconds + " s: killing it.");
            ModuleProcessSupport.SendKill(processId);
            if (!ModuleProcessSupport.WaitUntilGone(processId, TimeSpan.FromSeconds(2)))
            {
                log("WARNING: stale process " + processId + " is still running after the kill signal.");
            }
        }

        private static string ReadUserId(string statusPath)
        {
            try
            {
                foreach (string line in File.ReadLines(statusPath))
                {
                    if (line.StartsWith("Uid:", StringComparison.Ordinal))
                    {
                        string[] parts = line.Substring(4).Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        return parts.Length > 0 ? parts[0] : null;
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        private static IReadOnlyList<string> ReadCommandLine(string path)
        {
            try { return File.ReadAllText(path).Split('\0', StringSplitOptions.RemoveEmptyEntries); }
            catch (Exception) { return new string[0]; }
        }
    }
}
