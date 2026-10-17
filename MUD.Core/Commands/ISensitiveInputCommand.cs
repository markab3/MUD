namespace MUD.Core.Commands
{
    /// <summary>
    /// A command whose typed arguments may be secret. The input line is passed through Redact before it is
    /// queued or kept anywhere, so the secret never reaches the command queue text or any log.
    /// </summary>
    public interface ISensitiveInputCommand
    {
        string Redact(string input);
    }
}
