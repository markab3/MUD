using System;
using System.Collections.Generic;
using System.Linq;
using MUD.Core.Formatting;
using MUD.CoreApi;

namespace MUD.Core.Sessions
{
    /// <summary>
    /// A front-end sink that turns the %^Color%^ markup of each output into the text of the terminal the player chose,
    /// then passes everything on to the real sink. This is the conversion that used to be done in Player.ReceiveMessage;
    /// it sits here, in front of the telnet sink, until the conversion moves into the telnet project. Only Output is
    /// converted (prompts and farewells were never converted). Never blocks: it adds only string work to the inner call.
    /// </summary>
    public sealed class TerminalConvertingSink : IFrontEndSink
    {
        private readonly IFrontEndSink _inner;
        private readonly IEnumerable<ITerminalHandler> _terminalHandlers;
        private readonly Func<SessionId, string> _selectedTerminal;

        public TerminalConvertingSink(IFrontEndSink inner, IEnumerable<ITerminalHandler> terminalHandlers, Func<SessionId, string> selectedTerminal)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _terminalHandlers = terminalHandlers ?? Enumerable.Empty<ITerminalHandler>();
            _selectedTerminal = selectedTerminal ?? (id => null);
        }

        /// <summary>The sink that receives the converted output.</summary>
        public IFrontEndSink Inner { get { return _inner; } }

        public void Output(SessionId id, OutputChannel channel, string markupText, bool flush)
        {
            string selected = _selectedTerminal(id);
            var handler = _terminalHandlers.FirstOrDefault(th => th.TerminalName.ToLower() == selected?.ToLower() || (th.Aliases != null && th.Aliases.Any(a => a.ToLower() == selected?.ToLower())));
            if (handler != null) { markupText = handler.ResolveOutput(markupText); }
            _inner.Output(id, channel, markupText, flush);
        }

        public void SetEchoHidden(SessionId id, bool hidden) { _inner.SetEchoHidden(id, hidden); }

        public void Prompt(SessionId id, string text) { _inner.Prompt(id, text); }

        public void Close(SessionId id, string farewellMarkup, bool flush) { _inner.Close(id, farewellMarkup, flush); }

        public void SessionEnded(SessionId id, SessionEndReason reason) { _inner.SessionEnded(id, reason); }
    }
}
