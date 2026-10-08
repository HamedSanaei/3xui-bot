using Adminbot.Domain;
using Microsoft.Extensions.Configuration;
using System.Globalization;

namespace Adminbot.Services.TelegramEndpoints;

/// <summary>Reads the existing live global Super Admin allow-list without granting tenant-owner authority.</summary>
/// <remarks>Production configuration reloads in place. A removed or empty live list must never fall back to the stale startup snapshot. Direct isolated constructors may omit live configuration and use their mutable AppConfig fixture.</remarks>
internal static class TelegramEndpointAdministratorPolicy
{
    /// <summary>Checks one sender/recipient against the current global allow-list.</summary>
    /// <param name="live">Live application IConfiguration, or null only for isolated direct construction.</param>
    /// <param name="fallback">Required startup/direct-construction AppConfig; never tenant-local settings.</param>
    /// <param name="actor">Positive numeric Telegram user id, not a chat, bot, tenant, or database id.</param>
    /// <returns>True only for an explicitly listed current global Super Admin.</returns>
    /// <remarks>Called on every panel action and again immediately before an alert send boundary.</remarks>
    /// <example><code>if (!TelegramEndpointAdministratorPolicy.IsAuthorized(configuration, options, sender.Id)) return;</code></example>
    internal static bool IsAuthorized(IConfiguration live, AppConfig fallback, long actor)
    {
        if (actor <= 0) return false;
        if (live == null) return fallback.AdminsUserIds?.Contains(actor) == true;
        foreach (var child in live.GetSection(nameof(AppConfig.AdminsUserIds)).GetChildren())
            if (long.TryParse(child.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var configured) && configured == actor) return true;
        return false;
    }

    /// <summary>Enumerates current positive global operator ids for transactional alert intent creation.</summary>
    /// <param name="live">Live application configuration; a configured empty list yields no recipients.</param>
    /// <param name="fallback">Required mutable direct-construction AppConfig when live configuration is absent.</param>
    /// <returns>Current global Telegram user ids; may be empty or duplicated, so the store deduplicates before inserting receipts.</returns>
    /// <remarks>No customer audience, chat id, token, tenant-owner role or notification transport is discovered here.</remarks>
    /// <example><code>foreach (var admin in TelegramEndpointAdministratorPolicy.Recipients(configuration, options).Distinct()) { /* persist intent */ }</code></example>
    internal static IEnumerable<long> Recipients(IConfiguration live, AppConfig fallback)
    {
        if (live == null)
        {
            if (fallback.AdminsUserIds != null) foreach (var id in fallback.AdminsUserIds) if (id > 0) yield return id;
            yield break;
        }
        foreach (var child in live.GetSection(nameof(AppConfig.AdminsUserIds)).GetChildren())
            if (long.TryParse(child.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0) yield return id;
    }
}
