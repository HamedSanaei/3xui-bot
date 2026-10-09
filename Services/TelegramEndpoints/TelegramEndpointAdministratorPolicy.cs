using Adminbot.Domain;
using Microsoft.Extensions.Configuration;
using System.Globalization;

namespace Adminbot.Services.TelegramEndpoints;

/// <summary>Reads the existing live global Super Admin allow-list without granting tenant-owner authority.</summary>
/// <remarks>Production configuration reloads in place. A removed or empty live list must never fall back to the stale startup snapshot. Direct isolated constructors may omit live configuration and use their mutable AppConfig fixture.</remarks>
internal static class TelegramEndpointAdministratorPolicy
{
    /// <summary>Checks one panel actor against the current global allow-list.</summary>
    /// <param name="live">Live application IConfiguration, or null only for isolated direct construction.</param>
    /// <param name="fallback">Required startup/direct-construction AppConfig; never tenant-local settings.</param>
    /// <param name="actor">Positive numeric Telegram user id, not a chat, bot, tenant, or database id.</param>
    /// <returns>True only for an explicitly listed current global Super Admin.</returns>
    /// <remarks>Called on every panel action. Operator incidents instead target the configured logger channel, independently of private-chat recipients.</remarks>
    /// <example><code>if (!TelegramEndpointAdministratorPolicy.IsAuthorized(configuration, options, sender.Id)) return;</code></example>
    internal static bool IsAuthorized(IConfiguration live, AppConfig fallback, long actor)
    {
        if (actor <= 0) return false;
        if (live == null) return fallback.AdminsUserIds?.Contains(actor) == true;
        foreach (var child in live.GetSection(nameof(AppConfig.AdminsUserIds)).GetChildren())
            if (long.TryParse(child.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var configured) && configured == actor) return true;
        return false;
    }

}
