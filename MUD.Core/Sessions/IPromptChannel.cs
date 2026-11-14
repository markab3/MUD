using System;
using MUD.CoreApi;

namespace MUD.Core.Sessions
{
    /// <summary>
    /// What prompted input (the password change dialogue) needs from a player's session: the throttle key, plain
    /// output, a prompt without a line ending, and echo control. Backed by the session's front-end sink.
    /// </summary>
    public interface IPromptChannel
    {
        /// <summary>The address-like key the login throttle uses (empty when unknown).</summary>
        string RemoteKey { get; }

        /// <summary>Sends a line of text (a line ending is added by the front end).</summary>
        void Send(string text);

        /// <summary>Sends text without a line ending, for prompts.</summary>
        void SendPrompt(string text);

        /// <summary>Hides or restores the echo of what the user types.</summary>
        void SetEchoHidden(bool hidden);
    }

    /// <summary>An <see cref="IPromptChannel"/> that forwards to one session's <see cref="IFrontEndSink"/>.</summary>
    public sealed class SinkPromptChannel : IPromptChannel
    {
        private readonly SessionId _sessionId;
        private readonly IFrontEndSink _sink;

        public SinkPromptChannel(SessionId sessionId, IFrontEndSink sink, string remoteKey)
        {
            _sessionId = sessionId;
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            RemoteKey = remoteKey ?? string.Empty;
        }

        public string RemoteKey { get; private set; }

        public void Send(string text)
        {
            _sink.Output(_sessionId, OutputChannel.System, text, false);
        }

        public void SendPrompt(string text)
        {
            _sink.Prompt(_sessionId, text);
        }

        public void SetEchoHidden(bool hidden)
        {
            _sink.SetEchoHidden(_sessionId, hidden);
        }
    }
}
