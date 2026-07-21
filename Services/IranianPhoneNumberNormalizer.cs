using System.Text;

/// <summary>
/// Normalizes Iranian mobile numbers submitted through Telegram contacts without accepting foreign numbers.
/// </summary>
/// <remarks>
/// This helper is used only by automatic owned-bot contact verification. The separate super-admin manual
/// verification flow intentionally accepts international and virtual numbers and must not call this helper.
/// </remarks>
public static class IranianPhoneNumberNormalizer
{
    /// <summary>
    /// Tries to normalize an Iranian mobile number to the canonical <c>+989xxxxxxxxx</c> representation.
    /// </summary>
    /// <param name="phoneNumber">
    /// Raw phone number received from a Telegram <c>Contact</c>. Persian and Arabic digits plus common visual
    /// separators are accepted. The value must represent an Iranian mobile prefix beginning with <c>09</c>.
    /// </param>
    /// <param name="normalizedPhoneNumber">
    /// Canonical <c>+989xxxxxxxxx</c> value when validation succeeds; otherwise an empty string.
    /// </param>
    /// <returns>
    /// <c>true</c> only for valid Iranian mobile forms such as <c>09...</c>, <c>98...</c>, <c>+98...</c>, or
    /// <c>0098...</c>; otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// This method validates format and country only. Callers must separately verify that Telegram's contact
    /// <c>UserId</c> equals the sender's Telegram user id before persisting the result.
    /// </remarks>
    /// <example>
    /// <code>
    /// if (IranianPhoneNumberNormalizer.TryNormalize("0098 912 345 6789", out var phone))
    /// {
    ///     // phone == "+989123456789"
    /// }
    /// </code>
    /// </example>
    public static bool TryNormalize(string phoneNumber, out string normalizedPhoneNumber)
    {
        normalizedPhoneNumber = string.Empty;
        if (string.IsNullOrWhiteSpace(phoneNumber))
            return false;

        var builder = new StringBuilder(phoneNumber.Length);
        foreach (var character in phoneNumber.Trim())
        {
            if (character == '+' && builder.Length == 0)
            {
                builder.Append(character);
                continue;
            }

            var numericValue = char.GetNumericValue(character);
            if (numericValue is >= 0 and <= 9 && Math.Floor(numericValue) == numericValue)
            {
                builder.Append((char)('0' + (int)numericValue));
                continue;
            }

            if (char.IsWhiteSpace(character) || character is '-' or '‐' or '‑' or '–' or '—' or '(' or ')' or '.' or '/')
                continue;

            return false;
        }

        var compact = builder.ToString();
        string nationalNumber;
        if (compact.StartsWith("+98", StringComparison.Ordinal))
            nationalNumber = compact[3..];
        else if (compact.StartsWith("0098", StringComparison.Ordinal))
            nationalNumber = compact[4..];
        else if (compact.StartsWith("98", StringComparison.Ordinal))
            nationalNumber = compact[2..];
        else if (compact.StartsWith("0", StringComparison.Ordinal))
            nationalNumber = compact[1..];
        else
            return false;

        if (nationalNumber.Length != 10 || nationalNumber[0] != '9' || !nationalNumber.All(char.IsDigit))
            return false;

        normalizedPhoneNumber = "+98" + nationalNumber;
        return true;
    }
}
