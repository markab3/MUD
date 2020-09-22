using System;

namespace MUD.Core
{
    public static class StringExtensions
    {
        // Derp this isnt a string extension. Its a char extension.
        public static bool IsVowel(this char chr) {
            return "aeiouAEIOU".IndexOf(chr) >= 0;
        }

        public static string GetArticle(this string str)
        {
            if (str[str.Length-1].IsVowel())
            {
                return "an";
            }
            return "a";
        }

        public static string GetPlural(this string str)
        {
            if (str.EndsWith('s') || str.EndsWith("ss") || str.EndsWith("sh") || str.EndsWith("ch") || str.EndsWith('x') || str.EndsWith('z'))
            {
                return str + "es";
            }
            if (str.EndsWith('y') && !str[str.Length-2].IsVowel()) {
                return str.Substring(0, str.Length-1) + "ies";
            }
            return str + "s";
        }
    }
}