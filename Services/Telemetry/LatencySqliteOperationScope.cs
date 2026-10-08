namespace Adminbot.Services.Telemetry;

/// <summary>Closed background operation categories for the workers involved in observed SQLite contention.</summary>
/// <remarks>Categories identify code ownership only. Tenant/customer ids, financial values and SQL never enter this vocabulary.</remarks>
public enum LatencySqliteOperationCategory
{
    /// <summary>Tenant manual-receipt notification scanning and local delivery receipts.</summary>
    TenantManualReceiptNotification,
    /// <summary>Storefront funding alert scanning and local delivery receipts.</summary>
    TenantStorefrontFundingAlert,
    /// <summary>Discount reservation expiry/reconciliation database work.</summary>
    TenantDiscountReservation,
    /// <summary>Payment settlement customer notification database work; no amount or wallet metadata is exposed.</summary>
    PaymentSettlementNotification,
    /// <summary>Existing XUI renewal recovery database work, separate from its external network effects.</summary>
    XuiRenewalRecovery
}

/// <summary>Supplies a fixed database-operation category across an existing background worker's awaited boundaries.</summary>
/// <remarks>This is a category only, not a second trace or handler clock. Query tenant isolation remains owned by the existing worker;
/// telemetry records are global unless the existing handler scope supplies bot/update correlation.</remarks>
public sealed class LatencySqliteOperationScope : IDisposable
{
    /// <summary>Worker-local metadata isolated across concurrent async execution flows.</summary>
    private static readonly AsyncLocal<string> Ambient = new();
    /// <summary>Enclosing category restored after the owned worker scope ends.</summary>
    private readonly string _previous;
    /// <summary>Atomic disposal guard preventing repeated restoration of an enclosing flow.</summary>
    private int _disposed;

    /// <summary>Gets the controlled category active in this asynchronous flow, or null outside tagged workers.</summary>
    public static string Current => Ambient.Value;

    /// <summary>Enters one compile-time database category without logging, querying or retaining a worker.</summary>
    /// <param name="category">Required enum category chosen by the owning worker, never input from a customer or callback.</param>
    /// <returns>A disposable scope restoring the previous category on worker completion/cancellation.</returns>
    /// <remarks>One scope per worker lifetime is sufficient. Unknown enum values use sqlite_background rather than affecting execution.</remarks>
    /// <example><code>using var diagnostics = LatencySqliteOperationScope.Push(LatencySqliteOperationCategory.XuiRenewalRecovery);</code></example>
    public static LatencySqliteOperationScope Push(LatencySqliteOperationCategory category) => new(category);

    /// <summary>Initializes only asynchronous-flow metadata with one bounded constant.</summary>
    /// <param name="category">Internal worker category, never a tenant id.</param>
    /// <remarks>No transaction, retry, financial authorization or SQL statement changes.</remarks>
    private LatencySqliteOperationScope(LatencySqliteOperationCategory category)
    {
        _previous = Ambient.Value;
        Ambient.Value = category switch
        {
            LatencySqliteOperationCategory.TenantManualReceiptNotification => "tenant_manual_receipt_notification",
            LatencySqliteOperationCategory.TenantStorefrontFundingAlert => "tenant_storefront_funding_alert",
            LatencySqliteOperationCategory.TenantDiscountReservation => "tenant_discount_reservation",
            LatencySqliteOperationCategory.PaymentSettlementNotification => "payment_settlement_notification",
            LatencySqliteOperationCategory.XuiRenewalRecovery => "xui_renewal_recovery",
            _ => "sqlite_background"
        };
    }

    /// <summary>Restores the enclosing category once, including cancellation and worker failure.</summary>
    /// <remarks>The scope owns no database/network/file resources and cannot retry any side effect.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) Ambient.Value = _previous;
    }
}
