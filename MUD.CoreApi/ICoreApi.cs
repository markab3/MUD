namespace MUD.CoreApi
{
    /// <summary>
    /// What a front end calls on core (front end to core). Every call is made on the caller's thread and only
    /// enqueues work for the game thread; none of them runs game logic inline.
    /// </summary>
    public interface ICoreApi
    {
        /// <summary>
        /// Opens a session and returns its id, which core generates. <paramref name="frontEnd"/> names the front end
        /// (for example "telnet"); <paramref name="remoteKey"/> is the opaque address-like key the login throttle uses;
        /// <paramref name="sink"/> receives core's events for this session.
        /// </summary>
        SessionId OpenSession(string frontEnd, string remoteKey, IFrontEndSink sink);

        /// <summary>
        /// Submits one raw line typed in the session. Enqueues only. A session more than a fixed number of lines
        /// behind has further lines dropped, and is closed if it keeps doing that. Unknown ids are ignored.
        /// </summary>
        void SubmitLine(SessionId id, string rawLine);

        /// <summary>Closes the session from the front end's side (connection lost, idle, ...). Unknown ids are ignored.</summary>
        void CloseSession(SessionId id, SessionEndReason reason);
    }
}
