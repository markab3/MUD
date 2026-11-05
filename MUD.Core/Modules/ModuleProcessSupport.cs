using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace MUD.Core.Modules
{
    /// <summary>The environment every module process gets, and the signals core sends to them.</summary>
    internal static class ModuleProcessSupport
    {
        private const int SignalTerminate = 15;
        private const int SignalKill = 9;

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int processId, int signal);

        /// <summary>
        /// A cleaned environment (MODULES 5.1): PATH, HOME, XDG_RUNTIME_DIR and DOTNET_ROOT when core has them. In particular
        /// MUD_MONGO_URI is NOT passed on: the core is the only process that talks to MongoDB.
        /// </summary>
        public static void ApplyCleanEnvironment(ProcessStartInfo startInfo)
        {
            startInfo.Environment.Clear();
            foreach (string name in new[] { "PATH", "HOME", "XDG_RUNTIME_DIR", "DOTNET_ROOT" })
            {
                string value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(value)) { startInfo.Environment[name] = value; }
            }
        }

        public static bool SendTerminate(int processId) { return kill(processId, SignalTerminate) == 0; }

        public static bool SendKill(int processId) { return kill(processId, SignalKill) == 0; }

        /// <summary>
        /// True when the process is no longer running. A zombie (exited, not yet collected by its parent) counts as gone.
        /// </summary>
        public static bool IsGone(int processId)
        {
            string stat;
            try { stat = File.ReadAllText("/proc/" + processId + "/stat"); }
            catch (Exception) { return true; }
            // The state letter follows the closing bracket of the command name: "pid (name) S ..."
            int close = stat.LastIndexOf(')');
            return close < 0 || close + 2 >= stat.Length || stat[close + 2] == 'Z' || stat[close + 2] == 'X';
        }

        /// <summary>Asks the process to end, waits up to the grace period, then kills it. Returns true when it is gone.</summary>
        public static bool TerminateProcess(int processId, TimeSpan grace)
        {
            if (IsGone(processId)) { return true; }
            SendTerminate(processId);
            if (WaitUntilGone(processId, grace)) { return true; }
            SendKill(processId);
            return WaitUntilGone(processId, TimeSpan.FromSeconds(2));
        }

        public static bool WaitUntilGone(int processId, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (!IsGone(processId))
            {
                if (DateTime.UtcNow >= deadline) { return false; }
                Thread.Sleep(20);
            }
            return true;
        }
    }
}
