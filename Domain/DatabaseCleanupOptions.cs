namespace Adminbot.Domain;

/// <summary>Startup-bound retention of disposable users.db diagnostics, never financial or business-operation receipts.</summary>
/// <remarks>Missing configuration leaves the new worker off; existing seven-day inbox maintenance remains active. Full VACUUM is an explicit offline operator action.</remarks>
public sealed class DatabaseCleanupOptions
{
    /// <summary>Explicitly enables the additional diagnostic-retention worker; defaults to false for old deployments.</summary>
    public bool Enabled { get; set; }
    /// <summary>Positive days to retain disposable endpoint history and acknowledged alerts; defaults to 30.</summary>
    public int RetentionDays { get; set; } = 30;
    /// <summary>Positive days before erasing leftover terminal inbox payloads; queued/running payloads are never erased by age.</summary>
    public int PayloadRetentionDays { get; set; } = 2;
    /// <summary>Inbox deduplication days, at least seven; unresolved review and linked XUI receipts remain protected.</summary>
    public int InboxRetentionDays { get; set; } = 7;
    /// <summary>Maximum rows or incremental-vacuum pages per batch, from 1 through 10000.</summary>
    public int BatchSize { get; set; } = 2000;
    /// <summary>Maximum batches per disposable table and scan, from 1 through 100.</summary>
    public int MaxBatchesPerTable { get; set; } = 20;
    /// <summary>Enables passive WAL checkpoint and bounded incremental vacuum only when the database already supports it.</summary>
    public bool MaintenanceEnabled { get; set; } = true;
    /// <summary>Startup delay, zero through one day; defaults to ten minutes.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>Scan cadence from five minutes through thirty days; defaults to one day.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Validates explicitly enabled resource bounds without enabling missing/disabled legacy configuration.</summary>
    /// <exception cref="ArgumentException">An enabled retention interval or batch bound is unsafe.</exception>
    /// <remarks>Must run before migrations/hosting. Disabled configuration does not alter the original inbox policy.</remarks>
    /// <example><code>appConfig.DatabaseCleanup.Validate();</code></example>
    public void Validate()
    {
        if (!Enabled) return;
        if (RetentionDays is < 1 or > 36500 || PayloadRetentionDays < 1 || PayloadRetentionDays > RetentionDays ||
            InboxRetentionDays is < 7 or > 36500 || BatchSize is < 1 or > 10000 || MaxBatchesPerTable is < 1 or > 100 ||
            InitialDelay < TimeSpan.Zero || InitialDelay > TimeSpan.FromDays(1) ||
            Interval < TimeSpan.FromMinutes(5) || Interval > TimeSpan.FromDays(30))
            throw new ArgumentException("DatabaseCleanup requires positive retention (InboxRetentionDays >= 7), PayloadRetentionDays <= RetentionDays, BatchSize 1..10000, MaxBatchesPerTable 1..100, InitialDelay 0..1 day and Interval 5 minutes..30 days.");
    }
}
