using System;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.Configuration;
using MUD.Core.Modules;

namespace MUD.Main
{
    /// <summary>
    /// Reads the supervisor numbers from the ModuleHost section of appsettings.json. A missing or invalid value keeps the default in
    /// <see cref="ModuleSupervisorOptions"/>. Times are in seconds.
    ///
    ///   ModuleHost:ModulesDirectory        the one directory core scans for modules (default: "modules" next to MUD.Main)
    ///   ModuleHost:StartupTimeoutSeconds   D70, default 30
    ///   ModuleHost:RestartBackoffSeconds   list, for example "1,2,5,10,30"
    ///   ModuleHost:CrashLoopMaxCrashes, CrashLoopWindowSeconds, PingTimeoutSeconds, TerminateGraceSeconds,
    ///   ModuleHost:DescribeTimeoutSeconds, DescribeMaxOutputCharacters
    /// </summary>
    public static class ModuleSupervisorSettings
    {
        public static ModuleSupervisorOptions Read(IConfiguration config, string baseDirectory, Action<string> log)
        {
            var options = new ModuleSupervisorOptions();
            string directory = config["ModuleHost:ModulesDirectory"];
            if (string.IsNullOrWhiteSpace(directory)) { directory = "modules"; }
            options.ModulesDirectory = System.IO.Path.IsPathRooted(directory) ? directory : System.IO.Path.Combine(baseDirectory, directory);

            options.StartupTimeout = Seconds(config, "StartupTimeoutSeconds", options.StartupTimeout, log);
            options.DescribeTimeout = Seconds(config, "DescribeTimeoutSeconds", options.DescribeTimeout, log);
            options.TerminateGrace = Seconds(config, "TerminateGraceSeconds", options.TerminateGrace, log);
            options.CrashLoopWindow = Seconds(config, "CrashLoopWindowSeconds", options.CrashLoopWindow, log);
            options.PingTimeout = Seconds(config, "PingTimeoutSeconds", options.PingTimeout, log);

            int number;
            if (int.TryParse(config["ModuleHost:CrashLoopMaxCrashes"], out number) && number > 0) { options.CrashLoopMaxCrashes = number; }
            if (int.TryParse(config["ModuleHost:DescribeMaxOutputCharacters"], out number) && number > 0) { options.DescribeMaxOutputCharacters = number; }

            string backoff = config["ModuleHost:RestartBackoffSeconds"];
            if (!string.IsNullOrWhiteSpace(backoff))
            {
                var steps = backoff.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v >= 0 ? (double?)v : null)
                    .ToList();
                if (steps.Count > 0 && steps.All(s => s.HasValue)) { options.RestartBackoff = steps.Select(s => TimeSpan.FromSeconds(s.Value)).ToList(); }
                else { log("ModuleHost:RestartBackoffSeconds '" + backoff + "' is not a list of seconds; the default is kept."); }
            }
            return options;
        }

        private static TimeSpan Seconds(IConfiguration config, string key, TimeSpan fallback, Action<string> log)
        {
            string text = config["ModuleHost:" + key];
            if (string.IsNullOrWhiteSpace(text)) { return fallback; }
            double value;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0)
            {
                return TimeSpan.FromSeconds(value);
            }
            log("ModuleHost:" + key + " '" + text + "' is not a positive number of seconds; the default of " + fallback.TotalSeconds + " is kept.");
            return fallback;
        }
    }
}
