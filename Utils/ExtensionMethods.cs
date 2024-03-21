using System;
using System.Collections.Generic;
using System.Linq;
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

        public static double ConvertBytesToGB(this long bytes)
        {
            const double bytesInGB = 1024 * 1024 * 1024;
            return bytes / bytesInGB;
        }

    }
}