using System;
using System.Collections.Generic;
using System.Threading.Channels;
using MUD.Contracts;
using MUD.CoreApi;
using Wire = MUD.Contracts;

namespace MUD.Core.Sessions
{
    /// <summary>
    /// One accepted front-end connection (created by a successful Connect). Owns the bounded queue that feeds the front end's
    /// event stream and the set of sessions the connection opened. The queue is how the sink rule of D42 is kept over the wire:
    /// <see cref="TryEnqueue"/> never blocks, so core's game thread is never held up by a slow front end. A front end that falls
    /// so far behind that the queue is full is cut off as if it had disconnected (its sessions are closed in core), because
    /// dropping single events would leave a player's screen out of step with the game.
    /// </summary>
    public sealed class FrontEndConnection
    {
        private readonly object _lock = new object();
        private readonly Channel<SessionEvent> _outbound;
        private readonly HashSet<string> _sessions = new HashSet<string>(StringComparer.Ordinal);
        private readonly Action<FrontEndConnection, string> _onOverflow;
        private volatile bool _closed;

        internal FrontEndConnection(string connectionId, string name, int outboundCapacity, Action<FrontEndConnection, string> onOverflow)
        {
            ConnectionId = connectionId;
            Name = name;
            _onOverflow = onOverflow;
            _outbound = Channel.CreateBounded<SessionEvent>(new BoundedChannelOptions(outboundCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        public string ConnectionId { get; private set; }

        /// <summary>The front end's name from its Connect call, for example "telnet".</summary>
        public string Name { get; private set; }

        /// <summary>True once the Events stream has been opened. One stream per connection.</summary>
        public bool IsSubscribed { get; internal set; }

        public bool IsClosed { get { return _closed; } }

        public ChannelReader<SessionEvent> Reader { get { return _outbound.Reader; } }

        /// <summary>Events waiting for the stream.</summary>
        public int PendingEvents { get { return _outbound.Reader.Count; } }

        /// <summary>
        /// Queues an event without blocking. Returns false when the connection is closed or the queue is full; a full queue also
        /// reports the overflow once, which closes the connection on another thread.
        /// </summary>
        public bool TryEnqueue(SessionEvent sessionEvent)
        {
            if (_closed) { return false; }
            if (_outbound.Writer.TryWrite(sessionEvent)) { return true; }
            _onOverflow(this, "the front end's event queue is full");
            return false;
        }

        internal void Track(string sessionId)
        {
            lock (_lock) { _sessions.Add(sessionId); }
        }

        internal bool Untrack(string sessionId)
        {
            lock (_lock) { return _sessions.Remove(sessionId); }
        }

        /// <summary>True when this connection opened the session and has not closed it.</summary>
        public bool Owns(string sessionId)
        {
            lock (_lock) { return _sessions.Contains(sessionId); }
        }

        public int SessionCount
        {
            get { lock (_lock) { return _sessions.Count; } }
        }

        internal string[] TakeSessions()
        {
            lock (_lock)
            {
                var all = new string[_sessions.Count];
                _sessions.CopyTo(all);
                _sessions.Clear();
                return all;
            }
        }

        /// <summary>Stops accepting events. The stream still delivers what is already queued, then ends.</summary>
        internal void Complete()
        {
            _closed = true;
            _outbound.Writer.TryComplete();
        }
    }

    /// <summary>
    /// The <see cref="IFrontEndSink"/> that core gives to its own session registry for a session opened over frontends.sock. It turns each
    /// call into a <see cref="SessionEvent"/> on the connection's queue and returns at once (D42).
    /// </summary>
    public sealed class RemoteFrontEndSink : IFrontEndSink
    {
        private readonly FrontEndConnection _connection;

        public RemoteFrontEndSink(FrontEndConnection connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        public void Output(SessionId id, OutputChannel channel, string markupText, bool flush)
        {
            _connection.TryEnqueue(new SessionEvent
            {
                SessionId = id.Value,
                Output = new Wire.Output { Channel = ChannelName(channel), Text = markupText ?? string.Empty, Flush = flush }
            });
        }

        public void SetEchoHidden(SessionId id, bool hidden)
        {
            _connection.TryEnqueue(new SessionEvent { SessionId = id.Value, SetEcho = new SetEcho { Hidden = hidden } });
        }

        public void Prompt(SessionId id, string text)
        {
            _connection.TryEnqueue(new SessionEvent { SessionId = id.Value, Prompt = new Wire.Prompt { Text = text ?? string.Empty } });
        }

        public void Close(SessionId id, string farewellMarkup, bool flush)
        {
            _connection.TryEnqueue(new SessionEvent { SessionId = id.Value, Close = new CloseFromCore { Farewell = farewellMarkup ?? string.Empty, Flush = flush } });
        }

        public void SetTerminal(SessionId id, string terminalName)
        {
            _connection.TryEnqueue(new SessionEvent { SessionId = id.Value, SetTerminal = new Wire.SetTerminal { Name = terminalName ?? string.Empty } });
        }

        public void SessionEnded(SessionId id, SessionEndReason reason)
        {
            _connection.TryEnqueue(new SessionEvent { SessionId = id.Value, Ended = new Wire.SessionEnded { Reason = ReasonName(reason) } });
        }

        /// <summary>The channel tag on the wire (D44): say, tell, room, combat, system, chat.</summary>
        public static string ChannelName(OutputChannel channel)
        {
            switch (channel)
            {
                case OutputChannel.Say: return "say";
                case OutputChannel.Tell: return "tell";
                case OutputChannel.Room: return "room";
                case OutputChannel.Combat: return "combat";
                case OutputChannel.Chat: return "chat";
                default: return "system";
            }
        }

        /// <summary>The reason text on the wire. Each reason has a fixed lower-case name.</summary>
        public static string ReasonName(SessionEndReason reason)
        {
            switch (reason)
            {
                case SessionEndReason.ClientClosed: return "client_closed";
                case SessionEndReason.Idle: return "idle";
                case SessionEndReason.Flood: return "flood";
                case SessionEndReason.Quit: return "quit";
                case SessionEndReason.TakenOver: return "taken_over";
                case SessionEndReason.WorldStopping: return "shutdown";
                default: return "unknown";
            }
        }
    }
}
