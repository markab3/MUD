using MUD.Core;
using MUD.Telnet;

namespace MUD.Main
{
    public enum EPlayerSessionStatus
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
        LoggedIn = 2
    }
    public class PlayerSession
    {
        public Client TelnetClient { get; set; }

        public Player SessionPlayer { get; set; }

        public EPlayerSessionStatus SessionStatus { get; set; }

        public PlayerSession(Client telnetClient)
        {
            TelnetClient = telnetClient;
            SessionStatus = EPlayerSessionStatus.Guest;
        }
    }
}