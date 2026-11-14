namespace MUD.CoreApi
{
    /// <summary>Why a session ended. Used both when a front end closes a session and when core ends one.</summary>
    public enum SessionEndReason
    {
        /// <summary>The remote side closed the connection.</summary>
        ClientClosed,
        /// <summary>The session was idle for too long.</summary>
        Idle,
        /// <summary>The session sent input too fast or in too large amounts.</summary>
        Flood,
        /// <summary>The player chose to quit.</summary>
        Quit,
        /// <summary>The player logged in again from another session, which took this one over.</summary>
        TakenOver,
        /// <summary>The world is stopping.</summary>
        WorldStopping
    }
}
