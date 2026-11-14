using System;
using System.Runtime.CompilerServices;
using MUD.CoreApi;

namespace MUD.Telnet
{
    /// <summary>
    /// The telnet front end's binding to core: it listens to a <see cref="Server"/> and gives every connection a core
    /// session through <see cref="ICoreApi"/>. A new connection gets a <see cref="TelnetSessionSink"/> and a
    /// <see cref="PreLoginHandler"/>; its lines go to the handler until login succeeds and to
    /// <see cref="ICoreApi.SubmitLine"/> afterwards. This project knows core only as <see cref="ICoreApi"/>.
    /// </summary>
    public sealed class TelnetFrontEnd
    {
        public const string FrontEndName = "telnet";

        private sealed class Binding
        {
            public SessionId SessionId;
        }

        private readonly ICoreApi _core;
        private readonly string _welcomeMessage;
        private readonly Action<string> _log;
        private readonly ConditionalWeakTable<Client, Binding> _bindings = new ConditionalWeakTable<Client, Binding>();

        public TelnetFrontEnd(ICoreApi core, string welcomeMessage, Action<string> log = null)
        {
            _core = core ?? throw new ArgumentNullException(nameof(core));
            _welcomeMessage = welcomeMessage ?? string.Empty;
            _log = log ?? Console.WriteLine;
        }

        /// <summary>Starts handling the server's connections. Call before the server starts.</summary>
        public void Attach(Server server)
        {
            if (server == null) { throw new ArgumentNullException(nameof(server)); }
            server.ClientConnected += clientConnected;
            server.ClientDisconnected += clientDisconnected;
        }

        private void clientConnected(object sender, Client client)
        {
            _log("CONNECTED: " + client);

            SessionId sessionId = _core.OpenSession(FrontEndName, client.RemoteKey, new TelnetSessionSink(client));
            _bindings.Add(client, new Binding { SessionId = sessionId });
            var preLogin = new PreLoginHandler(_core, sessionId, client);
            client.DataReceived += (s, line) =>
            {
                if (preLogin.IsLoggedIn)
                {
                    // Lines from now on go to the player (core routes by session).
                    _core.SubmitLine(sessionId, line);
                    return;
                }
                preLogin.HandleLine(line);
                if (preLogin.IsLoggedIn) { client.IsLoggedIn = true; }
            };
            client.Send(_welcomeMessage);
            preLogin.ShowWho();
            client.Send("");
        }

        private void clientDisconnected(object sender, Client client)
        {
            Binding binding;
            if (_bindings.TryGetValue(client, out binding)) { _core.CloseSession(binding.SessionId, SessionEndReason.ClientClosed); }
            _log("DISCONNECTED: " + client);
        }
    }
}
