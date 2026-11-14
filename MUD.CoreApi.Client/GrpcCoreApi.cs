using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using MUD.Contracts;
using MUD.CoreApi;

namespace MUD.CoreApi.Client
{
    /// <summary>
    /// The gRPC implementation of <see cref="ICoreApi"/> for a front end that runs in its own process (card P7-1, owner decision D66).
    /// It talks to the Session service on frontends.sock: one server stream (Events) carries the output and session events of every
    /// session of this front end to the sinks given to <see cref="OpenSession"/>, and the calls of the interface are separate unary calls.
    ///
    /// Rules this class keeps:
    /// - The sinks are called on the stream's reader thread, one event at a time and in the order core sent them. A sink never blocks (D42).
    /// - <see cref="SubmitLine"/>, <see cref="CloseSession"/> and <see cref="RecordLoginOutcome"/> only enqueue, as the interface says. One
    ///   sender drains the queue, so the lines of a session reach core in the order they were typed.
    /// - <see cref="Authenticate"/>, <see cref="CreatePlayer"/>, <see cref="IsThrottled"/> and <see cref="GetOnlineNames"/> wait for core, for a
    ///   bounded time. A call that cannot reach core returns <see cref="LoginResult.TryAgainLater"/>, false or an empty list.
    /// - When the stream or a call shows that core is gone (it stopped, restarted, or forgot this connection), every open session is ended
    ///   with <see cref="SessionEndReason.WorldStopping"/> (the telnet sink then shows "world is restarting", D29) and
    ///   <see cref="ConnectionLost"/> is raised once. Reconnecting is the host's job (card P7-2); this class does not retry.
    /// </summary>
    public sealed class GrpcCoreApi : ICoreApi, IDisposable
    {
        /// <summary>How long a unary call may take before it gives up. Core's own wait for the game thread is shorter than this.</summary>
        public static readonly TimeSpan DefaultCallDeadline = TimeSpan.FromSeconds(15);

        // Events that arrive for a session before OpenSession has returned are held, up to this many per session.
        private const int MaxHeldEventsPerSession = 64;

        private readonly GrpcChannel _channel;
        private readonly Session.SessionClient _client;
        private readonly string _frontEndName;
        private readonly string _token;
        private readonly Action<string> _log;
        private readonly TimeSpan _callDeadline;
        private readonly object _lock = new object();
        private readonly Dictionary<string, IFrontEndSink> _sinks = new Dictionary<string, IFrontEndSink>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<SessionEvent>> _held = new Dictionary<string, List<SessionEvent>>(StringComparer.Ordinal);
        private readonly Channel<Func<Task>> _sends = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private string _connectionId;
        private Task _reader;
        private Task _sender;
        private int _lost;

        /// <param name="channel">The channel to frontends.sock; see <see cref="OpenChannel"/>.</param>
        /// <param name="frontEndName">For example "telnet". Core names the sessions of this connection after it.</param>
        /// <param name="token">The front-end token read from the mode-600 token file (D66). Never logged.</param>
        public GrpcCoreApi(GrpcChannel channel, string frontEndName, string token, Action<string> log = null, TimeSpan? callDeadline = null)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            if (string.IsNullOrWhiteSpace(frontEndName)) { throw new ArgumentException("A front end name is required.", nameof(frontEndName)); }
            _frontEndName = frontEndName;
            _token = token ?? string.Empty;
            _client = new Session.SessionClient(channel);
            _log = log ?? Console.WriteLine;
            _callDeadline = callDeadline ?? DefaultCallDeadline;
        }

        /// <summary>Raised once when core is gone. The argument is a short reason without any secret. Raised on a pool thread.</summary>
        public event Action<string> ConnectionLost;

        public bool IsConnected { get { return _connectionId != null && Volatile.Read(ref _lost) == 0; } }

        /// <summary>The number of sessions this front end has open.</summary>
        public int OpenSessionCount
        {
            get { lock (_lock) { return _sinks.Count; } }
        }

        /// <summary>Opens a channel to a Unix domain socket, h2c, as the other clients of frontends.sock do.</summary>
        public static GrpcChannel OpenChannel(string socketPath)
        {
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (context, cancellation) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellation);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };
            return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });
        }

        /// <summary>
        /// Presents the token, the name and the contract version, and opens the event stream. Returns null on success or the refusal text
        /// (a wrong token, a version mismatch) or "core is not reachable". Call once, before any session is opened.
        /// </summary>
        public string Connect()
        {
            if (_connectionId != null) { throw new InvalidOperationException("Already connected."); }
            FrontEndWelcome welcome;
            try
            {
                welcome = _client.Connect(new FrontEndHello { Name = _frontEndName, Token = _token, ContractVersion = ContractVersion.Current },
                    deadline: DateTime.UtcNow + _callDeadline);
            }
            catch (RpcException)
            {
                return "core is not reachable";
            }
            if (!welcome.Ok) { return string.IsNullOrEmpty(welcome.Refusal) ? "core refused the connection" : welcome.Refusal; }

            _connectionId = welcome.ConnectionId;
            _sender = Task.Run(SendLoopAsync);
            _reader = Task.Run(ReadLoopAsync);
            return null;
        }

        // ---- ICoreApi ----

        /// <summary>
        /// Opens a session in core. Waits for core's reply (the id comes from core, D46). <paramref name="frontEnd"/> is not sent: core names the
        /// session after the name given at <see cref="Connect"/>. A failure to reach core returns an empty id.
        /// </summary>
        public SessionId OpenSession(string frontEnd, string remoteKey, IFrontEndSink sink)
        {
            if (sink == null) { throw new ArgumentNullException(nameof(sink)); }
            if (!IsConnected) { return default(SessionId); }
            OpenSessionReply reply;
            try
            {
                reply = _client.OpenSession(new OpenSessionRequest { ConnectionId = _connectionId, RemoteKey = remoteKey ?? string.Empty },
                    deadline: DateTime.UtcNow + _callDeadline);
            }
            catch (RpcException ex)
            {
                NoteFailure(ex);
                return default(SessionId);
            }
            List<SessionEvent> held;
            lock (_lock)
            {
                _sinks[reply.SessionId] = sink;
                _held.Remove(reply.SessionId, out held);
            }
            if (held != null) { foreach (SessionEvent early in held) { Deliver(sink, early); } }
            return new SessionId(reply.SessionId);
        }

        public void SubmitLine(SessionId id, string rawLine)
        {
            if (id.IsEmpty || !IsConnected) { return; }
            var request = new LineRequest { ConnectionId = _connectionId, SessionId = id.Value, Line = rawLine ?? string.Empty };
            Enqueue(() => _client.LineAsync(request, deadline: DateTime.UtcNow + _callDeadline).ResponseAsync);
        }

        public void CloseSession(SessionId id, SessionEndReason reason)
        {
            if (id.IsEmpty) { return; }
            lock (_lock)
            {
                // Core must not be called about a session the front end has closed, and this front end no longer wants its events.
                _sinks.Remove(id.Value);
                _held.Remove(id.Value);
            }
            if (!IsConnected) { return; }
            var request = new CloseSessionRequest { ConnectionId = _connectionId, SessionId = id.Value, Reason = ReasonName(reason) };
            Enqueue(() => _client.CloseSessionAsync(request, deadline: DateTime.UtcNow + _callDeadline).ResponseAsync);
        }

        public LoginResult Authenticate(SessionId id, string name, string password)
        {
            if (id.IsEmpty || !IsConnected) { return LoginResult.TryAgainLater; }
            try
            {
                AuthReply reply = _client.Authenticate(new AuthRequest { ConnectionId = _connectionId, SessionId = id.Value, Name = name ?? string.Empty, Password = password ?? string.Empty },
                    deadline: DateTime.UtcNow + _callDeadline);
                return FromWire(reply.Result);
            }
            catch (RpcException ex)
            {
                NoteFailure(ex);
                return LoginResult.TryAgainLater;
            }
        }

        public LoginResult CreatePlayer(SessionId id, string name, string password)
        {
            if (id.IsEmpty || !IsConnected) { return LoginResult.TryAgainLater; }
            try
            {
                AuthReply reply = _client.CreatePlayer(new CreatePlayerRequest { ConnectionId = _connectionId, SessionId = id.Value, Name = name ?? string.Empty, Password = password ?? string.Empty },
                    deadline: DateTime.UtcNow + _callDeadline);
                return FromWire(reply.Result);
            }
            catch (RpcException ex)
            {
                NoteFailure(ex);
                return LoginResult.TryAgainLater;
            }
        }

        public bool IsThrottled(string remoteKey)
        {
            if (!IsConnected) { return false; }
            try
            {
                return _client.IsThrottled(new ThrottleRequest { ConnectionId = _connectionId, RemoteKey = remoteKey ?? string.Empty },
                    deadline: DateTime.UtcNow + _callDeadline).Throttled;
            }
            catch (RpcException ex)
            {
                NoteFailure(ex);
                return false;
            }
        }

        public void RecordLoginOutcome(string remoteKey, bool success)
        {
            if (!IsConnected) { return; }
            var request = new LoginOutcomeRequest { ConnectionId = _connectionId, RemoteKey = remoteKey ?? string.Empty, Success = success };
            Enqueue(() => _client.RecordLoginOutcomeAsync(request, deadline: DateTime.UtcNow + _callDeadline).ResponseAsync);
        }

        public IReadOnlyList<string> GetOnlineNames()
        {
            if (!IsConnected) { return new List<string>(); }
            try
            {
                OnlineNamesReply reply = _client.GetOnlineNames(new OnlineNamesRequest { ConnectionId = _connectionId }, deadline: DateTime.UtcNow + _callDeadline);
                return new List<string>(reply.Names);
            }
            catch (RpcException ex)
            {
                NoteFailure(ex);
                return new List<string>();
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _sends.Writer.TryComplete();
            try { if (_reader != null) { _reader.Wait(TimeSpan.FromSeconds(2)); } } catch (Exception) { }
            try { if (_sender != null) { _sender.Wait(TimeSpan.FromSeconds(2)); } } catch (Exception) { }
            _channel.Dispose();
            _stop.Dispose();
        }

        // ---- the sender: one task, so calls leave in the order they were queued ----

        private void Enqueue(Func<Task> send)
        {
            _sends.Writer.TryWrite(send);
        }

        private async Task SendLoopAsync()
        {
            await foreach (Func<Task> send in _sends.Reader.ReadAllAsync())
            {
                try
                {
                    await send();
                }
                catch (RpcException ex)
                {
                    NoteFailure(ex);
                }
                catch (Exception ex)
                {
                    _log("A call to core failed: " + ex.GetType().Name + ".");
                }
            }
        }

        // ---- the reader: the event stream ----

        private async Task ReadLoopAsync()
        {
            string reason = "the event stream ended";
            try
            {
                using (AsyncServerStreamingCall<SessionEvent> call = _client.Events(new SessionEventsRequest { ConnectionId = _connectionId }, cancellationToken: _stop.Token))
                {
                    await foreach (SessionEvent sessionEvent in call.ResponseStream.ReadAllAsync(_stop.Token))
                    {
                        Dispatch(sessionEvent);
                    }
                }
                reason = "core closed the event stream";
            }
            catch (OperationCanceledException)
            {
                return; // this object is being disposed
            }
            catch (RpcException ex)
            {
                reason = "the event stream failed (" + ex.StatusCode + ")";
            }
            catch (Exception ex)
            {
                reason = "the event stream failed (" + ex.GetType().Name + ")";
            }
            LoseConnection(reason);
        }

        private void Dispatch(SessionEvent sessionEvent)
        {
            IFrontEndSink sink;
            lock (_lock)
            {
                if (!_sinks.TryGetValue(sessionEvent.SessionId, out sink))
                {
                    // Either OpenSession has not returned yet, or the session was closed here and core's last events are still in flight.
                    // Held events are dropped when the session is closed; ones for a session that never registers are bounded.
                    if (_held.Count < 1024)
                    {
                        List<SessionEvent> list;
                        if (!_held.TryGetValue(sessionEvent.SessionId, out list)) { list = new List<SessionEvent>(); _held[sessionEvent.SessionId] = list; }
                        if (list.Count < MaxHeldEventsPerSession) { list.Add(sessionEvent); }
                    }
                    return;
                }
                if (sessionEvent.BodyCase == SessionEvent.BodyOneofCase.Ended) { _sinks.Remove(sessionEvent.SessionId); }
            }
            Deliver(sink, sessionEvent);
        }

        private void Deliver(IFrontEndSink sink, SessionEvent sessionEvent)
        {
            var id = new SessionId(sessionEvent.SessionId);
            try
            {
                switch (sessionEvent.BodyCase)
                {
                    case SessionEvent.BodyOneofCase.Output:
                        sink.Output(id, ParseChannel(sessionEvent.Output.Channel), sessionEvent.Output.Text, sessionEvent.Output.Flush);
                        break;
                    case SessionEvent.BodyOneofCase.SetEcho:
                        sink.SetEchoHidden(id, sessionEvent.SetEcho.Hidden);
                        break;
                    case SessionEvent.BodyOneofCase.Prompt:
                        sink.Prompt(id, sessionEvent.Prompt.Text);
                        break;
                    case SessionEvent.BodyOneofCase.Close:
                        sink.Close(id, sessionEvent.Close.Farewell, sessionEvent.Close.Flush);
                        break;
                    case SessionEvent.BodyOneofCase.SetTerminal:
                        sink.SetTerminal(id, sessionEvent.SetTerminal.Name);
                        break;
                    case SessionEvent.BodyOneofCase.Ended:
                        sink.SessionEnded(id, ParseReason(sessionEvent.Ended.Reason));
                        break;
                }
            }
            catch (Exception ex)
            {
                // A sink that throws must not end the stream for every other session.
                _log("A session sink failed: " + ex.GetType().Name + ".");
            }
        }

        private void NoteFailure(RpcException ex)
        {
            // Unauthenticated: core no longer knows this connection (it restarted). Unavailable: the socket is gone.
            if (ex.StatusCode == StatusCode.Unauthenticated || ex.StatusCode == StatusCode.Unavailable)
            {
                LoseConnection("core is not reachable (" + ex.StatusCode + ")");
            }
        }

        private void LoseConnection(string reason)
        {
            if (Interlocked.Exchange(ref _lost, 1) != 0) { return; }
            List<KeyValuePair<string, IFrontEndSink>> open;
            lock (_lock)
            {
                open = new List<KeyValuePair<string, IFrontEndSink>>(_sinks);
                _sinks.Clear();
                _held.Clear();
            }
            _log("Lost core: " + reason + "; " + open.Count + " session(s) ended.");
            // Core cannot send the end of these sessions any more. The reason is WorldStopping so a telnet user is told the world is restarting (D29).
            foreach (KeyValuePair<string, IFrontEndSink> session in open)
            {
                try { session.Value.SessionEnded(new SessionId(session.Key), SessionEndReason.WorldStopping); }
                catch (Exception ex) { _log("A session sink failed: " + ex.GetType().Name + "."); }
            }
            _sends.Writer.TryComplete();
            Action<string> handler = ConnectionLost;
            if (handler != null)
            {
                Task.Run(() => { try { handler(reason); } catch (Exception) { } });
            }
        }

        // ---- wire conversions ----

        public static LoginResult FromWire(AuthResult result)
        {
            switch (result)
            {
                case AuthResult.AuthOk: return LoginResult.Success;
                case AuthResult.AuthBadCredentials: return LoginResult.BadCredentials;
                case AuthResult.AuthRetired: return LoginResult.Retired;
                case AuthResult.AuthThrottled: return LoginResult.Throttled;
                case AuthResult.AuthAlreadyOnline: return LoginResult.AlreadyOnline;
                case AuthResult.AuthNameTaken: return LoginResult.NameTaken;
                default: return LoginResult.TryAgainLater;
            }
        }

        /// <summary>The channel tags of D44. An empty or unknown tag is system.</summary>
        public static OutputChannel ParseChannel(string tag)
        {
            switch ((tag ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "say": return OutputChannel.Say;
                case "tell": return OutputChannel.Tell;
                case "room": return OutputChannel.Room;
                case "combat": return OutputChannel.Combat;
                case "chat": return OutputChannel.Chat;
                default: return OutputChannel.System;
            }
        }

        public static SessionEndReason ParseReason(string reason)
        {
            switch ((reason ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "client_closed": return SessionEndReason.ClientClosed;
                case "idle": return SessionEndReason.Idle;
                case "flood": return SessionEndReason.Flood;
                case "quit": return SessionEndReason.Quit;
                case "taken_over": return SessionEndReason.TakenOver;
                case "shutdown": return SessionEndReason.WorldStopping;
                default: return SessionEndReason.Quit;
            }
        }

        public static string ReasonName(SessionEndReason reason)
        {
            switch (reason)
            {
                case SessionEndReason.Idle: return "idle";
                case SessionEndReason.Flood: return "flood";
                case SessionEndReason.Quit: return "quit";
                case SessionEndReason.TakenOver: return "taken_over";
                case SessionEndReason.WorldStopping: return "shutdown";
                default: return "client_closed";
            }
        }
    }
}
