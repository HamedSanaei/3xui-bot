using Adminbot.Domain;

/// <summary>
/// Single source of truth for whether a platform payment gateway may be offered or invoked by a tenant storefront.
/// </summary>
/// <remarks>
/// New invoice creation requires both the live global platform switch and the exact storefront owner's persisted switch.
/// Previously created invoices remain eligible for inquiry and settlement after later disablement.
/// </remarks>
public static class TenantPaymentGatewayPolicy
{
    /// <summary>Returns true only when the provider is enabled globally and for this exact tenant store.</summary>
    public static bool IsEnabled(
        BotInstance tenant,
        PaymentGateway gateway,
        PaymentGatewayAvailabilitySnapshot global)
    {
        if (tenant == null || global == null || !global.IsEnabled(gateway))
            return false;

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

    /// <summary>Returns true when the owner's personal card route is fully configured for this tenant.</summary>
    public static bool IsPersonalCardEnabled(BotInstance tenant)
        => tenant?.TenantCardPaymentEnabled == true &&
           !string.IsNullOrWhiteSpace(tenant.TenantCardNumber);
}
