namespace MUD.CoreApi
{
    /// <summary>The outcome of a login or character creation request.</summary>
    public enum LoginResult
    {
        Success,
        BadCredentials,
        /// <summary>The right password was given, but the character has been retired and cannot be played.</summary>
        Retired,
        /// <summary>Too many failed attempts from this remote key; try again later.</summary>
        Throttled,
        /// <summary>The account was already online and this session took it over.</summary>
        AlreadyOnline,
        NameTaken,
        TryAgainLater
    }
}
