namespace Adminbot.Domain;

/// <summary>Durable website debit receipt in users.db, independent from the shared local wallet in credentials.db.</summary>
/// <remarks>Owner/key/amount are immutable. Sending means uncertain after restart; never replay automatically. Retain for financial audit.</remarks>
public sealed class SiteWalletDebitOperation
{
    /// <summary>Business event key containing owner, reference type and reference id; primary key, never a Telegram update id.</summary>
    public string Id { get; set; }
    /// <summary>Shared website account owner's Telegram user id; never tenant/customer identity.</summary>
    public long OwnerTelegramUserId { get; set; }
    /// <summary>Positive debit in Iranian toman.</summary>
    public long AmountToman { get; set; }
    /// <summary>Sending or applied. Sending prohibits another debit and requires operator reconciliation.</summary>
    public string Status { get; set; } = "sending";
    /// <summary>Website receipt balance before debit in toman; valid only when applied.</summary>
    public long BeforeBalance { get; set; }
    /// <summary>Website receipt balance after debit in toman; valid only when applied.</summary>
    public long AfterBalance { get; set; }
    /// <summary>UTC time when the non-replayable remote intent was committed.</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Pins the funding source of one tenant order before its first financial effect.</summary>
/// <remarks>Order id is a logical foreign key to TenantBotOrders. Keep with order history; this is not a cross-database transaction.</remarks>
public sealed class TenantWalletRoute
{
    /// <summary>Internal TenantBotOrder primary key, globally unique across storefronts.</summary>
    public int Id { get; set; }
    /// <summary>Shared wallet owner's Telegram user id, immutable.</summary>
    public long OwnerTelegramUserId { get; set; }
    /// <summary>Immutable base cost in positive Iranian toman.</summary>
    public long AmountToman { get; set; }
    /// <summary>bot, site or negative; review marks historical uncertain funding. Site changes to negative only before a remote attempt.</summary>
    public string Source { get; set; }
}

/// <summary>Signals an uncertain website debit that must not be retried or compensated from another wallet.</summary>
public sealed class SiteWalletDebitUncertainException : InvalidOperationException
{
    /// <summary>Creates an operation-specific recovery signal without payloads or credentials.</summary>
    /// <param name="operationId">Persisted financial business key for operator reconciliation.</param>
    public SiteWalletDebitUncertainException(string operationId)
        : base($"Website debit requires reconciliation: {operationId}") { }
}
