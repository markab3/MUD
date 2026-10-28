using System.Collections.Generic;

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

        /// <summary>
        /// Logs the session in as an existing character. Core runs the login on the game thread and the caller waits
        /// for the result, for a bounded time; it is therefore never called from the game thread's own callbacks by a
        /// front end. When the result is not a success core may already have sent an explanation to the session's sink
        /// (retired character, previous session still saving); the front end adds only its own generic text. A wait
        /// that runs out returns <see cref="LoginResult.TryAgainLater"/> and the login is not carried out.
        /// <see cref="LoginResult.AlreadyOnline"/> means the account was online and this session took it over: the
        /// session is logged in. Does not consult the throttle; the front end does that with <see cref="IsThrottled"/>
        /// and <see cref="RecordLoginOutcome"/>.
        /// </summary>
        LoginResult Authenticate(SessionId id, string name, string password);

        /// <summary>
        /// Creates a character and logs the session in as it. Waits the same way as <see cref="Authenticate"/>. A
        /// <see cref="LoginResult.NameTaken"/> result carries no text from core; the front end words it. Other failures
        /// (a name that is not allowed, a character that could not be saved) have already been explained to the sink.
        /// </summary>
        LoginResult CreatePlayer(SessionId id, string name, string password);

        /// <summary>True while too many logins have failed from this key (an address-like string); the front end refuses further attempts.</summary>
        bool IsThrottled(string remoteKey);

        /// <summary>Counts a failed login against the key, or clears its failure count after a success.</summary>
        void RecordLoginOutcome(string remoteKey, bool success);

        /// <summary>The names of the players who are online, for the pre-login "who". Read only; waits like <see cref="Authenticate"/>.</summary>
        IReadOnlyList<string> GetOnlineNames();
    }
}
