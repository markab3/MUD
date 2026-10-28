using MUD.CoreApi;
using MUD.Telnet;

namespace MUD.Main
{
    /// <summary>
    /// Lets the existing telnet code keep working while later cards cut it over to the core API: it is the
    /// <see cref="IFrontEndSink"/> for one session and forwards each event to that session's
    /// <see cref="IClientConnection"/>. The output channel is dropped, because the telnet connection has no use for it yet.
    ///
    /// Never blocks: the connection's Send, SendPrompt and SetEchoHidden only queue bytes for its single writer, and a
    /// close with flush is finished on another thread.
    /// </summary>
    public sealed class LegacyConnectionSink : IFrontEndSink
    {
        public LegacyConnectionSink(IClientConnection connection)
        {
            Connection = connection;
        }

        public IClientConnection Connection { get; private set; }

        public void Output(SessionId id, OutputChannel channel, string markupText, bool flush)
        {
            Connection.Send(markupText);
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
            if (string.IsNullOrEmpty(farewellMarkup))
            {
                Connection.Disconnect(flush);
            }
            else
            {
                Connection.DisconnectWithMessage(farewellMarkup);
            }
        }

        public void SessionEnded(SessionId id, SessionEndReason reason)
        {
            // The connection was closed by Close; nothing more to tell a telnet client.
        }
    }
}
