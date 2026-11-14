using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using MUD.CoreApi;

namespace MUD.Core.Sessions
{
    /// <summary>
    /// The registry of open sessions (decision D46): session id to sink and player. Lives in core. Safe from any thread;
    /// front ends open and close sessions on their own threads while the game thread looks sessions up.
    ///
    /// Ids are 128 random bits from the operating system's cryptographic random generator, written as 32 lowercase hex
    /// characters: unguessable, and a repeat is not possible in practice. An id is also never handed out while it is
    /// still registered.
    /// </summary>
    public sealed class SessionRegistry
    {
        private readonly ConcurrentDictionary<string, SessionConnection> _sessions =
            new ConcurrentDictionary<string, SessionConnection>(StringComparer.Ordinal);

        public int Count { get { return _sessions.Count; } }

        public SessionConnection Open(string frontEnd, string remoteKey, IFrontEndSink sink, Func<bool> worldIsStopping)
        {
            if (sink == null) { throw new ArgumentNullException(nameof(sink)); }
            while (true)
            {
                var id = new SessionId(NewIdText());
                var connection = new SessionConnection(id, frontEnd, remoteKey, sink, worldIsStopping);
                if (_sessions.TryAdd(id.Value, connection)) { return connection; }
            }
        }

        public bool TryGet(SessionId id, out SessionConnection connection)
        {
            connection = null;
            return !id.IsEmpty && _sessions.TryGetValue(id.Value, out connection);
        }

        public bool Remove(SessionId id, out SessionConnection connection)
        {
            connection = null;
            return !id.IsEmpty && _sessions.TryRemove(id.Value, out connection);
        }

        private static string NewIdText()
        {
            byte[] bytes = new byte[16];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}
