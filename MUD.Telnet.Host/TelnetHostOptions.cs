using System;
using System.IO;
using System.Net;
using MUD.Telnet;

namespace MUD.Telnet.Host
{
    /// <summary>
    /// The settings of the telnet host process, all from environment variables (the systemd unit sets them; optional overrides come from
    /// ~/.config/mud/telnet.env). Nothing here is a secret: the front-end token is read from its own mode-600 file (D66) and is not kept here.
    /// </summary>
    public sealed class TelnetHostOptions
    {
        public const string BindAddressVariable = "MUD_TELNET_BIND_ADDRESS";
        public const string PortVariable = "MUD_TELNET_PORT";
        public const string RunDirectoryVariable = "MUD_RUN_DIRECTORY";
        public const string TokenFileVariable = "MUD_FRONTEND_TOKEN_FILE";
        public const string FrontEndsSocketName = "frontends.sock";
        public const int DefaultPort = 7680;

        public IPAddress BindAddress { get; set; } = IPAddress.Any;

        public int Port { get; set; } = DefaultPort;

        /// <summary>Where frontends.sock is. Same rule as core's RunDirectory.ResolvePath (CORE_API 7.3).</summary>
        public string SocketPath { get; set; }

        /// <summary>The mode-600 file that holds the front-end token (D66).</summary>
        public string TokenFilePath { get; set; }

        public TelnetLimits Limits { get; set; } = new TelnetLimits();

        public static TelnetHostOptions FromEnvironment(Func<string, string> getVariable = null, string homeDirectory = null)
        {
            getVariable = getVariable ?? Environment.GetEnvironmentVariable;
            homeDirectory = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var options = new TelnetHostOptions();

            IPAddress address;
            if (IPAddress.TryParse(getVariable(BindAddressVariable), out address)) { options.BindAddress = address; }
            int port;
            if (int.TryParse(getVariable(PortVariable), out port) && port > 0 && port <= 65535) { options.Port = port; }

            string runDirectory = getVariable(RunDirectoryVariable);
            if (string.IsNullOrWhiteSpace(runDirectory))
            {
                string runtimeDirectory = getVariable("XDG_RUNTIME_DIR");
                runDirectory = !string.IsNullOrWhiteSpace(runtimeDirectory)
                    ? Path.Combine(runtimeDirectory, "mud")
                    : Path.Combine(homeDirectory, ".local", "share", "mud", "run");
            }
            options.SocketPath = Path.Combine(Path.GetFullPath(runDirectory), FrontEndsSocketName);

            string tokenFile = getVariable(TokenFileVariable);
            options.TokenFilePath = !string.IsNullOrWhiteSpace(tokenFile) ? tokenFile : Path.Combine(homeDirectory, ".config", "mud", "frontend-token");

            options.Limits = TelnetLimits.FromEnvironment(getVariable);
            return options;
        }
    }
}
