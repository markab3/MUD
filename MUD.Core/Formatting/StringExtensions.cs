using System;
using System.Collections.Generic;
using System.Linq;

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
        
        public static string GetListText(this IEnumerable<string> strIEnum)
        {
            if (strIEnum == null) { return string.Empty; }
            var strArr = strIEnum.ToArray();
            if (strArr.Length == 1) { return strArr[0]; }
            return string.Join(", ", strArr, 0, strArr.Length - 1) + " and " + strArr[strArr.Length - 1];
        }

        // Ok so these just aren't really string extensions. They're just extensions.
        public static string GetNumberText(this int number)
        {
            if (number == 1) { return "one"; }
            else if (number == 2) { return "two"; }
            else if (number == 3) { return "three"; }
            else if (number == 4) { return "four"; }
            else if (number >= 5 && number < 12) { return "several"; }
            else if (number >= 12 && number < 36) { return "dozens of"; }
            else if (number >= 36) { return "many"; }
            return number.ToString(); // default..
        }

        public static string GetExactNumberText(this int number)
        {
            if (number == 0) { return "zero"; }
            if (number == 1) { return "one"; }
            if (number == 2) { return "two"; }
            if (number == 3) { return "three"; }
            if (number == 4) { return "four"; }
            if (number == 5) { return "five"; }
            if (number == 6) { return "six"; }
            if (number == 7) { return "seven"; }
            if (number == 8) { return "eight"; }
            if (number == 9) { return "nine"; }
            if (number == 10) { return "ten"; }
            if (number == 11) { return "eleven"; }
            if (number == 12) { return "twelve"; }
            if (number == 13) { return "thirteen"; }
            if (number == 14) { return "fourteen"; }
            if (number == 15) { return "fifteen"; }
            if (number == 16) { return "sixteen"; }
            if (number == 17) { return "seventeen"; }
            if (number == 18) { return "eighteen"; }
            if (number == 19) { return "nineteen"; }
            if (number == 20) { return "twenty"; }
            if (number == 30) { return "thirty"; }
            if (number == 40) { return "forty"; }
            if (number == 50) { return "fifty"; }
            if (number == 60) { return "sixty"; }
            if (number == 70) { return "seventy"; }
            if (number == 80) { return "eighty"; }
            if (number == 90) { return "ninety"; }
            if (number >= 21 && number <= 29) { return "twenty-" + GetExactNumberText(number - 20); }
            if (number >= 31 && number <= 39) { return "thirty-" + GetExactNumberText(number - 30); }
            if (number >= 41 && number <= 49) { return "fourty-" + GetExactNumberText(number - 40); }
            if (number >= 51 && number <= 59) { return "fifty-" + GetExactNumberText(number - 50); }
            if (number >= 61 && number <= 69) { return "sixty-" + GetExactNumberText(number - 60); }
            if (number >= 71 && number <= 79) { return "seventy-" + GetExactNumberText(number - 70); }
            if (number >= 81 && number <= 89) { return "eighty-" + GetExactNumberText(number - 80); }
            if (number >= 91 && number <= 99) { return "ninety-" + GetExactNumberText(number - 90); }
            if (number >= 21 && number <= 29) { return "twenty-" + GetExactNumberText(number - 20); }
            if (number >= 31 && number <= 39) { return "thirty-" + GetExactNumberText(number - 30); }
            if (number >= 41 && number <= 49) { return "fourty-" + GetExactNumberText(number - 40); }
            if (number >= 51 && number <= 59) { return "fifty-" + GetExactNumberText(number - 50); }
            if (number >= 61 && number <= 69) { return "sixty-" + GetExactNumberText(number - 60); }
            if (number >= 71 && number <= 79) { return "seventy-" + GetExactNumberText(number - 70); }
            if (number >= 81 && number <= 89) { return "eighty-" + GetExactNumberText(number - 80); }
            if (number >= 91 && number <= 99) { return "ninety-" + GetExactNumberText(number - 90); }
            if (number >= 100 && number <= 199) { return "one hundred " + GetExactNumberText(number - 100); }
            if (number >= 200 && number <= 299) { return "two hundred " + GetExactNumberText(number - 200); }
            if (number >= 300 && number <= 399) { return "three hundred " + GetExactNumberText(number - 300); }
            if (number >= 400 && number <= 499) { return "four hundred " + GetExactNumberText(number - 400); }
            if (number >= 500 && number <= 599) { return "five hundred " + GetExactNumberText(number - 500); }
            if (number >= 600 && number <= 699) { return "six hundred " + GetExactNumberText(number - 600); }
            if (number >= 700 && number <= 799) { return "seven hundred " + GetExactNumberText(number - 700); }
            if (number >= 800 && number <= 899) { return "eight hundred " + GetExactNumberText(number - 800); }
            if (number >= 900 && number <= 999) { return "nine hundred " + GetExactNumberText(number - 800); }
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