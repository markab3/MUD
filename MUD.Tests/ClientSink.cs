using MUD.CoreApi;
using MUD.Telnet;

namespace MUD.Tests
{
    /// <summary>
    /// A front-end sink that writes to a real telnet <see cref="Client"/>, for tests that read what arrives on the socket.
    /// Output is not converted (the terminal conversion is not part of the sink contract). Like the real front end it
    /// only queues bytes for the client's writer, so it never blocks.
    /// </summary>
    public class ClientSink : IFrontEndSink
    {
        private readonly Client _client;

        public ClientSink(Client client) { _client = client; }

        public void Output(SessionId id, OutputChannel channel, string markupText, bool flush) { _client.Send(markupText); }

        public void SetEchoHidden(SessionId id, bool hidden) { _client.SetEchoHidden(hidden); }

        public void Prompt(SessionId id, string text) { _client.SendPrompt(text); }

        public void Close(SessionId id, string farewellMarkup, bool flush)
        {
            if (string.IsNullOrEmpty(farewellMarkup)) { _client.Disconnect(flush); }
            else { _client.DisconnectWithMessage(farewellMarkup); }
        }

        public void SessionEnded(SessionId id, SessionEndReason reason) { }
    }
}
