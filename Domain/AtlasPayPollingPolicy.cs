namespace Adminbot.Domain;

/// <summary>Defines webhook-first AtlasPay automatic-confirmation admission.</summary>
/// <remarks>
/// When a signing secret is configured, automatic provider inquiries are admitted only by a durable signed webhook event.
/// Customer and super-admin checks remain explicit authoritative inquiries. Legacy deployments without a webhook secret
/// retain the bounded background polling path so already-issued invoices are not stranded.
/// </remarks>
public static class AtlasPayPollingPolicy
{
    /// <summary>Returns whether signed AtlasPay webhook delivery is the primary automatic trigger.</summary>
    /// <param name="configuration">AtlasPay runtime settings.</param>
    /// <returns><c>true</c> when a non-empty webhook signing secret is configured.</returns>
    public static bool UsesWebhookPrimary(AppConfig configuration)
        => !string.IsNullOrWhiteSpace(configuration?.AtlasPayWebhookSecret);

    /// <summary>Schedules legacy automatic polling only when no signed webhook has been configured.</summary>
    /// <param name="configuration">AtlasPay runtime settings.</param>
    /// <param name="nowUtc">Current UTC time supplied by the caller.</param>
    /// <returns>The first legacy poll time, or <c>null</c> when webhook-first admission is active.</returns>
    public static DateTime? GetInitialNextInquiryUtc(AppConfig configuration, DateTime nowUtc)
        => UsesWebhookPrimary(configuration)
            ? null
            : nowUtc.AddSeconds(Math.Clamp(configuration?.AtlasPayReconciliationIntervalSeconds ?? 30, 10, 3600));
}
