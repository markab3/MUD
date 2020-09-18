using System.Collections.Generic;
using System.Text;

namespace MUD.Telnet
{

    public class TelnetCommand
    {
        public CommandCode? Command { get; private set; }
        public OptionCode? Option { get; private set; }

        public List<byte> NegotiationData { get; private set; }

        public TelnetCommand(CommandCode? commandCode = null, OptionCode? optionCode = null, List<byte> negotiationData = null)
        {
            Command = commandCode;
            Option = optionCode;
            NegotiationData = negotiationData;
            if (NegotiationData == null) { NegotiationData = new List<byte>(); }
        }

        public bool ProccessByte(byte nextByte)
        {
            // If I do not have a command code, this will be it.
            if (Command == null && nextByte >= 240 && nextByte <= 254)
            {
                Command = (CommandCode)nextByte;
                return true;
            }

            // If the command has no option and was one that gets an option, this is the option.
            if (Command.HasValue && (byte)Command.Value >= 250 && Option == null)
            {
                Option = (OptionCode)nextByte;
                return true;
            }

            // If the command was SB (Begin Negotiation) and the option is set, then this is negotiation data.
            // SB ends with IAC and SE, which would fall through here at the IAC and it would get another command going for SE.
            // Assumption now is that the 
            if (Command.HasValue && Command == CommandCode.SB && Option.HasValue && nextByte < 0xFF)
            {
                appendNegotiationData(nextByte);
                return true;
            }

            return false;
        }

        public byte[] ToByteArray()
        {
            List<byte> commandBytes = new List<byte>();
            commandBytes.Add((byte)CommandCode.IAC);
            if (Command != null)
            {
                commandBytes.Add((byte)Command.Value);
                if (Option != null)
                {
                    commandBytes.Add((byte)Option.Value);
                }
                if (Command == CommandCode.SB) {
                    commandBytes.AddRange(NegotiationData);
                    commandBytes.Add((byte)CommandCode.IAC);
                    commandBytes.Add((byte)CommandCode.SE);
                }
            }
            return commandBytes.ToArray();
        }

        public override string ToString()
        {
            return Command?.ToString() +" " + Option?.ToString() + " " + Encoding.ASCII.GetString(NegotiationData?.ToArray());
        }

        private void appendNegotiationData(byte data)
        {
            NegotiationData.Add(data);
        }
    }
}