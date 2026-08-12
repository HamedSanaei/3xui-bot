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

        /// <summary>
        /// Converts decimal digits from any Unicode decimal script, including Persian and Arabic-Indic digits,
        /// to their ASCII equivalents while preserving every non-digit character.
        /// </summary>
        /// <param name="input">
        /// The optional user-supplied text to normalize. A null or empty value is returned unchanged.
        /// </param>
        /// <returns>
        /// A string whose Unicode decimal digits are represented by <c>0</c> through <c>9</c>; non-digit text is
        /// unchanged. The return value is null only when <paramref name="input" /> is null.
        /// </returns>
        /// <remarks>
        /// Telegram amount, duration, and identifier parsers use this helper before numeric validation. It performs
        /// no trimming and does not discard signs, separators, or letters, so callers remain responsible for their
        /// own input grammar.
        /// </remarks>
        /// <example>
        /// <code>
        /// var normalized = "مدت ۳ روز".PersianNumbersToEnglish();
        /// // normalized == "مدت 3 روز"
        /// </code>
        /// </example>
        public static string PersianNumbersToEnglish(this string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return input;
            }

            var normalized = new StringBuilder(input.Length);
            foreach (char c in input)
            {
                var numericValue = char.GetNumericValue(c);
                if (numericValue >= 0 && numericValue <= 9 && Math.Floor(numericValue) == numericValue)
                    normalized.Append((char)('0' + (int)numericValue));
                else
                    normalized.Append(c);
            }

            return normalized.ToString();
        }
    }
}
