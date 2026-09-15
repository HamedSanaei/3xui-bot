namespace Adminbot.Domain;

/// <summary>Delivery priority within one bot; active requests are never preempted.</summary>
public enum TelegramWorkPriority
{
    /// <summary>Existing financial/account/renewal outboxes waiting for a real Telegram acknowledgement.</summary>
    Critical = 0,
    /// <summary>Menus, status replies and edits produced by foreground business execution.</summary>
    Normal = 1,
    /// <summary>Operator logs and best-effort notifications.</summary>
    Low = 2
}
