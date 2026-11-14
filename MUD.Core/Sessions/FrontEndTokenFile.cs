using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MUD.Core.Sessions
{
    /// <summary>
    /// The front-end token (owner decision D66, gap G21): a file with mode 600 that holds one random token. Front ends are started
    /// by systemd, not by core, so they cannot be handed a token through a child environment as modules are (D17); they read this
    /// file at start and present its content in their first call. Core reads the same file, and creates it with a new random token
    /// when it does not exist yet. Like the module token the token guards against accidents, not against a hostile process of the
    /// same user (D19). The token is never logged.
    /// </summary>
    public sealed class FrontEndTokenFile
    {
        public const string TokenFileVariable = "MUD_FRONTEND_TOKEN_FILE";

        private readonly string _token;

        private FrontEndTokenFile(string path, string token)
        {
            Path = path;
            _token = token;
        }

        public string Path { get; private set; }

        /// <summary>
        /// Where the token file is: the configured path when one is given, else the variable MUD_FRONTEND_TOKEN_FILE, else
        /// ~/.config/mud/frontend-token (next to the module grants file, CORE_API 7.3).
        /// </summary>
        public static string ResolvePath(string configuredPath)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath)) { return configuredPath; }
            string overridePath = Environment.GetEnvironmentVariable(TokenFileVariable);
            if (!string.IsNullOrWhiteSpace(overridePath)) { return overridePath; }
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "mud", "frontend-token");
        }

        /// <summary>
        /// Reads the token file, creating it (mode 600, with a new random token) when it does not exist. An existing file with
        /// group or other permissions is used, and a warning is logged. An empty file is refused.
        /// </summary>
        public static FrontEndTokenFile LoadOrCreate(string path, Action<string> log = null)
        {
            log = log ?? Console.WriteLine;
            if (string.IsNullOrWhiteSpace(path)) { throw new ArgumentException("A token file path is required.", nameof(path)); }
            path = System.IO.Path.GetFullPath(path);
            if (!File.Exists(path))
            {
                string directory = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }
                string created = NewToken();
                // Create with owner-only permissions from the start, so the token is never readable by others, even briefly.
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
                try
                {
                    using (var stream = new FileStream(path, options))
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(created + "\n");
                        stream.Write(bytes, 0, bytes.Length);
                    }
                    log("Front-end token file created at " + path + ".");
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Another process created it first; read what it wrote.
                }
            }
            string token = File.ReadAllText(path).Trim();
            if (token.Length == 0) { throw new InvalidOperationException("The front-end token file " + path + " is empty."); }
            WarnIfReadableByOthers(path, log);
            return new FrontEndTokenFile(path, token);
        }

        /// <summary>Reads an existing token file; used by a front end, which never creates it. Returns null when the file is missing or empty.</summary>
        public static string ReadToken(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { return null; }
            string token = File.ReadAllText(path).Trim();
            return token.Length == 0 ? null : token;
        }

        /// <summary>True when the presented token is the file's token. Compared in constant time.</summary>
        public bool Validate(string presented)
        {
            if (string.IsNullOrEmpty(presented)) { return false; }
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(_token), Encoding.UTF8.GetBytes(presented));
        }

        private static string NewToken()
        {
            byte[] bytes = new byte[32];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        private static void WarnIfReadableByOthers(string path, Action<string> log)
        {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) { return; }
            UnixFileMode mode = File.GetUnixFileMode(path);
            UnixFileMode others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((mode & others) != 0)
            {
                log("WARNING: " + path + " should be mode 600 (owner only); its mode is " + Convert.ToString((int)mode, 8) + ".");
            }
        }
    }
}
