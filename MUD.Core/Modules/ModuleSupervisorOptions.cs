using System;
using System.Collections.Generic;

namespace MUD.Core.Modules
{
    /// <summary>
    /// The numbers and paths of the module supervisor. Owner decision D70 sets the start-up timeout (30 seconds per required
    /// module) and the systemd restart limit. EVERY OTHER NUMBER BELOW IS A DEFAULT CHOSEN BY CARD P4-3 and is flagged in its
    /// pull request for the owner to confirm or change (D20 and D67 leave the backoff, the crash-loop policy, the describe
    /// limits and the grace period to the card). All are read from the ModuleHost section of appsettings.json.
    /// </summary>
    public class ModuleSupervisorOptions
    {
        /// <summary>
        /// The one directory core scans for modules (D67): one folder per module, the executable inside named after the folder.
        /// A missing directory means no modules.
        /// </summary>
        public string ModulesDirectory { get; set; }

        /// <summary>
        /// D70: a required module that is not Ready this long after core starts it makes core exit with a clear error. The same
        /// limit makes the supervisor give up on any module that was started but never became Ready (it is killed and goes to backoff).
        /// </summary>
        public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>How long one `--describe` run may take before it is killed and the module is reported as invalid. (Default chosen by P4-3.)</summary>
        public TimeSpan DescribeTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>The most characters of standard output a `--describe` run may print; more than this and the run is killed. (Default chosen by P4-3.)</summary>
        public int DescribeMaxOutputCharacters { get; set; } = 64 * 1024;

        /// <summary>
        /// How long a process gets between the terminate signal and the force kill, both for stale processes found at start (D68)
        /// and for modules the supervisor stops. (Default chosen by P4-3.)
        /// </summary>
        public TimeSpan TerminateGrace { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Wait before restart number 1, 2, 3 and so on after a crash; the last value is used for every later restart. (Default chosen by P4-3.)
        /// </summary>
        public IReadOnlyList<TimeSpan> RestartBackoff { get; set; } = new[]
        {
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)
        };

        /// <summary>
        /// Crash-loop policy (D20; numbers chosen by P4-3): this many unexpected stops inside <see cref="CrashLoopWindow"/> and the
        /// module is marked Failed and is not restarted again until an admin runs `modules start`.
        /// </summary>
        public int CrashLoopMaxCrashes { get; set; } = 5;

        public TimeSpan CrashLoopWindow { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// A Ready module that has not called Ping for this long is hung: it is stopped and restarted like a crash (MODULES section 3).
        /// The module library must ping well inside this time. (Default chosen by P4-3.)
        /// </summary>
        public TimeSpan PingTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>How often the supervisor looks at timeouts and restart times.</summary>
        public TimeSpan TickInterval { get; set; } = TimeSpan.FromMilliseconds(200);
    }
}
