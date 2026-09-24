namespace Adminbot.Domain;

/// <summary>Defines webhook-first AtlasPay automatic confirmation with bounded polling fallback.</summary>
/// <remarks>
/// A valid signed webhook remains the fastest automatic trigger, but it is never the only recovery path. Every created
/// invoice also receives a bounded read-only inquiry schedule so a missing, delayed, or misconfigured webhook cannot
/// strand a provider-confirmed payment forever. Customer and super-admin checks remain explicit authoritative inquiries.
/// </remarks>
public static class AtlasPayPollingPolicy
{
    /// <summary>Returns whether signed AtlasPay webhook delivery is configured as the primary automatic trigger.</summary>
    /// <param name="configuration">AtlasPay runtime settings.</param>
    /// <returns><c>true</c> when a non-empty webhook signing secret is configured.</returns>
    public static bool UsesWebhookPrimary(AppConfig configuration)
        => !string.IsNullOrWhiteSpace(configuration?.AtlasPayWebhookSecret);

    /// <summary>Schedules the first bounded fallback inquiry for every successfully created invoice.</summary>
    /// <param name="configuration">AtlasPay runtime settings.</param>
    /// <param name="nowUtc">Current UTC time supplied by the caller.</param>
    /// <returns>The first fallback poll time. Webhook delivery may reconcile the payment earlier.</returns>
    public static DateTime? GetInitialNextInquiryUtc(AppConfig configuration, DateTime nowUtc)
        => nowUtc.AddSeconds(Math.Clamp(configuration?.AtlasPayReconciliationIntervalSeconds ?? 30, 10, 3600));
}
