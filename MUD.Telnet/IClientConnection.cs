namespace MUD.Telnet
{
    /// <summary>
    /// What the game needs from a client connection. Implemented by <see cref="Client"/>; tests supply a fake.
    /// Send never blocks: it queues text for the connection's single writer.
    /// </summary>
    public interface IClientConnection : ILoginConnection
    {
        /// <summary>
        /// Closes the connection. With flush, queued output (e.g. "Thanks for playing!") is written first,
        /// bounded by a short timeout; the close then happens asynchronously.
        /// </summary>
        void Disconnect(bool flush);
    }
}
