using Adminbot.Domain;

/// <summary>
/// Single source of truth for whether a platform payment gateway may be offered or invoked by a tenant storefront.
/// </summary>
/// <remarks>
/// New invoice creation requires a live global gateway. Funded storefronts also require their saved provider
/// preference; underfunded storefronts use exactly the central gateway set and cannot use a personal card.
/// Missing, blocked, or unobserved owner funding fails closed. This policy never changes persisted preferences.
/// Previously created invoices remain eligible for inquiry and settlement after later disablement or funding changes.
/// </remarks>
public static class TenantPaymentGatewayPolicy
{
    /// <summary>Determines new central-gateway admission from one exact-owner funding snapshot.</summary>
    /// <param name="tenant">Required storefront whose saved provider preferences apply only in funded mode.</param>
    /// <param name="gateway">Central provider being offered or newly invoked, never an existing invoice inquiry.</param>
    /// <param name="global">Required current central gateway switches shared with owned bots.</param>
    /// <param name="funding">Required exact stored owner's read-only financial classification; null fails closed.</param>
    /// <returns>True only for a live global provider and an accessible owner, ignoring tenant opt-outs only during insufficient funding.</returns>
    /// <remarks>No financial or configuration mutation occurs. Callers must re-evaluate funding before first creation, not reuse a menu snapshot.</remarks>
    /// <example><code>TenantPaymentGatewayPolicy.IsEnabled(store, gateway, global, funding);</code></example>
    public static bool IsEnabled(
        BotInstance tenant,
        PaymentGateway gateway,
        PaymentGatewayAvailabilitySnapshot global,
        TenantAccessEvaluation funding)
    {
        if (tenant == null || global == null || funding?.IsAllowed != true || !global.IsEnabled(gateway))
            return false;
        // Do not persist the override: sibling stores share owner funding, but each store's saved choices must return after recovery.
        if (funding.RequiresPlatformPayments)
            return true;

        return gateway switch
        {
            PaymentGateway.HooshPay => tenant.TenantHooshPayEnabled,
            PaymentGateway.Tetraminator => tenant.TenantTetraminatorEnabled,
            PaymentGateway.UniquePay => tenant.TenantUniquePayEnabled,
            PaymentGateway.AtlasPay => tenant.TenantAtlasPayEnabled,
            PaymentGateway.NowPayments => tenant.TenantNowPaymentsEnabled,
            _ => false
        };
    }

    /// <summary>Determines whether new personal-card payment admission is configured and financially eligible.</summary>
    /// <param name="tenant">Required storefront with its saved personal-card preference and card number.</param>
    /// <param name="funding">Required exact stored owner's funding classification; null, blocked, missing, or insufficient funding denies admission.</param>
    /// <returns>True only in funded Allowed mode with the owner's personal card enabled and configured.</returns>
    /// <remarks>Does not gate settlement of actual previously submitted receipts and never mutates tenant preferences.</remarks>
    /// <example><code>if (TenantPaymentGatewayPolicy.IsPersonalCardEnabled(store, funding)) { /* Offer personal card. */ }</code></example>
    public static bool IsPersonalCardEnabled(BotInstance tenant, TenantAccessEvaluation funding)
        => funding?.Decision == TenantAccessDecision.Allowed &&
           tenant?.TenantCardPaymentEnabled == true &&
           !string.IsNullOrWhiteSpace(tenant.TenantCardNumber);
}
