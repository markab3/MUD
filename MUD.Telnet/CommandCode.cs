
namespace MUD.Telnet
{

    public enum CommandCode : byte
    {
        SE = 240, // Subnegotiation End
        NOP = 241, // No Operation
        DM = 242, // Data Mark
        BRK = 243, // Break
        IP = 244, // Interrupt
        AO = 245, // Abort Output
        AYT = 246, // Are You there?
        EC = 247, // Erase Character
        EL = 248, // Erase Line
        GA = 249, // Go Ahead
        SB = 250, // Subnegotiation Begins
        WILL = 251,
        WONT = 252,
        DO = 253,
        DONT = 254
    }
}