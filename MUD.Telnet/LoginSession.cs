using System;
using MUD.CoreApi;

namespace MUD.Telnet
{
    /// <summary>
    /// The parts of a connection the pre-login flow needs. Implemented by <see cref="Client"/>; tests supply a fake.
    /// </summary>
    public interface ILoginConnection
    {
        /// <summary>Address used as the login-throttle key (empty when unknown).</summary>
        string RemoteKey { get; }

        /// <summary>Sends a line of text (a line ending is appended).</summary>
        void Send(string text);

        /// <summary>Sends text without a line ending, for prompts.</summary>
        void SendPrompt(string text);

        /// <summary>
        /// Turns client-side echo off (IAC WILL ECHO) or back on (IAC WONT ECHO) and stops the server echoing
        /// input lines while hidden.
        /// </summary>
        void SetEchoHidden(bool hidden);

        void DisconnectWithMessage(string message);
    }

    public enum LoginState
    {
        /// <summary>Not in a prompted flow; lines are treated as pre-login commands.</summary>
        Idle,
        AwaitingName,
        AwaitingPassword,
        AwaitingConfirm
    }

    /// <summary>
    /// Per-connection pre-login state machine: the prompted "connect"/"create" flow plus the legacy one-line forms.
    /// It asks core to log in or create through <see cref="ICoreApi"/>, which also holds the brute-force throttle
    /// (decision D43). Passwords are never logged or echoed.
    /// </summary>
    public class LoginSession
    {
        public const string LegacyNotice =
            "Note: your password was visible on that line. The prompted form is preferred: type 'connect' or 'create' on its own.";

        private readonly ILoginConnection _connection;
        private readonly ICoreApi _core;
        private readonly SessionId _sessionId;

        private bool _creating;
        private string _name;
        private string _password;

        public LoginSession(ILoginConnection connection, ICoreApi core, SessionId sessionId)
        {
            _connection = connection;
            _core = core;
            _sessionId = sessionId;
        }

        public LoginState State { get; private set; } = LoginState.Idle;

        /// <summary>True once login or creation has succeeded; the owner should stop routing lines here.</summary>
        public bool IsLoggedIn { get; private set; }

        /// <summary>
        /// Returns a version of <paramref name="line"/> that is safe to write to a log, given the current state.
        /// </summary>
        public string Loggable(string line)
        {
            if (State == LoginState.AwaitingPassword || State == LoginState.AwaitingConfirm) { return "<hidden>"; }
            if (State == LoginState.Idle && line != null)
            {
                string[] parts = line.Split(' ');
                if (parts.Length > 2 && (IsLoginVerb(parts[0]) || IsCreateVerb(parts[0])))
                {
                    return parts[0] + " " + parts[1] + " <hidden>";
                }
            }
            return line;
        }

        /// <summary>
        /// Handles one line. Returns true if the line was consumed by the login flow; false if it is not a login
        /// command (only possible while Idle) and the caller should process it.
        /// </summary>
        public bool HandleLine(string line)
        {
            line = line ?? string.Empty;
            switch (State)
            {
                case LoginState.AwaitingName:
                    return handleName(line.Trim());
                case LoginState.AwaitingPassword:
                    return handlePassword(line);
                case LoginState.AwaitingConfirm:
                    return handleConfirm(line);
            }

            string[] parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) { return false; }

            bool isLogin = IsLoginVerb(parts[0]);
            bool isCreate = IsCreateVerb(parts[0]);
            if (!isLogin && !isCreate) { return false; }

            if (parts.Length == 1)
            {
                begin(isCreate);
            }
            else if (parts.Length == 3)
            {
                connectLegacy(isCreate, parts[1], parts[2]);
            }
            else
            {
                _connection.Send(isCreate
                    ? "Format: create   (you will be prompted), or create <username> <password>"
                    : "Format: connect   (you will be prompted), or connect <username> <password>");
            }
            return true;
        }

        private static bool IsLoginVerb(string verb)
        {
            return verb == "connect" || verb == "login";
        }

        private static bool IsCreateVerb(string verb)
        {
            return verb == "create";
        }

        private void begin(bool creating)
        {
            _creating = creating;
            _name = null;
            _password = null;
            State = LoginState.AwaitingName;
            _connection.SendPrompt("Name: ");
        }

        private bool handleName(string name)
        {
            if (name.Length == 0)
            {
                reset();
                _connection.Send("Cancelled.");
                return true;
            }
            _name = name;
            State = LoginState.AwaitingPassword;
            _connection.SetEchoHidden(true);
            _connection.SendPrompt("Password: ");
            return true;
        }

        private bool handlePassword(string password)
        {
            _connection.SetEchoHidden(false);
            _connection.Send(string.Empty); // The client did not echo the user's Enter.

            if (_creating)
            {
                _password = password;
                State = LoginState.AwaitingConfirm;
                _connection.SetEchoHidden(true);
                _connection.SendPrompt("Confirm password: ");
                return true;
            }

            string name = _name;
            reset();
            attemptLogin(name, password);
            return true;
        }

        private bool handleConfirm(string confirm)
        {
            _connection.SetEchoHidden(false);
            _connection.Send(string.Empty);

            string name = _name;
            string password = _password;
            reset();

            if (password != confirm)
            {
                _connection.Send("The passwords did not match. Type 'create' to try again.");
                return true;
            }
            attemptCreate(name, password);
            return true;
        }

        private void connectLegacy(bool creating, string name, string password)
        {
            _connection.Send(LegacyNotice);
            if (creating) { attemptCreate(name, password); } else { attemptLogin(name, password); }
        }

        private void attemptLogin(string name, string password)
        {
            string key = _connection.RemoteKey ?? string.Empty;
            if (_core.IsThrottled(key))
            {
                _connection.DisconnectWithMessage("Too many failed login attempts. Please try again later.");
                return;
            }

            LoginResult result = _core.Authenticate(_sessionId, name, password);
            if (result == LoginResult.Success || result == LoginResult.AlreadyOnline)
            {
                IsLoggedIn = true;
                _core.RecordLoginOutcome(key, true);
            }
            else
            {
                _core.RecordLoginOutcome(key, false);
                _connection.Send(string.Format("Login failed for {0}.", name));
            }
        }

        private void attemptCreate(string name, string password)
        {
            LoginResult result = _core.CreatePlayer(_sessionId, name, password);
            if (result == LoginResult.Success)
            {
                IsLoggedIn = true;
            }
            else if (result == LoginResult.NameTaken)
            {
                _connection.Send(string.Format("There is already someone by the name of {0}.", name));
            }
        }

        private void reset()
        {
            State = LoginState.Idle;
            _name = null;
            _password = null;
        }
    }
}
