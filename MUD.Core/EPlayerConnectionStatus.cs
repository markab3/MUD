
namespace MUD.Core
{
    public enum EPlayerConnectionStatus
    {
        /// <summary>
        /// The client has not been
        /// authenticated yet.
        /// </summary>
        Guest = 0,
        /// <summary>
        /// The client is authenticating.
        /// </summary>
        Authenticating = 1,
        /// <summary>
        /// The client is logged in.
        /// </summary>
        LoggedIn = 2,
        /// <summary>
        /// Player lost their connection.
        /// </summary>
        NetDead = 3
    }
}