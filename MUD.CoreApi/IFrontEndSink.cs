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

        /// <summary>
        /// The terminal the player chose for the session (the persisted setting, pushed at login and whenever the player
        /// changes it). The front end converts the markup of later output for that terminal; a name it does not know
        /// leaves the markup as it is. A front end with no use for it keeps this default, which does nothing.
        /// </summary>
        void SetTerminal(SessionId id, string terminalName) { }

        /// <summary>The session is over, with the reason.</summary>
        void SessionEnded(SessionId id, SessionEndReason reason);
    }
}
