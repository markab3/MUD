using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using MUD.Contracts;
using MUD.CoreApi;

namespace MUD.Core.Sessions
{
    /// <summary>
    /// The front-end connections accepted on frontends.sock (card P7-1). Connect is the first call of a front end (D66): it presents
    /// the token read from the mode-600 token file, its name and the contract version, which must equal core's (D24, strict). A
    /// successful Connect returns a connection id that every later call carries. The sessions themselves live in core's session
    /// registry (D46); this class only remembers which connection opened which, so that a front end that goes away takes its
    /// sessions with it (a lost front end logs its players out, as a lost telnet connection does today).
    /// </summary>
    public sealed class FrontEndRegistry
    {
        /// <summary>The default capacity of a connection's event queue (a setting, FrontEnds:OutboundQueueCapacity).</summary>
        public const int DefaultOutboundQueueCapacity = 2000;

        private readonly object _lock = new object();
        private readonly Dictionary<string, FrontEndConnection> _byId = new Dictionary<string, FrontEndConnection>(StringComparer.Ordinal);
        private readonly Dictionary<string, FrontEndConnection> _byName = new Dictionary<string, FrontEndConnection>(StringComparer.Ordinal);
        private readonly ICoreApi _core;
        private readonly FrontEndTokenFile _tokenFile;
        private readonly uint _contractVersion;
        private readonly int _outboundCapacity;
        private readonly Action<string> _log;

        public FrontEndRegistry(ICoreApi core, FrontEndTokenFile tokenFile, uint contractVersion, int outboundQueueCapacity = DefaultOutboundQueueCapacity, Action<string> log = null)
        {
            _core = core ?? throw new ArgumentNullException(nameof(core));
            _tokenFile = tokenFile ?? throw new ArgumentNullException(nameof(tokenFile));
            _contractVersion = contractVersion;
            _outboundCapacity = outboundQueueCapacity > 0 ? outboundQueueCapacity : DefaultOutboundQueueCapacity;
            _log = log ?? Console.WriteLine;
        }

        public ICoreApi Core { get { return _core; } }

        public int ConnectionCount
        {
            get { lock (_lock) { return _byId.Count; } }
        }

        /// <summary>Checks the hello and accepts the connection. A refusal carries a fixed sentence; the token is never logged or echoed.</summary>
        public FrontEndWelcome Connect(FrontEndHello hello)
        {
            string name = hello == null ? string.Empty : (hello.Name ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                return Refuse("A front end name is required.");
            }
            if (!_tokenFile.Validate(hello.Token))
            {
                _log("Front end \"" + name + "\" refused: the token does not match.");
                return Refuse("The front-end token was not accepted.");
            }
            if (hello.ContractVersion != _contractVersion)
            {
                _log("Front end \"" + name + "\" refused: it speaks contract version " + hello.ContractVersion + " and core speaks " + _contractVersion + ".");
                return Refuse("Contract version mismatch: the front end speaks " + hello.ContractVersion + " and core speaks " + _contractVersion + ".");
            }

            FrontEndConnection replaced = null;
            FrontEndConnection connection = new FrontEndConnection(NewConnectionId(), name, _outboundCapacity, OnOverflow);
            lock (_lock)
            {
                // A front end that restarts connects again before core has noticed its old stream is gone: the newer connection wins.
                if (_byName.TryGetValue(name, out replaced)) { _byId.Remove(replaced.ConnectionId); }
                _byId[connection.ConnectionId] = connection;
                _byName[name] = connection;
            }
            if (replaced != null) { EndConnection(replaced, "replaced by a newer connection of the same front end"); }
            _log("Front end \"" + name + "\" connected.");
            return new FrontEndWelcome { Ok = true, ConnectionId = connection.ConnectionId, CoreContractVersion = _contractVersion };
        }

        /// <summary>The connection for an id, or null.</summary>
        public FrontEndConnection Find(string connectionId)
        {
            if (string.IsNullOrEmpty(connectionId)) { return null; }
            lock (_lock)
            {
                FrontEndConnection connection;
                return _byId.TryGetValue(connectionId, out connection) ? connection : null;
            }
        }

        /// <summary>The front end is gone (its stream ended, or it was cut off): its sessions are closed in core as if their connections dropped.</summary>
        public void Disconnect(FrontEndConnection connection, string reason)
        {
            if (connection == null) { return; }
            lock (_lock)
            {
                FrontEndConnection current;
                if (_byId.TryGetValue(connection.ConnectionId, out current) && ReferenceEquals(current, connection))
                {
                    _byId.Remove(connection.ConnectionId);
                    if (_byName.TryGetValue(connection.Name, out current) && ReferenceEquals(current, connection)) { _byName.Remove(connection.Name); }
                }
            }
            EndConnection(connection, reason);
        }

        /// <summary>
        /// Core is stopping: every event stream is completed after the events already queued are written, so the host can stop without
        /// waiting on open streams. The sessions are not closed one by one here; the world has already ended them (D29).
        /// </summary>
        public void CompleteAll()
        {
            List<FrontEndConnection> all;
            lock (_lock) { all = new List<FrontEndConnection>(_byId.Values); }
            foreach (FrontEndConnection connection in all) { connection.Complete(); }
        }

        private void EndConnection(FrontEndConnection connection, string reason)
        {
            connection.Complete();
            string[] sessions = connection.TakeSessions();
            foreach (string sessionId in sessions) { _core.CloseSession(new SessionId(sessionId), SessionEndReason.ClientClosed); }
            _log("Front end \"" + connection.Name + "\" disconnected (" + reason + "); " + sessions.Length + " session(s) closed.");
        }

        // Called from the game thread when a connection's queue is full: the work moves off that thread.
        private void OnOverflow(FrontEndConnection connection, string reason)
        {
            Task.Run(() => Disconnect(connection, reason));
        }

        private static FrontEndWelcome Refuse(string refusal)
        {
            return new FrontEndWelcome { Ok = false, Refusal = refusal };
        }

        private static string NewConnectionId()
        {
            byte[] bytes = new byte[16];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}
