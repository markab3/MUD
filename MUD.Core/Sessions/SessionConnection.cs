using System;
using System.Threading;
using MUD.CoreApi;
using MUD.Core.GameObjects;
using MUD.Telnet;

namespace MUD.Core.Sessions
{
    /// <summary>
    /// Core's end of one open session. It is the <see cref="IFrontEndSink"/> that a <see cref="Player"/> holds: every
    /// event is forwarded to the front end's own sink and returns at once, because the sink must never block
    /// (decision D42). Once the session has ended (core closed it, or the front end reported it closed), further events
    /// are dropped, as writing to a closed connection is today.
    ///
    /// It is also still an <see cref="IClientConnection"/>, only so that the telnet login dialogue (which is moved to the
    /// core API by a later card) keeps working against it. The player does not use that contract.
    /// </summary>
    public sealed class SessionConnection : IClientConnection, IFrontEndSink
    {
        private const int Open = 0;
        private const int ClosedByCore = 1;
        private const int Detached = 2;

        private readonly Func<bool> _worldIsStopping;
        private int _state;
        private int _endReported;

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

        public bool HasEnded { get { return Volatile.Read(ref _state) != Open; } }

        // IFrontEndSink: what the player calls. The session id the caller passes is this session's own id.

        public void Output(SessionId id, OutputChannel channel, string markupText, bool flush)
        {
            if (HasEnded) { return; }
            Sink.Output(Id, channel, markupText, flush);
        }

        public void SetEchoHidden(SessionId id, bool hidden)
        {
            if (HasEnded) { return; }
            Sink.SetEchoHidden(Id, hidden);
        }

        public void Prompt(SessionId id, string text)
        {
            if (HasEnded) { return; }
            Sink.Prompt(Id, text);
        }

        /// <summary>Asks the front end to close the session. Happens once; later calls, and calls after a detach, do nothing.</summary>
        public void Close(SessionId id, string farewellMarkup, bool flush)
        {
            if (Interlocked.CompareExchange(ref _state, ClosedByCore, Open) != Open) { return; }
            Sink.Close(Id, farewellMarkup ?? string.Empty, flush);
        }

        /// <summary>Tells the front end why the session ended. Only after core closed it, and only once.</summary>
        public void SessionEnded(SessionId id, SessionEndReason reason)
        {
            if (Volatile.Read(ref _state) != ClosedByCore) { return; }
            if (Interlocked.Exchange(ref _endReported, 1) != 0) { return; }
            Sink.SessionEnded(Id, reason);
        }

        // IClientConnection: the telnet login dialogue.

        public void Send(string text)
        {
            Output(Id, OutputChannel.System, text, false);
        }

        public void SendPrompt(string text)
        {
            Prompt(Id, text);
        }

        public void SetEchoHidden(bool hidden)
        {
            SetEchoHidden(Id, hidden);
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
            if (HasEnded) { return; }
            Close(Id, farewellMarkup, flush);
            SessionEnded(Id, reason);
        }

        /// <summary>The front end has already closed the session: core must not call the sink about it any more.</summary>
        public void Detach()
        {
            Interlocked.Exchange(ref _state, Detached);
        }

        private SessionEndReason DefaultReason()
        {
            return _worldIsStopping() ? SessionEndReason.WorldStopping : SessionEndReason.Quit;
        }
    }
}
