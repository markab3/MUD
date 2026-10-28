using System;
using System.Collections.Generic;
using System.Linq;
using MUD.CoreApi;

namespace MUD.Telnet
{
    /// <summary>
    /// Everything a telnet connection can type before it has logged in: <c>connect</c> and <c>create</c> (through
    /// <see cref="LoginSession"/>), <c>who</c>, <c>quit</c>, and the reply to anything else. It runs on the thread that
    /// delivers the connection's lines, so the wait inside <see cref="ICoreApi.Authenticate"/> never holds up the game
    /// thread. Once <see cref="IsLoggedIn"/> is true the host sends the connection's lines to
    /// <see cref="ICoreApi.SubmitLine"/> instead.
    /// </summary>
    public class PreLoginHandler
    {
        private readonly ICoreApi _core;
        private readonly ILoginConnection _connection;
        private readonly LoginSession _session;
        private readonly Action<string> _log;

        public PreLoginHandler(ICoreApi core, SessionId sessionId, ILoginConnection connection, Action<string> log = null)
        {
            _core = core ?? throw new ArgumentNullException(nameof(core));
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _session = new LoginSession(connection, core, sessionId);
            _log = log ?? Console.WriteLine;
        }

        /// <summary>True once login or character creation has succeeded.</summary>
        public bool IsLoggedIn { get { return _session.IsLoggedIn; } }

        /// <summary>The same list text the game uses elsewhere: "a", "a and b", "a, b and c".</summary>
        public static string FormatNames(IReadOnlyList<string> names)
        {
            if (names.Count == 1) { return names[0]; }
            return string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];
        }

        /// <summary>Tells the connection who is online (the reply to <c>who</c>, also shown on connect).</summary>
        public void ShowWho()
        {
            IReadOnlyList<string> names = _core.GetOnlineNames();
            if (names == null || names.Count == 0)
            {
                _connection.Send("No one is logged on.");
            }
            else if (names.Count == 1)
            {
                _connection.Send(string.Format("{0} is logged on.", names[0]));
            }
            else
            {
                _connection.Send(string.Format("{0} are logged on.", FormatNames(names)));
            }
        }

        /// <summary>Handles one line typed before login.</summary>
        public void HandleLine(string message)
        {
            message = (message ?? string.Empty).TrimEnd('\n').TrimEnd('\r');

            // Never log a line that may contain a password.
            _log("MESSAGE: " + _session.Loggable(message));

            if (_session.State != LoginState.Idle)
            {
                _session.HandleLine(message);
            }
            else if (message == "quit" || message.StartsWith("quit "))
            {
                _connection.Disconnect(false);
            }
            else if (message == "who" || message.StartsWith("who "))
            {
                ShowWho();
            }
            else if (_session.HandleLine(message))
            {
                // Consumed by the login flow.
            }
            else
            {
                _connection.Send(string.Format("Command {0} was not recognized.", message));
            }
        }
    }
}
