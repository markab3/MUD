using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace MUD.Core.Modules
{
    /// <summary>
    /// The per-spawn tokens (owner decision D17). Core issues a random token for a module name when it starts that module and
    /// passes it in the child's environment; the module presents it in its first call. The token guards against accidents, not
    /// against a hostile process of the same user (D19). There is no fixed or development token (D69).
    /// </summary>
    public sealed class ModuleTokenRegistry
    {
        private sealed class Issued
        {
            public string Token;
            public long Sequence;
        }

        private readonly object _lock = new object();
        private readonly Dictionary<string, Issued> _byModule = new Dictionary<string, Issued>(StringComparer.Ordinal);
        private long _sequence;

        /// <summary>Issues a new random token for the module, replacing any earlier one. A later issue has a higher sequence number.</summary>
        public string Issue(string moduleName)
        {
            if (string.IsNullOrWhiteSpace(moduleName)) { throw new ArgumentException("A module name is required.", nameof(moduleName)); }
            byte[] bytes = new byte[32];
            RandomNumberGenerator.Fill(bytes);
            string token = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            lock (_lock)
            {
                _byModule[moduleName] = new Issued { Token = token, Sequence = ++_sequence };
            }
            return token;
        }

        /// <summary>True when the token is the one currently issued for the module. The sequence number orders the spawns of one module.</summary>
        public bool Validate(string moduleName, string token, out long sequence)
        {
            sequence = 0;
            if (string.IsNullOrEmpty(moduleName) || string.IsNullOrEmpty(token)) { return false; }
            lock (_lock)
            {
                Issued issued;
                if (!_byModule.TryGetValue(moduleName, out issued)) { return false; }
                bool equal = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(issued.Token), Encoding.UTF8.GetBytes(token));
                if (!equal) { return false; }
                sequence = issued.Sequence;
                return true;
            }
        }

        public void Revoke(string moduleName)
        {
            lock (_lock) { _byModule.Remove(moduleName); }
        }
    }
}
