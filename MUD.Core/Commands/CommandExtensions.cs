namespace MUD.Core.Commands
{
    public static class CommandExtensions
    {
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