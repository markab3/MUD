namespace MUD.Telnet
{
    public enum OptionCode : byte
    {
        Echo = 1,
        SuppressGoAhead = 3,
        Status = 5,
        TimingMark = 6,
        TerminalType = 24,
        WindowSize = 31,
        TerminalSpeed = 32,
        RemoteFlowControl = 33,
        LineMode = 34,
        EnvironmentVariables = 36
    }
}