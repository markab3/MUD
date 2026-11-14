using System;
using System.Collections.Generic;
using System.Linq;
using MUD.CoreApi;

namespace MUD.Telnet
{
    /// <summary>
    /// The <see cref="IFrontEndSink"/> for one telnet session. Core sends markup; this converts the %^Token%^ markup of
    /// each output to the text of the terminal the player chose (pushed by core through <see cref="SetTerminal"/>) and
    /// forwards it to the session's <see cref="ILoginConnection"/>. Only Output is converted: prompts and farewells
    /// never were. A terminal name no handler knows leaves the markup as it is.
    ///
    /// Closing is two steps, as core reports it: Close writes the farewell, and SessionEnded (which core always sends
    /// after Close) says why and then closes the connection. When the world is stopping, the "world is restarting"
    /// message (decision D29) is written first, so it is not lost behind the close.
    ///
    /// Never blocks: the connection's Send, SendPrompt and SetEchoHidden only queue bytes for its single writer, and a
    /// close with flush is finished on another thread. The conversion adds only string work.
    /// </summary>
    public sealed class TelnetSessionSink : IFrontEndSink
    {
        private static readonly ITerminalHandler[] DefaultHandlers = new ITerminalHandler[]
        {
            new AnsiTerminalHandler(), new GMudTerminalHandler(), new DefaultTerminalHandler()
        };

        private readonly IReadOnlyList<ITerminalHandler> _handlers;
        private volatile ITerminalHandler _handler;
        private volatile bool _closeRequested;
        private volatile bool _closeFlush;
        private int _ended;

        /// <summary>The text written to a telnet client when the world is stopping (decision D29).</summary>
        public const string WorldRestartingMessage = "The world is restarting. Please reconnect in a moment.";

        public TelnetSessionSink(ILoginConnection connection) : this(connection, null)
        {
        }

        public TelnetSessionSink(ILoginConnection connection, IEnumerable<ITerminalHandler> handlers)
        {
            Connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _handlers = (handlers ?? DefaultHandlers).ToList();
        }

        public ILoginConnection Connection { get; private set; }

        /// <summary>The handler of the terminal currently chosen for the session; null when none matches (markup is sent unconverted).</summary>
        public ITerminalHandler CurrentHandler { get { return _handler; } }

        public void SetTerminal(SessionId id, string terminalName)
        {
            _handler = Find(terminalName);
        }

        public void Output(SessionId id, OutputChannel channel, string markupText, bool flush)
        {
            ITerminalHandler handler = _handler;
            Connection.Send(handler == null ? markupText : handler.ResolveOutput(markupText));
        }

        public void SetEchoHidden(SessionId id, bool hidden)
        {
            Connection.SetEchoHidden(hidden);
        }

        public void Prompt(SessionId id, string text)
        {
            Connection.SendPrompt(text);
        }

        public void Close(SessionId id, string farewellMarkup, bool flush)
        {
            _closeFlush = flush || !string.IsNullOrEmpty(farewellMarkup);
            _closeRequested = true;
            if (!string.IsNullOrEmpty(farewellMarkup)) { Connection.Send(farewellMarkup); }
        }

        public void SessionEnded(SessionId id, SessionEndReason reason)
        {
            // Once only. A session ended without a Close (core never does this) still closes the connection.
            if (System.Threading.Interlocked.Exchange(ref _ended, 1) != 0) { return; }
            if (reason == SessionEndReason.WorldStopping) { Connection.Send(WorldRestartingMessage); }
            Connection.Disconnect(_closeRequested ? _closeFlush : true);
        }

        private ITerminalHandler Find(string terminalName)
        {
            if (string.IsNullOrEmpty(terminalName)) { return null; }
            return _handlers.FirstOrDefault(handler =>
                string.Equals(handler.TerminalName, terminalName, StringComparison.OrdinalIgnoreCase)
                || (handler.Aliases != null && handler.Aliases.Any(alias => string.Equals(alias, terminalName, StringComparison.OrdinalIgnoreCase))));
        }
    }
}
