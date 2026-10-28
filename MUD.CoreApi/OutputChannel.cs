namespace MUD.CoreApi
{
    /// <summary>
    /// The kind of a piece of output, so a front end can route or style it (decisions D28 and D44). The text itself is
    /// the existing %^Color%^ markup. Output that is not tagged is <see cref="System"/>.
    /// </summary>
    public enum OutputChannel
    {
        /// <summary>What a player says to the room.</summary>
        Say,
        /// <summary>A private message between two players.</summary>
        Tell,
        /// <summary>Room descriptions and events seen by everyone in a room.</summary>
        Room,
        /// <summary>Fighting.</summary>
        Combat,
        /// <summary>Everything else; the default for untagged output.</summary>
        System,
        /// <summary>The server-wide chat channel.</summary>
        Chat
    }
}
