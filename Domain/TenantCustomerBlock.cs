namespace Adminbot.Domain;

/// <summary>An active order and receipt restriction for one Telegram customer in one tenant storefront.</summary>
/// <remarks>The composite storefront/customer key is unique. Removing this row lifts the restriction without modifying the source receipt or audit timestamps on it.</remarks>
public sealed class TenantCustomerBlock
{
    /// <summary>Internal BotInstances identifier of the tenant storefront that owns this restriction.</summary>
    public string TenantBotId { get; set; }

    /// <summary>Numeric Telegram user id restricted within this storefront.</summary>
    public long CustomerTelegramUserId { get; set; }

    /// <summary>Internal users.db id of the rejected receipt. It also identifies this particular block when lifting it.</summary>
    public int ReceiptId { get; set; }

    /// <summary>Global Telegram user id of the authorized storefront owner who decided the block.</summary>
    public long BlockedByTelegramUserId { get; set; }

    /// <summary>UTC time the rejection and restriction were committed together.</summary>
    public DateTime BlockedAtUtc { get; set; }
}
