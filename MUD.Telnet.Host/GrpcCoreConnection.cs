using System;
using System.Collections.Generic;
using MUD.CoreApi;
using MUD.CoreApi.Client;

namespace MUD.Telnet.Host
{
    /// <summary>The real <see cref="ICoreConnection"/>: a <see cref="GrpcCoreApi"/> on frontends.sock. It only forwards.</summary>
    public sealed class GrpcCoreConnection : ICoreConnection
    {
        private readonly GrpcCoreApi _api;

        public GrpcCoreConnection(GrpcCoreApi api)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
        }

        public event Action<string> ConnectionLost
        {
            add { _api.ConnectionLost += value; }
            remove { _api.ConnectionLost -= value; }
        }

        public string Connect() { return _api.Connect(); }

        public SessionId OpenSession(string frontEnd, string remoteKey, IFrontEndSink sink) { return _api.OpenSession(frontEnd, remoteKey, sink); }

        public void SubmitLine(SessionId id, string rawLine) { _api.SubmitLine(id, rawLine); }

        public void CloseSession(SessionId id, SessionEndReason reason) { _api.CloseSession(id, reason); }

        public LoginResult Authenticate(SessionId id, string name, string password) { return _api.Authenticate(id, name, password); }

        public LoginResult CreatePlayer(SessionId id, string name, string password) { return _api.CreatePlayer(id, name, password); }

        public bool IsThrottled(string remoteKey) { return _api.IsThrottled(remoteKey); }

        public void RecordLoginOutcome(string remoteKey, bool success) { _api.RecordLoginOutcome(remoteKey, success); }

        public IReadOnlyList<string> GetOnlineNames() { return _api.GetOnlineNames(); }

        public void Dispose() { _api.Dispose(); }
    }
}
