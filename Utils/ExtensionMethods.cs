using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Adminbot.Utils
{
    public static class ExtensionMethods
    {
        public static string EscapeMarkdown(this string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            char[] charactersToEscape = { '_', '*', '[', ']', '(', ')' };
            foreach (char character in charactersToEscape)
            {
                text = text.Replace(character.ToString(), "\\" + character);
            }
            return text;
        }

    }
}