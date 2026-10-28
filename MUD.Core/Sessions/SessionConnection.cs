using System;
using System.Threading;
using MUD.CoreApi;
using MUD.Core.GameObjects;
using MUD.Telnet;

namespace MUD.Core.Sessions
{
    /// <summary>
    /// A connection that can carry the output channel of each message. Player.ReceiveMessage uses it when the
    /// connection supports it and falls back to Send (system channel) otherwise.
    /// </summary>
    public interface ITaggedConnection
    {
        void Send(string text, OutputChannel channel);
    }

    /// <summary>
    /// Core's end of one open session: the existing <see cref="IClientConnection"/> contract, implemented on top of the
    /// front end's <see cref="IFrontEndSink"/>. Because it is an IClientConnection, the game thread's existing
    /// bookkeeping (inbound accounting, the player map, logout) works on it unchanged.
    ///
    /// Every call here is forwarded to the sink and returns at once; the sink must never block (decision D42).
    /// After the session has ended, further output is dropped, as writing to a closed connection is today.
    /// </summary>
    public sealed class SessionConnection : IClientConnection, ITaggedConnection
    {
        private readonly Func<bool> _worldIsStopping;
        private int _ended;
        private volatile bool _takenOver;

        public SessionConnection(SessionId id, string frontEnd, string remoteKey, IFrontEndSink sink, Func<bool> worldIsStopping)
        {
            Id = id;
            FrontEnd = frontEnd ?? string.Empty;
            RemoteKey = remoteKey ?? string.Empty;
            Sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _worldIsStopping = worldIsStopping ?? (() => false);
        }

        public SessionId Id { get; private set; }

        public string FrontEnd { get; private set; }

        /// <summary>The address-like key the login throttle uses.</summary>
        public string RemoteKey { get; private set; }

        public IFrontEndSink Sink { get; private set; }

        /// <summary>The player logged in on this session, or null before login. Written on the game thread only.</summary>
        public volatile Player Player;

        public bool HasEnded { get { return Volatile.Read(ref _ended) != 0; } }

        public void Send(string text)
        {
            Send(text, OutputChannel.System);
        }

        public void Send(string text, OutputChannel channel)
        {
            if (HasEnded) { return; }
            Sink.Output(Id, channel, text, false);
        }

        public void SendPrompt(string text)
        {
            if (HasEnded) { return; }
            Sink.Prompt(Id, text);
        }

        public void SetEchoHidden(bool hidden)
        {
            if (HasEnded) { return; }
            Sink.SetEchoHidden(Id, hidden);
        }

        public void DisconnectWithMessage(string message)
        {
            Terminate(message, DefaultReason(), true);
        }

        public void Disconnect(bool flush)
        {
            Terminate(string.Empty, DefaultReason(), flush);
        }

        /// <summary>
        /// Ends the session from core's side: asks the sink to close (after the farewell text) and tells it why.
        /// Happens once; later calls do nothing.
        /// </summary>
        public void Terminate(string farewellMarkup, SessionEndReason reason, bool flush)
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0) { return; }
            Sink.Close(Id, farewellMarkup ?? string.Empty, flush);
            Sink.SessionEnded(Id, reason);
        }

        /// <summary>The front end has already closed the session: core must not call the sink about it any more.</summary>
        public void Detach()
        {
            Interlocked.Exchange(ref _ended, 1);
        }

        /// <summary>Called when a second login takes the player over: the close that follows is reported as TakenOver.</summary>
        public void MarkTakenOver()
        {
            _takenOver = true;
        }

        private SessionEndReason DefaultReason()
        {
            if (_takenOver) { return SessionEndReason.TakenOver; }
            return _worldIsStopping() ? SessionEndReason.WorldStopping : SessionEndReason.Quit;
        }
    }
}
