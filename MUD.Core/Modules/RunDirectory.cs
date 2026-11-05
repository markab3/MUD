using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MUD.Core.Modules
{
    /// <summary>Another core already holds the run directory lock.</summary>
    public sealed class RunDirectoryLockedException : Exception
    {
        public RunDirectoryLockedException(string message) : base(message) { }
    }

    /// <summary>
    /// The run directory (CORE_API 7.3): created by core with mode 0700, holds the two socket files and core.lock. Core takes an
    /// exclusive flock on core.lock (a second core fails fast), then deletes stale *.sock files, then binds. A socket file is
    /// never deleted without the lock. The lock is released when this object is disposed or the process dies.
    /// </summary>
    public sealed class RunDirectory : IDisposable
    {
        public const string RunDirectoryVariable = "MUD_RUN_DIRECTORY";
        public const string ModulesSocketName = "modules.sock";
        public const string FrontEndsSocketName = "frontends.sock";
        public const string LockFileName = "core.lock";

        // A Unix socket path may not exceed 107 bytes plus the terminator (sun_path is 108 bytes).
        private const int MaxSocketPathBytes = 107;

        private const int LockExclusive = 2;
        private const int LockNonBlocking = 4;

        [DllImport("libc", SetLastError = true)]
        private static extern int flock(int fileDescriptor, int operation);

        private readonly Action<string> _log;
        private FileStream _lockFile;

        private RunDirectory(string path, FileStream lockFile, Action<string> log)
        {
            Path = path;
            _lockFile = lockFile;
            _log = log;
        }

        public string Path { get; private set; }

        public string ModulesSocketPath { get { return System.IO.Path.Combine(Path, ModulesSocketName); } }

        public string FrontEndsSocketPath { get { return System.IO.Path.Combine(Path, FrontEndsSocketName); } }

        /// <summary>
        /// Where the run directory is: the override variable MUD_RUN_DIRECTORY, else $XDG_RUNTIME_DIR/mud, else
        /// ~/.local/share/mud/run (CORE_API 7.3). The override variable name was chosen by card P4-2.
        /// </summary>
        public static string ResolvePath()
        {
            string overridePath = Environment.GetEnvironmentVariable(RunDirectoryVariable);
            if (!string.IsNullOrWhiteSpace(overridePath)) { return overridePath; }
            string runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrWhiteSpace(runtime)) { return System.IO.Path.Combine(runtime, "mud"); }
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "mud", "run");
        }

        /// <summary>
        /// Creates the directory (0700), takes the lock and removes stale socket files.
        /// Throws RunDirectoryLockedException when another core holds the lock.
        /// </summary>
        public static RunDirectory Acquire(string path, Action<string> log = null)
        {
            log = log ?? Console.WriteLine;
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) { throw new PlatformNotSupportedException("The module host needs Unix domain sockets and flock."); }
            if (string.IsNullOrWhiteSpace(path)) { throw new ArgumentException("A run directory path is required.", nameof(path)); }
            path = System.IO.Path.GetFullPath(path);

            foreach (string name in new[] { ModulesSocketName, FrontEndsSocketName })
            {
                int bytes = Encoding.UTF8.GetByteCount(System.IO.Path.Combine(path, name));
                if (bytes > MaxSocketPathBytes)
                {
                    throw new InvalidOperationException("The socket path " + System.IO.Path.Combine(path, name) + " is " + bytes
                        + " bytes long; the limit for a Unix socket path is " + MaxSocketPathBytes + ". Use a shorter run directory (" + RunDirectoryVariable + ").");
                }
            }

            const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(path, ownerOnly);
            // CreateDirectory leaves an existing directory alone and applies the process umask to a new one: make the mode certain.
            File.SetUnixFileMode(path, ownerOnly);

            string lockPath = System.IO.Path.Combine(path, LockFileName);
            FileStream lockFile;
            try
            {
                // On Unix .NET takes its own advisory flock when a file is opened, so a second open while a core holds the
                // lock fails here with an IOException (found by test); the explicit flock below covers the other case.
                lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            }
            catch (IOException)
            {
                throw new RunDirectoryLockedException("Another core is running: " + lockPath + " is locked. Stop it first, or use a different " + RunDirectoryVariable + ".");
            }
            try
            {
                File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                int fileDescriptor = (int)lockFile.SafeFileHandle.DangerousGetHandle();
                if (flock(fileDescriptor, LockExclusive | LockNonBlocking) != 0)
                {
                    int error = Marshal.GetLastWin32Error();
                    const int WouldBlock = 11; // EWOULDBLOCK / EAGAIN on Linux
                    if (error == WouldBlock)
                    {
                        throw new RunDirectoryLockedException("Another core is running: " + lockPath + " is locked. Stop it first, or use a different " + RunDirectoryVariable + ".");
                    }
                    throw new IOException("Could not lock " + lockPath + " (error " + error + ").");
                }

                lockFile.SetLength(0);
                byte[] pid = Encoding.ASCII.GetBytes(Environment.ProcessId.ToString() + "\n");
                lockFile.Write(pid, 0, pid.Length);
                lockFile.Flush();

                // We hold the lock, so any socket file here belongs to a core that is gone.
                foreach (string stale in Directory.GetFiles(path, "*.sock"))
                {
                    log("Removing stale socket file " + stale);
                    File.Delete(stale);
                }
            }
            catch
            {
                lockFile.Dispose();
                throw;
            }
            return new RunDirectory(path, lockFile, log);
        }

        /// <summary>Sets a bound socket file to mode 0600. Failure is fatal (CORE_API 7.3): the exception is not caught here.</summary>
        public static void RestrictSocket(string socketPath)
        {
            File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        /// <summary>Deletes the socket files and releases the lock.</summary>
        public void Dispose()
        {
            FileStream lockFile = _lockFile;
            if (lockFile == null) { return; }
            _lockFile = null;
            foreach (string socket in new[] { ModulesSocketPath, FrontEndsSocketPath })
            {
                try { if (File.Exists(socket)) { File.Delete(socket); } }
                catch (Exception ex) { _log("Could not remove " + socket + ": " + ex.Message); }
            }
            lockFile.Dispose(); // closing the descriptor releases the flock
        }
    }
}
