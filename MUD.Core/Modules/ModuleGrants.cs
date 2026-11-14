using System;
using System.Collections.Generic;
using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MUD.Core.Modules
{
    /// <summary>
    /// The owner-edited grants file (owner decisions D18, D65), by default ~/.config/mud/module-grants.yaml. Read by core only
    /// and reloadable without restarting core. D65 gives the vocabulary (one capability per line per module); the file layout
    /// below was chosen by card P4-2 and is flagged in its pull request:
    ///
    ///   modules:
    ///     desert:
    ///       - emit:entity.move
    ///       - timer.schedule
    ///       - state
    ///       - read
    ///
    /// A module with no entry, or a missing file, has no grants. A file that cannot be parsed leaves the earlier grants in force.
    /// </summary>
    public sealed class ModuleGrants
    {
        private sealed class GrantsDocument
        {
            public Dictionary<string, List<string>> Modules { get; set; }
        }

        private readonly string _path;
        private readonly Action<string> _log;
        private readonly object _lock = new object();
        private Dictionary<string, HashSet<string>> _granted = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        public ModuleGrants(string path, Action<string> log = null)
        {
            _path = path;
            _log = log ?? Console.WriteLine;
        }

        public static string DefaultPath
        {
            get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "mud", "module-grants.yaml"); }
        }

        public string FilePath { get { return _path; } }

        /// <summary>Reads the file. Returns false, and keeps the earlier grants, when it exists but cannot be read or parsed.</summary>
        public bool Reload()
        {
            if (!File.Exists(_path))
            {
                lock (_lock) { _granted = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal); }
                _log("Module grants file not found at " + _path + ": no module has any grant.");
                return true;
            }
            try
            {
                WarnIfReadableByOthers();
                string text = File.ReadAllText(_path);
                var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
                var document = deserializer.Deserialize<GrantsDocument>(text);
                var loaded = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                if (document != null && document.Modules != null)
                {
                    foreach (var entry in document.Modules)
                    {
                        var set = new HashSet<string>(StringComparer.Ordinal);
                        if (entry.Value != null)
                        {
                            foreach (string capability in entry.Value)
                            {
                                if (!string.IsNullOrWhiteSpace(capability)) { set.Add(capability.Trim()); }
                            }
                        }
                        loaded[entry.Key] = set;
                    }
                }
                lock (_lock) { _granted = loaded; }
                _log("Module grants loaded from " + _path + " (" + loaded.Count + " module(s)).");
                return true;
            }
            catch (Exception ex)
            {
                _log("Module grants file " + _path + " could not be read; the earlier grants stay in force: " + ex.Message);
                return false;
            }
        }

        /// <summary>True when the module is granted the capability (exact match of the string).</summary>
        public bool IsGranted(string moduleName, string capability)
        {
            lock (_lock)
            {
                HashSet<string> set;
                return _granted.TryGetValue(moduleName, out set) && set.Contains(capability);
            }
        }

        private void WarnIfReadableByOthers()
        {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) { return; }
            UnixFileMode mode = File.GetUnixFileMode(_path);
            UnixFileMode others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((mode & others) != 0)
            {
                _log("WARNING: " + _path + " should be mode 600 (owner only); its mode is " + Convert.ToString((int)mode, 8) + ".");
            }
        }
    }
}
