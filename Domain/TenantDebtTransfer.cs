namespace Adminbot.Domain;

/// <summary>Durable owner-wide transfer from the website wallet into the bot wallet to repay debt.</summary>
/// <remarks>Immutable identity and amount; no order or profit. Retain for audit. Only a confirmed website receipt authorizes local credit.</remarks>
public sealed class TenantDebtTransfer
{
    /// <summary>Independent business id shared by both wallet receipts.</summary>
    public string Id { get; set; }
    /// <summary>Global owner's Telegram id, shared by all their stores.</summary>
    public long OwnerTelegramUserId { get; set; }
    /// <summary>Positive immutable transfer amount in toman.</summary>
    public long AmountToman { get; set; }
    /// <summary>Pending, completed or cancelled before remote attempt. Pending uncertainty prevents another transfer.</summary>
    public string Status { get; set; } = "pending";
    /// <summary>UTC reservation time; financial history is retained.</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
