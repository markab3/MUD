using System.Collections.Generic;

namespace MUD.Telnet
{
    /// <summary>
    /// Builds polite refusals for unsupported telnet options (RFC 854/855): WONT in answer to DO, DONT in
    /// answer to WILL. Each option is refused at most once per direction, so a peer that repeats its request
    /// cannot get us into a negotiation loop. WONT and DONT are never answered, so they have no method here.
    /// </summary>
    public class OptionRefusals
    {
        private readonly HashSet<byte> _refusedDo = new HashSet<byte>();
        private readonly HashSet<byte> _refusedWill = new HashSet<byte>();

        /// <summary>IAC WONT option, or null if this DO was already refused.</summary>
        public byte[] RefuseDo(byte option)
        {
            return _refusedDo.Add(option) ? new byte[] { (byte)CommandCode.IAC, (byte)CommandCode.WONT, option } : null;
        }

        /// <summary>IAC DONT option, or null if this WILL was already refused.</summary>
        public byte[] RefuseWill(byte option)
        {
            return _refusedWill.Add(option) ? new byte[] { (byte)CommandCode.IAC, (byte)CommandCode.DONT, option } : null;
        }
    }
}
