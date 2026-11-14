namespace MUD.Contracts
{
    /// <summary>
    /// The wire contract version (D24). Every module and front end sends this number in its first call and core
    /// refuses any difference (strict equality). Increase it by one with every change to the .proto files.
    /// </summary>
    public static class ContractVersion
    {
        public const uint Current = 1;
    }
}
