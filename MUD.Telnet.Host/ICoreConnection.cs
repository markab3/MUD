using System;
using MUD.CoreApi;

namespace MUD.Telnet.Host
{
    /// <summary>
    /// One connection to core: an <see cref="ICoreApi"/> that must be connected first and that reports when core is gone. It exists so the
    /// reconnect logic can be tested without sockets; <see cref="GrpcCoreConnection"/> is the real one.
    /// </summary>
    public interface ICoreConnection : ICoreApi, IDisposable
    {
        /// <summary>Connects. Returns null on success or the reason it failed (no secret in it).</summary>
        string Connect();

        /// <summary>Raised once when core is gone; every open session has been ended by then.</summary>
        event Action<string> ConnectionLost;
    }
}
