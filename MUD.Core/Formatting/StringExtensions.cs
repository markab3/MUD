using System;

namespace MUD.Core.Formatting
{
    public static class StringExtensions
    {
        // Derp this isnt a string extension. Its a char extension.
        public static bool IsVowel(this char chr)
        {
            return "aeiouAEIOU".IndexOf(chr) >= 0;
        }

        // Derp this isnt a string extension either...
        public static string GetListText(this string[] strArr)
        {
            if (strArr == null) { return string.Empty; }
            if (strArr.Length == 1) { return strArr[0]; }
            return string.Join(", ", strArr, 0, strArr.Length - 1) + " and " + strArr[strArr.Length - 1];
        }

        // Ok so these just aren't really string extensions. They're just extensions.
        public static string GetNumberText(this int number) {
            if (number == 1) {return "one";}
            else if (number == 2) {return "two";}
            else if (number == 3) {return "three";}
            else if (number == 4) {return "four";}
            else if (number >= 5 && number < 12) {return "several";}
            else if (number >= 12 && number < 36) {return "dozens of";}
            else if (number >= 36) {return "many";}
            return number.ToString(); // default..
        }

        public static string GetArticle(this string str)
        {
            if (str[str.Length - 1].IsVowel())
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
            if (str.EndsWith('y') && !str[str.Length - 2].IsVowel())
            {
                return str.Substring(0, str.Length - 1) + "ies";
            }
            return str + "s";
        }
    }
}