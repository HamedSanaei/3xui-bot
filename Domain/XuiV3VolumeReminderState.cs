namespace Adminbot.Domain
{
    /// <summary>
    /// Stores the durable traffic-reminder cycle and delivery claim for one physical XUI v3 client.
    /// </summary>
    /// <remarks>
    /// Rows live only in <c>users.db</c> and are isolated by the hashed panel identity plus numeric panel client id.
    /// The row contains no panel token or subscription secret. Its cycle is advanced after a confirmed renewal,
    /// counter reset, quota increase, or client recreation so 80/90/99 percent notifications can run again.
    /// </remarks>
    public sealed class XuiV3VolumeReminderState
    {
        /// <summary>Database-generated users.db row identifier.</summary>
        public int Id { get; set; }

        /// <summary>SHA-256 identity of the panel base URL and root path; it contains no credentials.</summary>
        public string PanelKey { get; set; }

        /// <summary>Numeric client id assigned by the XUI v3 panel.</summary>
        public int ClientId { get; set; }

        /// <summary>Panel creation timestamp in Unix milliseconds, used to detect a recreated client id.</summary>
        public long ClientCreatedAt { get; set; }

        /// <summary>Latest client email used in reminder text and renewal callbacks.</summary>
        public string Email { get; set; }

        /// <summary>Owned or tenant runtime bot id that created and must send notifications for this account.</summary>
        public string BotId { get; set; }

        /// <summary>Numeric Telegram id of the account owner.</summary>
        public long TelegramUserId { get; set; }

        /// <summary>Local monotonically increasing volume cycle number for this physical panel client.</summary>
        public long CycleNumber { get; set; } = 1;

        /// <summary>Latest panel <c>updatedAt</c> value in Unix milliseconds observed from the list API.</summary>
        public long PanelUpdatedAt { get; set; }

        /// <summary>Most recently observed finite traffic quota in bytes.</summary>
        public long TotalBytes { get; set; }

        /// <summary>Most recently observed upload plus download consumption in bytes.</summary>
        public long UsedBytes { get; set; }

        /// <summary>Latest renewal marker embedded in bot-owned XUI metadata, when available.</summary>
        public DateTime? LastRenewedAtUtc { get; set; }

        /// <summary>
        /// Highest threshold durably handled in the current cycle; lower skipped thresholds are implicitly handled.
        /// </summary>
        public int HighestHandledThreshold { get; set; }

        /// <summary>Threshold currently claimed for Telegram delivery, or null when no send is in progress.</summary>
        public int? ClaimedThreshold { get; set; }

        /// <summary>Current delivery state from <see cref="XuiV3VolumeReminderDeliveryStatuses"/>.</summary>
        public string DeliveryStatus { get; set; } = XuiV3VolumeReminderDeliveryStatuses.Idle;

        /// <summary>UTC deadline for the exclusive delivery claim.</summary>
        public DateTime? LeaseUntilUtc { get; set; }

        /// <summary>Number of Telegram send claims attempted during the current cycle.</summary>
        public int AttemptCount { get; set; }

        /// <summary>Telegram message id returned by the latest successful threshold notification.</summary>
        public int? TelegramMessageId { get; set; }

        /// <summary>Sanitized latest delivery or persistence error retained for operational diagnosis.</summary>
        public string LastError { get; set; }

        /// <summary>UTC timestamp when this row was first created.</summary>
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>UTC timestamp of the latest material state change.</summary>
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>UTC timestamp of the latest periodic observation persisted for this client.</summary>
        public DateTime LastObservedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>UTC timestamp of the latest successful Telegram threshold notification.</summary>
        public DateTime? LastDeliveredAtUtc { get; set; }
    }

    /// <summary>
    /// Defines durable delivery states for one XUI v3 client volume-reminder row.
    /// </summary>
    public static class XuiV3VolumeReminderDeliveryStatuses
    {
        /// <summary>No threshold delivery is currently claimed.</summary>
        public const string Idle = "idle";
        /// <summary>A worker owns a live pre-delivery claim.</summary>
        public const string Processing = "processing";
        /// <summary>Telegram returned a concrete message id and the threshold was persisted.</summary>
        public const string Sent = "sent";
        /// <summary>Telegram reported that the user blocked the originating bot.</summary>
        public const string TelegramBlocked = "telegram_blocked";
        /// <summary>A definite retryable delivery failure released the claim.</summary>
        public const string Failed = "failed";
        /// <summary>
        /// A stale claim or post-send persistence failure was suppressed to prefer no duplicate notification.
        /// </summary>
        public const string Ambiguous = "ambiguous";
    }
}
