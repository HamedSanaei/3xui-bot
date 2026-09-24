using System.Xml;

namespace Adminbot.Services.AppleMobileConfig;

/// <summary>Central input validation for safe Apple plist generation.</summary>
public static class MobileConfigValidation
{
    public const int MaxApnLength = 100;
    public const int MaxDisplayNameLength = 128;
    // Apple documents APN usernames/passwords with a 64-character maximum.
    public const int MaxCredentialLength = 64;
    public const int MaxDescriptionLength = 512;

    /// <summary>Validates and normalizes all generator options without exposing secret values in errors.</summary>
    public static ApnProfileOptions ValidateAndNormalize(ApnProfileOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!Enum.IsDefined(options.Protocol))
            throw new ArgumentOutOfRangeException(nameof(options.Protocol), "Unsupported IP protocol mode.");
        if (!Enum.IsDefined(options.AuthenticationType))
            throw new ArgumentOutOfRangeException(nameof(options.AuthenticationType), "Unsupported APN authentication type.");

        var apn = NormalizeRequired(options.Apn, nameof(options.Apn), MaxApnLength);
        var displayName = NormalizeRequired(options.DisplayName, nameof(options.DisplayName), MaxDisplayNameLength);
        var username = NormalizeOptional(options.Username, nameof(options.Username), MaxCredentialLength, trim: false);
        var password = NormalizeOptional(options.Password, nameof(options.Password), MaxCredentialLength, trim: false);
        var description = NormalizeOptional(options.Description, nameof(options.Description), MaxDescriptionLength, trim: true);

        return options with
        {
            Apn = apn,
            DisplayName = displayName,
            Username = username,
            Password = password,
            Description = description
        };
    }

    /// <summary>Validates one APN typed in the Telegram flow.</summary>
    public static string NormalizeApn(string apn)
        => NormalizeRequired(apn, nameof(ApnProfileOptions.Apn), MaxApnLength);

    private static string NormalizeRequired(string value, string fieldName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{fieldName} is required.", fieldName);

        var normalized = value.Trim();
        ValidateLengthAndXml(normalized, fieldName, maxLength);
        return normalized;
    }

    private static string NormalizeOptional(string value, string fieldName, int maxLength, bool trim)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        var normalized = trim ? value.Trim() : value;
        if (normalized.Length == 0)
            return null;

        ValidateLengthAndXml(normalized, fieldName, maxLength);
        return normalized;
    }

    private static void ValidateLengthAndXml(string value, string fieldName, int maxLength)
    {
        if (value.Length > maxLength)
            throw new ArgumentException($"{fieldName} exceeds the supported maximum length.", fieldName);

        try
        {
            XmlConvert.VerifyXmlChars(value);
        }
        catch (XmlException)
        {
            // Never include the supplied value here because Password can reach this validator.
            throw new ArgumentException($"{fieldName} contains characters that are not valid in XML 1.0.", fieldName);
        }
    }
}
