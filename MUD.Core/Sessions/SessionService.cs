using System;
using System.Threading.Tasks;
using Grpc.Core;
using MUD.Contracts;
using MUD.CoreApi;

namespace MUD.Core.Sessions
{
    /// <summary>
    /// The Session gRPC service served on frontends.sock (owner decisions D22, D66; card P7-1). A thin layer over <see cref="ICoreApi"/>:
    /// the front end opens one server stream (Events) for the output and session events of all its sessions, and makes separate unary
    /// calls for open, login, create, lines and close. No call carries a stored value in an error; statuses are fixed sentences.
    /// A call that names an unknown connection answers Unauthenticated, which is what a front end sees after core restarted.
    /// A call that names a session the connection did not open answers PermissionDenied.
    /// </summary>
    public sealed class SessionService : Session.SessionBase
    {
        private readonly FrontEndRegistry _registry;

        public SessionService(FrontEndRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public override Task<FrontEndWelcome> Connect(FrontEndHello request, ServerCallContext context)
        {
            return Task.FromResult(_registry.Connect(request));
        }

        public override async Task Events(SessionEventsRequest request, IServerStreamWriter<SessionEvent> responseStream, ServerCallContext context)
        {
            FrontEndConnection connection = Require(request.ConnectionId);
            lock (connection)
            {
                if (connection.IsSubscribed)
                {
                    throw new RpcException(new Status(StatusCode.AlreadyExists, "This connection already has a stream."));
                }
                connection.IsSubscribed = true;
            }

            string reason = "stream ended";
            try
            {
                // One writer drains the bounded queue into the stream (CORE_API 7.1).
                await foreach (SessionEvent sessionEvent in connection.Reader.ReadAllAsync(context.CancellationToken))
                {
                    await responseStream.WriteAsync(sessionEvent);
                }
                reason = "core closed the stream";
            }
            catch (OperationCanceledException)
            {
                reason = "front end closed the stream";
            }
            finally
            {
                // A lost event stream ends the connection and its sessions.
                _registry.Disconnect(connection, reason);
            }
        }

        public override Task<OpenSessionReply> OpenSession(OpenSessionRequest request, ServerCallContext context)
        {
            FrontEndConnection connection = Require(request.ConnectionId);
            // The sink only enqueues, so this does not wait on the game thread.
            SessionId id = _registry.Core.OpenSession(connection.Name, request.RemoteKey ?? string.Empty, new RemoteFrontEndSink(connection));
            connection.Track(id.Value);
            return Task.FromResult(new OpenSessionReply { SessionId = id.Value });
        }

        public override Task<AuthReply> Authenticate(AuthRequest request, ServerCallContext context)
        {
            FrontEndConnection connection = Require(request.ConnectionId);
            SessionId id = Own(connection, request.SessionId);
            // The login waits for the game thread (a bounded time), so it runs on a pool thread and never holds a request thread.
            return Task.Run(() => ToReply(_registry.Core.Authenticate(id, request.Name ?? string.Empty, request.Password ?? string.Empty)));
        }

        public override Task<AuthReply> CreatePlayer(CreatePlayerRequest request, ServerCallContext context)
        {
            FrontEndConnection connection = Require(request.ConnectionId);
            SessionId id = Own(connection, request.SessionId);
            return Task.Run(() => ToReply(_registry.Core.CreatePlayer(id, request.Name ?? string.Empty, request.Password ?? string.Empty)));
        }

        public override Task<Ack> Line(LineRequest request, ServerCallContext context)
        {
            FrontEndConnection connection = Require(request.ConnectionId);
            SessionId id = Own(connection, request.SessionId);
            _registry.Core.SubmitLine(id, request.Line ?? string.Empty);
            return Task.FromResult(new Ack { Ok = true });
        }

        public override Task<Ack> CloseSession(CloseSessionRequest request, ServerCallContext context)
        {
            FrontEndConnection connection = Require(request.ConnectionId);
            SessionId id = Own(connection, request.SessionId);
            connection.Untrack(id.Value);
            _registry.Core.CloseSession(id, ParseReason(request.Reason));
            return Task.FromResult(new Ack { Ok = true });
        }

        public override Task<ThrottleReply> IsThrottled(ThrottleRequest request, ServerCallContext context)
        {
            Require(request.ConnectionId);
            return Task.FromResult(new ThrottleReply { Throttled = _registry.Core.IsThrottled(request.RemoteKey ?? string.Empty) });
        }

        public override Task<Ack> RecordLoginOutcome(LoginOutcomeRequest request, ServerCallContext context)
        {
            Require(request.ConnectionId);
            _registry.Core.RecordLoginOutcome(request.RemoteKey ?? string.Empty, request.Success);
            return Task.FromResult(new Ack { Ok = true });
        }

        public override Task<OnlineNamesReply> GetOnlineNames(OnlineNamesRequest request, ServerCallContext context)
        {
            Require(request.ConnectionId);
            return Task.Run(() =>
            {
                var reply = new OnlineNamesReply();
                reply.Names.AddRange(_registry.Core.GetOnlineNames());
                return reply;
            });
        }

        private FrontEndConnection Require(string connectionId)
        {
            FrontEndConnection connection = _registry.Find(connectionId);
            if (connection == null || connection.IsClosed)
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Unknown or closed front-end connection. Connect again."));
            }
            return connection;
        }

        private static SessionId Own(FrontEndConnection connection, string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId) || !connection.Owns(sessionId))
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied, "That session was not opened by this connection."));
            }
            return new SessionId(sessionId);
        }

        /// <summary>Maps the result of a login to the wire. The text is empty: core has already written any explanation to the session.</summary>
        public static AuthReply ToReply(LoginResult result)
        {
            return new AuthReply { Result = ToWire(result) };
        }

        public static AuthResult ToWire(LoginResult result)
        {
            switch (result)
            {
                case LoginResult.Success: return AuthResult.AuthOk;
                case LoginResult.BadCredentials: return AuthResult.AuthBadCredentials;
                case LoginResult.Retired: return AuthResult.AuthRetired;
                case LoginResult.Throttled: return AuthResult.AuthThrottled;
                case LoginResult.AlreadyOnline: return AuthResult.AuthAlreadyOnline;
                case LoginResult.NameTaken: return AuthResult.AuthNameTaken;
                default: return AuthResult.AuthTryAgainLater;
            }
        }

        /// <summary>The close reason a front end names: client_closed, idle or flood. Anything else counts as the client closing.</summary>
        public static SessionEndReason ParseReason(string reason)
        {
            switch ((reason ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "idle": return SessionEndReason.Idle;
                case "flood": return SessionEndReason.Flood;
                default: return SessionEndReason.ClientClosed;
            }
        }
    }
}
