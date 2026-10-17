using System;

namespace MUD.Telnet
{
    /// <summary>
    /// Input, connection and abuse limits for the telnet server. Defaults are the owner-approved values;
    /// each can be overridden with an environment variable (see FromEnvironment).
    /// </summary>
    public class TelnetLimits
    {
        /// <summary>Max bytes in a single input line. Env: MUD_MAX_LINE_LENGTH</summary>
        public int MaxLineLength { get; set; } = 1024;

        /// <summary>Max simultaneous connections per remote IP. Env: MUD_MAX_CONNECTIONS_PER_IP</summary>
        public int MaxConnectionsPerIp { get; set; } = 5;

        /// <summary>Max simultaneous connections overall. Env: MUD_MAX_CONNECTIONS</summary>
        public int MaxTotalConnections { get; set; } = 200;

        /// <summary>Idle time allowed once logged in. Env: MUD_IDLE_TIMEOUT_LOGGED_IN_SECONDS</summary>
        public TimeSpan IdleTimeoutLoggedIn { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>Idle time allowed before login. Env: MUD_IDLE_TIMEOUT_PRELOGIN_SECONDS</summary>
        public TimeSpan IdleTimeoutPreLogin { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>Lines allowed per rate window. Env: MUD_RATE_LIMIT_LINES</summary>
        public int RateLimitLines { get; set; } = 20;

        /// <summary>Length of the rate window. Env: MUD_RATE_LIMIT_WINDOW_SECONDS</summary>
        public TimeSpan RateLimitWindow { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Failed logins from one IP before it is throttled. Env: MUD_LOGIN_MAX_FAILURES</summary>
        public int LoginMaxFailures { get; set; } = 5;

        /// <summary>How long a throttled IP is refused. Env: MUD_LOGIN_THROTTLE_SECONDS</summary>
        public TimeSpan LoginThrottleDuration { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Builds limits from defaults plus environment overrides. Missing, unparsable or non-positive
        /// values fall back to the default. A custom lookup can be supplied for testing.
        /// </summary>
        public static TelnetLimits FromEnvironment(Func<string, string> getVariable = null)
        {
            getVariable = getVariable ?? Environment.GetEnvironmentVariable;
            var limits = new TelnetLimits();
            limits.MaxLineLength = readInt(getVariable, "MUD_MAX_LINE_LENGTH", limits.MaxLineLength);
            limits.MaxConnectionsPerIp = readInt(getVariable, "MUD_MAX_CONNECTIONS_PER_IP", limits.MaxConnectionsPerIp);
            limits.MaxTotalConnections = readInt(getVariable, "MUD_MAX_CONNECTIONS", limits.MaxTotalConnections);
            limits.IdleTimeoutLoggedIn = readSeconds(getVariable, "MUD_IDLE_TIMEOUT_LOGGED_IN_SECONDS", limits.IdleTimeoutLoggedIn);
            limits.IdleTimeoutPreLogin = readSeconds(getVariable, "MUD_IDLE_TIMEOUT_PRELOGIN_SECONDS", limits.IdleTimeoutPreLogin);
            limits.RateLimitLines = readInt(getVariable, "MUD_RATE_LIMIT_LINES", limits.RateLimitLines);
            limits.RateLimitWindow = readSeconds(getVariable, "MUD_RATE_LIMIT_WINDOW_SECONDS", limits.RateLimitWindow);
            limits.LoginMaxFailures = readInt(getVariable, "MUD_LOGIN_MAX_FAILURES", limits.LoginMaxFailures);
            limits.LoginThrottleDuration = readSeconds(getVariable, "MUD_LOGIN_THROTTLE_SECONDS", limits.LoginThrottleDuration);
            return limits;
        }

        private static int readInt(Func<string, string> getVariable, string name, int fallback)
        {
            return int.TryParse(getVariable(name), out int value) && value > 0 ? value : fallback;
        }

        private static TimeSpan readSeconds(Func<string, string> getVariable, string name, TimeSpan fallback)
        {
            return int.TryParse(getVariable(name), out int value) && value > 0 ? TimeSpan.FromSeconds(value) : fallback;
        }
    }
}
