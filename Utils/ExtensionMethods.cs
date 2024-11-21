using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Adminbot.Utils
{
    public static class ExtensionMethods
    {
        // public static string EscapeMarkdown(this string text)
        // {
        //     if (string.IsNullOrEmpty(text))
        //         return text;

        //     char[] charactersToEscape = { '_', '*', '[', ']', '(', ')' };
        //     foreach (char character in charactersToEscape)
        //     {
        //         text = text.Replace(character.ToString(), "\\" + character);
        //     }
        //     return text;
        // }

        public static string EscapeMarkdown(this string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            var codeBlockRegex = new Regex(@"(`[^`]*`)");
            var parts = codeBlockRegex.Split(text);
            char[] charactersToEscape = { '_', '*', '[', ']', '(', ')' };

            for (int i = 0; i < parts.Length; i++)
            {
                // Only escape characters in text outside of code blocks
                if (!parts[i].StartsWith("`") || !parts[i].EndsWith("`"))
                {
                    foreach (char character in charactersToEscape)
                    {
                        parts[i] = parts[i].Replace(character.ToString(), "\\" + character);
                    }
                }
            }

            return string.Concat(parts);
        }

        public static string EscapeHtml(this string input)
        {
            return input
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }
        public static double ConvertBytesToGB(this long bytes)
        {
            const double bytesInGB = 1024 * 1024 * 1024;
            return bytes / bytesInGB;
        }

        public static bool TryConvertToLong(this string input, out long result)
        {
            return long.TryParse(input, out result);
        }
        public static string ToValidNumber(this string input)
        {
            if (string.IsNullOrEmpty(input))
                return "0";

            // Filter out non-numeric characters
            var numericString = new string(input.Where(char.IsDigit).ToArray());

            return numericString;

            // Try parsing the numeric string to long
            //return long.TryParse(numericString, out long result) ? result : 0;
        }

        public static string PersianNumbersToEnglish(this string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return input;
            }

            var builder = new StringBuilder(input.Length);
            foreach (char c in input)
            {
                switch (c)
                {
                    case '۰': builder.Append('0'); break;
                    case '۱': builder.Append('1'); break;
                    case '۲': builder.Append('2'); break;
                    case '۳': builder.Append('3'); break;
                    case '۴': builder.Append('4'); break;
                    case '۵': builder.Append('5'); break;
                    case '۶': builder.Append('6'); break;
                    case '۷': builder.Append('7'); break;
                    case '۸': builder.Append('8'); break;
                    case '۹': builder.Append('9'); break;
                    default: builder.Append(c); break; // If it's not a Persian number, just append the character as is
                }
            }
            return builder.ToString();
        }
    }
}