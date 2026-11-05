using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MUD.Contracts;

namespace MUD.Core.Modules
{
    /// <summary>
    /// The host side of the module handshake (owner decisions D16, D17, D18, D21, D24, D65): authenticates the first call,
    /// checks the contract version for strict equality, receives the manifest and compares its capabilities with the grants
    /// file. Holds the live connections. Safe to call from any thread; raises its events on the thread that caused them.
    /// </summary>
    public sealed class ModuleRegistry
    {
        private readonly ModuleTokenRegistry _tokens;
        private readonly ModuleGrants _grants;
        private readonly uint _coreContractVersion;
        private readonly ModuleHostOptions _options;
        private readonly Action<string> _log;
        private readonly object _lock = new object();
        private readonly Dictionary<string, ModuleConnection> _byConnectionId = new Dictionary<string, ModuleConnection>(StringComparer.Ordinal);
        private readonly Dictionary<string, ModuleConnection> _byModuleName = new Dictionary<string, ModuleConnection>(StringComparer.Ordinal);
        // The newest token sequence accepted per module, so a token is good for one connection only.
        private readonly Dictionary<string, long> _lastAcceptedSequence = new Dictionary<string, long>(StringComparer.Ordinal);

        public ModuleRegistry(ModuleTokenRegistry tokens, ModuleGrants grants, uint coreContractVersion, ModuleHostOptions options = null, Action<string> log = null)
        {
            _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
            _grants = grants ?? throw new ArgumentNullException(nameof(grants));
            _coreContractVersion = coreContractVersion;
            _options = options ?? new ModuleHostOptions();
            _log = log ?? Console.WriteLine;
        }

        public ModuleHostOptions Options { get { return _options; } }

        public ModuleTokenRegistry Tokens { get { return _tokens; } }

        public ModuleGrants Grants { get { return _grants; } }

        /// <summary>A module passed Hello. Raised on a gRPC handler thread.</summary>
        public event Action<ModuleConnection> Connected;

        /// <summary>A module called Ready. Raised on the game thread.</summary>
        public event Action<ModuleConnection> Ready;

        /// <summary>A connection ended, for any reason. Raised on the thread that noticed.</summary>
        public event Action<ModuleConnection> Disconnected;

        /// <summary>The connections that are open now.</summary>
        public IReadOnlyList<ModuleConnection> Connections()
        {
            lock (_lock) { return _byConnectionId.Values.ToList(); }
        }

        public ModuleConnection Find(string connectionId)
        {
            if (string.IsNullOrEmpty(connectionId)) { return null; }
            lock (_lock)
            {
                ModuleConnection connection;
                return _byConnectionId.TryGetValue(connectionId, out connection) ? connection : null;
            }
        }

        public ModuleConnection FindByModule(string moduleName)
        {
            if (string.IsNullOrEmpty(moduleName)) { return null; }
            lock (_lock)
            {
                ModuleConnection connection;
                return _byModuleName.TryGetValue(moduleName, out connection) ? connection : null;
            }
        }

        /// <summary>The first call of a module. Never throws for a bad request: the reply says why it was refused.</summary>
        public HelloReply Hello(HelloRequest request)
        {
            var reply = new HelloReply { Ok = false, CoreContractVersion = _coreContractVersion };
            string moduleName = request == null ? "" : request.Module ?? "";

            long sequence;
            if (request == null || !_tokens.Validate(moduleName, request.Token, out sequence))
            {
                // The reply does not say whether the name or the token was wrong.
                _log("Module handshake refused for '" + moduleName + "': the token is not the one core issued for this module.");
                reply.Refusal = "Authentication failed: the token is not valid for this module.";
                return reply;
            }

            if (request.ContractVersion != _coreContractVersion)
            {
                string message = "Contract version mismatch: module '" + moduleName + "' sent version " + request.ContractVersion
                    + ", core speaks version " + _coreContractVersion + ". Strict equality is required; rebuild the module against the current contracts.";
                _log("Module handshake refused: " + message);
                reply.Refusal = message;
                return reply;
            }

            Manifest manifest = request.Manifest;
            if (manifest == null)
            {
                _log("Module handshake refused for '" + moduleName + "': no manifest was sent.");
                reply.Refusal = "No manifest was sent. The first call must carry the manifest.";
                return reply;
            }
            if (!string.IsNullOrEmpty(manifest.Name) && !string.Equals(manifest.Name, moduleName, StringComparison.Ordinal))
            {
                _log("Module handshake refused for '" + moduleName + "': the manifest names '" + manifest.Name + "'.");
                reply.Refusal = "The manifest name '" + manifest.Name + "' does not match the module name '" + moduleName + "'.";
                return reply;
            }

            // Requested = the capabilities list plus anything listed as required (a required capability is always requested).
            var requested = new List<string>();
            foreach (string capability in manifest.Capabilities.Concat(manifest.RequiredCapabilities))
            {
                if (!string.IsNullOrWhiteSpace(capability) && !requested.Contains(capability)) { requested.Add(capability); }
            }
            var granted = new List<string>();
            var rejected = new List<string>();
            foreach (string capability in requested)
            {
                (_grants.IsGranted(moduleName, capability) ? granted : rejected).Add(capability);
            }

            var missingRequired = manifest.RequiredCapabilities.Where(c => !string.IsNullOrWhiteSpace(c) && rejected.Contains(c)).Distinct().ToList();
            if (missingRequired.Count > 0)
            {
                string message = "Module '" + moduleName + "' requires capabilities that the grants file does not allow: " + string.Join(", ", missingRequired)
                    + ". Edit " + _grants.FilePath + " and reload the grants.";
                _log("Module handshake refused: " + message);
                reply.Refusal = message;
                reply.Rejected.AddRange(rejected);
                return reply;
            }

            ModuleConnection connection;
            ModuleConnection replaced = null;
            lock (_lock)
            {
                long lastAccepted;
                if (_lastAcceptedSequence.TryGetValue(moduleName, out lastAccepted) && sequence <= lastAccepted)
                {
                    _log("Module handshake refused for '" + moduleName + "': this token was already used for a connection.");
                    reply.Refusal = "This token was already used. Core issues a new token for every start.";
                    return reply;
                }
                _byModuleName.TryGetValue(moduleName, out replaced);
                _lastAcceptedSequence[moduleName] = sequence;
                connection = new ModuleConnection(Guid.NewGuid().ToString("N"), moduleName, request.ModuleVersion ?? "", sequence,
                    manifest, granted, rejected, _options.OutboundQueueCapacity);
                _byConnectionId[connection.ConnectionId] = connection;
                _byModuleName[moduleName] = connection;
                if (replaced != null) { _byConnectionId.Remove(replaced.ConnectionId); }
            }

            // A newer spawn of the same module replaces the older connection (restart overlap, CORE_API 7.3).
            if (replaced != null)
            {
                _log("Module '" + moduleName + "' reconnected with a newer token: the earlier connection is closed.");
                replaced.Close();
                RaiseDisconnected(replaced);
            }

            if (rejected.Count > 0)
            {
                _log("WARNING: module '" + moduleName + "' starts with a partial grant. Not granted: " + string.Join(", ", rejected) + ".");
            }
            _log("Module '" + moduleName + "' (version " + connection.ModuleVersion + ") connected as " + connection.ConnectionId + "; granted: "
                + (granted.Count == 0 ? "nothing" : string.Join(", ", granted)) + ".");

            reply.Ok = true;
            reply.ConnectionId = connection.ConnectionId;
            reply.Granted.AddRange(granted);
            reply.Rejected.AddRange(rejected);
            Raise(Connected, connection);
            return reply;
        }

        /// <summary>Marks the connection ready. Must run on the game thread; the service posts it there.</summary>
        internal void MarkReady(ModuleConnection connection)
        {
            if (connection.IsClosed || connection.IsReady) { return; }
            connection.IsReady = true;
            _log("Module '" + connection.ModuleName + "' is ready.");
            Raise(Ready, connection);
        }

        /// <summary>Ends a connection (stream ended, module gone, or core stopping). Safe to call more than once.</summary>
        public void Disconnect(ModuleConnection connection, string reason)
        {
            if (connection == null) { return; }
            bool removed = false;
            lock (_lock)
            {
                ModuleConnection current;
                if (_byConnectionId.TryGetValue(connection.ConnectionId, out current) && ReferenceEquals(current, connection))
                {
                    _byConnectionId.Remove(connection.ConnectionId);
                    removed = true;
                }
                if (_byModuleName.TryGetValue(connection.ModuleName, out current) && ReferenceEquals(current, connection))
                {
                    _byModuleName.Remove(connection.ModuleName);
                }
            }
            connection.Close();
            if (removed)
            {
                _log("Module '" + connection.ModuleName + "' disconnected (" + reason + ").");
                RaiseDisconnected(connection);
            }
        }

        /// <summary>Ends every stream, so modules see the end at once instead of waiting for the host to give up (spike finding, Q6).</summary>
        public void DisconnectAll(string reason)
        {
            foreach (ModuleConnection connection in Connections()) { Disconnect(connection, reason); }
        }

        private void RaiseDisconnected(ModuleConnection connection) { Raise(Disconnected, connection); }

        private void Raise(Action<ModuleConnection> handlers, ModuleConnection connection)
        {
            if (handlers == null) { return; }
            foreach (Action<ModuleConnection> handler in handlers.GetInvocationList())
            {
                try { handler(connection); }
                catch (Exception ex) { _log("Exception in a module event handler: " + ex); }
            }
        }
    }
}
