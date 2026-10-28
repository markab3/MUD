namespace MUD.CoreApi
{
    /// <summary>
    /// Where core sends events for one front end's sessions (core to front end).
    ///
    /// EVERY method MUST NEVER BLOCK (decision D42). Core calls them on the game thread, so an implementation only
    /// records or enqueues the event for its own writer and returns at once. Core keeps no second queue in front of the
    /// sink, so a sink that blocks stalls the whole game.
    /// </summary>
    public interface IFrontEndSink
    {
        /// <summary>Output for a session: text in %^Color%^ markup, tagged with a channel. Flush asks for it to be written before any later close.</summary>
        void Output(SessionId id, OutputChannel channel, string markupText, bool flush);

        /// <summary>Hides or restores the echo of what the user types (password prompts).</summary>
        void SetEchoHidden(SessionId id, bool hidden);

        /// <summary>A prompt: text without a line ending.</summary>
        void Prompt(SessionId id, string text);

        /// <summary>Core asks the front end to close the session after writing the farewell text (which may be empty).</summary>
        void Close(SessionId id, string farewellMarkup, bool flush);

        /// <summary>The session is over, with the reason.</summary>
        void SessionEnded(SessionId id, SessionEndReason reason);
    }
}
