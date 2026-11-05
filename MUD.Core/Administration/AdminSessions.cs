using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace MUD.Core.Administration
{
    /// <summary>One signed-in web session: which player it belongs to. The role is never stored here; it is read again on every call (D31).</summary>
    public sealed class AdminSession
    {
        public AdminSession(string token, string playerId, string nameKey)
        {
            Token = token;
            PlayerId = playerId;
            NameKey = nameKey;
        }

        public string Token { get; private set; }

        public string PlayerId { get; private set; }

        public string NameKey { get; private set; }
    }

    /// <summary>
    /// The in-memory table of web session tokens (owner decision D31). A token is an opaque random string that core issues when
    /// the web authenticates a player; the table is lost when core restarts, so every web user signs in again after a restart.
    /// Safe from any thread. A token is a credential: it is never written to a log, and the table can only be looked up by token.
    /// </summary>
    public sealed class AdminSessions
    {
        private const int TokenBytes = 32;

        private readonly object _lock = new object();
        private readonly Dictionary<string, AdminSession> _sessions = new Dictionary<string, AdminSession>(StringComparer.Ordinal);

        /// <summary>Number of live sessions.</summary>
        public int Count
        {
            get { lock (_lock) { return _sessions.Count; } }
        }

        /// <summary>Opens a session for the player and returns it. The token is 256 random bits, URL-safe text.</summary>
        public AdminSession Issue(string playerId, string nameKey)
        {
            string token;
            lock (_lock)
            {
                do { token = NewToken(); } while (_sessions.ContainsKey(token));
                var session = new AdminSession(token, playerId, nameKey);
                _sessions[token] = session;
                return session;
            }
        }

        /// <summary>The session for the token, or null for an empty, unknown or revoked token.</summary>
        public AdminSession Find(string token)
        {
            if (string.IsNullOrEmpty(token)) { return null; }
            lock (_lock)
            {
                AdminSession session;
                return _sessions.TryGetValue(token, out session) ? session : null;
            }
        }

        /// <summary>Ends one session. Returns false when there was none.</summary>
        public bool Revoke(string token)
        {
            if (string.IsNullOrEmpty(token)) { return false; }
            lock (_lock) { return _sessions.Remove(token); }
        }

        /// <summary>Ends every session of the player (a retirement). Returns how many were ended.</summary>
        public int RevokeAllFor(string playerId)
        {
            lock (_lock)
            {
                List<string> tokens = _sessions.Values.Where(s => string.Equals(s.PlayerId, playerId, StringComparison.Ordinal)).Select(s => s.Token).ToList();
                foreach (string token in tokens) { _sessions.Remove(token); }
                return tokens.Count;
            }
        }

        private static string NewToken()
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(TokenBytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
}
