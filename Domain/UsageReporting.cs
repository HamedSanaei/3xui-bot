namespace Adminbot.Domain;

/// <summary>
/// Durable delivery state for one scheduled usage report sent to the central Telegram logger channel.
/// </summary>
/// <remarks>
/// Rows live only in <c>users.db</c>. The unique <see cref="ReportKey"/> prevents concurrent workers or service
/// restarts from intentionally sending the same completed-week report more than once.
/// </remarks>
public sealed class UsageReportDispatch
{
    /// <summary>Database-generated users.db row identifier.</summary>
    public int Id { get; set; }

    /// <summary>Stable global report key, such as <c>weekly:20260718</c>, unique across the application.</summary>
    public string ReportKey { get; set; }

    /// <summary>Inclusive UTC start of the completed reporting period.</summary>
    public DateTime PeriodStartUtc { get; set; }

    /// <summary>Exclusive UTC end of the completed reporting period.</summary>
    public DateTime PeriodEndUtc { get; set; }

    /// <summary>Current processing state from <see cref="UsageReportDispatchStatuses"/>.</summary>
    public string Status { get; set; } = UsageReportDispatchStatuses.Pending;

    /// <summary>Number of times a worker has claimed this report for generation or delivery.</summary>
    public int AttemptCount { get; set; }

    /// <summary>UTC lease expiry that prevents another worker from processing the same report concurrently.</summary>
    public DateTime? LeaseUntilUtc { get; set; }

    /// <summary>Last sanitized generation or Telegram delivery error; never contains bot tokens or API secrets.</summary>
    public string LastError { get; set; }

    /// <summary>Telegram message id returned after the report image is accepted by the central logger channel.</summary>
    public int? TelegramMessageId { get; set; }

    /// <summary>UTC creation timestamp for audit and retry diagnostics.</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp of the last claim, failure, or successful delivery update.</summary>
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp set only after Telegram returns a valid sent message.</summary>
    public DateTime? SentAtUtc { get; set; }
}

/// <summary>
/// Processing states used by <see cref="UsageReportDispatch"/>.
/// </summary>
public static class UsageReportDispatchStatuses
{
    /// <summary>The report is available for a worker to claim.</summary>
    public const string Pending = "pending";

    /// <summary>A worker owns a live lease and is generating the report.</summary>
    public const string Processing = "processing";

    /// <summary>Generation or a definite pre-delivery failure occurred and the report may be retried.</summary>
    public const string Failed = "failed";

    /// <summary>Telegram accepted the image and returned its message identifier.</summary>
    public const string Sent = "sent";

    /// <summary>
    /// Telegram returned a valid message, but persisting the terminal sent state encountered an error.
    /// </summary>
    /// <remarks>
    /// This state is intentionally not retryable because sending the same completed-week image again would create a
    /// duplicate channel report. Operators can use the stored Telegram message id and error for manual reconciliation.
    /// </remarks>
    public const string DeliveryRecordedWithError = "delivery_recorded_with_error";

}

/// <summary>
/// One Tehran-calendar daily bucket used by admin usage summaries and weekly charts.
/// </summary>
public sealed class UsageDailyStat
{
    /// <summary>Tehran-local Gregorian date represented by this bucket; the time component is midnight.</summary>
    public DateTime DateIran { get; init; }

    /// <summary>Number of distinct non-super-admin Telegram users interacting with any selected bot that day.</summary>
    public int UniqueUsers { get; set; }

    /// <summary>Total incoming Telegram messages and callback queries recorded for the selected bot scope that day.</summary>
    public long Interactions { get; set; }

    /// <summary>Gross value in Iranian toman of completed account purchases and renewals recorded that day.</summary>
    public long SalesToman { get; set; }

    /// <summary>Whether the expected JSONL activity file for this date was absent.</summary>
    public bool ActivityLogMissing { get; set; }

    /// <summary>Number of malformed or unusable JSONL lines skipped while building this bucket.</summary>
    public int MalformedLines { get; set; }
}

/// <summary>
/// Immutable reporting-period result assembled from daily activity logs and fulfilled tenant orders.
/// </summary>
public sealed class UsageAnalyticsReport
{
    /// <summary>
    /// Creates a report covering a contiguous sequence of completed Tehran-local days.
    /// </summary>
    /// <param name="startDateIran">Inclusive Tehran-local start date at midnight.</param>
    /// <param name="days">Ordered daily buckets; the collection must not contain the current incomplete day.</param>
    public UsageAnalyticsReport(DateTime startDateIran, IReadOnlyList<UsageDailyStat> days)
    {
        StartDateIran = startDateIran.Date;
        Days = days ?? Array.Empty<UsageDailyStat>();
    }

    /// <summary>Inclusive Tehran-local start date of the report.</summary>
    public DateTime StartDateIran { get; }

    /// <summary>Exclusive Tehran-local end date derived from the number of daily buckets.</summary>
    public DateTime EndDateIran => StartDateIran.AddDays(Days.Count);

    /// <summary>Daily buckets ordered from oldest to newest.</summary>
    public IReadOnlyList<UsageDailyStat> Days { get; }

    /// <summary>Sum of the daily distinct-user counts. It is not a cross-period distinct-user count.</summary>
    public long TotalDailyUniqueUsers => Days.Sum(x => (long)x.UniqueUsers);

    /// <summary>Total incoming messages and callbacks across the report period.</summary>
    public long TotalInteractions => Days.Sum(x => x.Interactions);

    /// <summary>Total gross completed account sales in Iranian toman.</summary>
    public long TotalSalesToman => Days.Sum(x => x.SalesToman);

    /// <summary>Number of dates for which the expected activity JSONL file was missing.</summary>
    public int MissingActivityLogDays => Days.Count(x => x.ActivityLogMissing);

    /// <summary>Total malformed JSONL lines ignored across the report period.</summary>
    public int MalformedLines => Days.Sum(x => x.MalformedLines);
}
