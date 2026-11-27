namespace Adminbot.Domain;

/// <summary>Global wallet-charge campaign settings. Missing settings mean disabled with the default offer.</summary>
public sealed class WalletChargeCampaign
{
    /// <summary>Singleton users.db key, shared by all owned bots.</summary>
    public int Id { get; set; } = 1;

    /// <summary>Plain-text campaign name displayed to admins and eligible customers.</summary>
    public string Name { get; set; } = "بلک‌فرایدی";

    /// <summary>Whole bonus percentage, from 1 through 100 inclusive.</summary>
    public int Percent { get; set; } = 20;

    /// <summary>Whether new eligible invoices may capture this offer; existing offers are unchanged.</summary>
    public bool Enabled { get; set; }
}

/// <summary>Permanent, invoice-scoped campaign promise and its recovery state in users.db.</summary>
/// <remarks>Provider plus local payment id is the logical foreign key across the separate provider tables.
/// Zero bonus freezes a non-offer. Positive offers become due only after official payment and a matching principal
/// receipt. The named credentials.db credit and idempotent ledger/outbox entries survive separate commit failures.</remarks>
public sealed class WalletChargeBonus
{
    /// <summary>Canonical provider key used in the principal and bonus wallet operation keys.</summary>
    public string Provider { get; set; }

    /// <summary>Positive local payment id, scoped to the provider.</summary>
    public int PaymentId { get; set; }

    /// <summary>Internal owned-bot id that issued this invoice; never rebound to a tenant.</summary>
    public string BotId { get; set; }

    /// <summary>Global Telegram user id whose wallet received the original payment.</summary>
    public long TelegramUserId { get; set; }

    /// <summary>Private Telegram destination captured at issuance.</summary>
    public long ChatId { get; set; }

    /// <summary>Frozen campaign name; empty for an ineligible or disabled issuance.</summary>
    public string Name { get; set; }

    /// <summary>Frozen whole bonus percentage; zero when no campaign applied.</summary>
    public int Percent { get; set; }

    /// <summary>Original credited principal in whole toman, excluding fees and bonus.</summary>
    public long PrincipalToman { get; set; }

    /// <summary>Frozen whole-toman bonus, rounded down; zero means no credit will be attempted.</summary>
    public long BonusToman { get; set; }

    /// <summary>Earliest UTC recovery time after official confirmation; null until the offer is ready.</summary>
    public DateTime? NextAttemptAtUtc { get; set; }

    /// <summary>Whether official provider proof explicitly confirmed a provisional principal receipt.</summary>
    public bool ProvisionalConfirmed { get; set; }

    /// <summary>UTC completion after the bonus receipt, ledger entry and final notification are durable.</summary>
    public DateTime? CompletedAtUtc { get; set; }
}
