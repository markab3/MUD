namespace MUD.Core.Commands
{
    public static class CommandExtensions
    {
        public const int MaxMessageLength = 500;

        public const string MessageShortenedNotice = "Your message was too long and has been shortened to 500 characters.";

        /// <summary>
        /// Truncates player speech to MaxMessageLength characters. Returns true if it was shortened.
        /// </summary>
        public static bool TruncateMessage(string message, out string result)
        {
            if (message != null && message.Length > MaxMessageLength)
            {
                result = message.Substring(0, MaxMessageLength);
                return true;
            }
            result = message;
            return false;
        }

        public static string StripKeyword(this ICommand command, string input)
        {
            string remainingInput = null;

            foreach (string currentKeyword in command.CommandKeywords)
            {
                if (input.StartsWith(currentKeyword + " ") || input == currentKeyword)
                {
                    remainingInput = input.Substring(currentKeyword.Length).Trim();
                    break;
                }
            }
            return remainingInput;
        }
    }
}