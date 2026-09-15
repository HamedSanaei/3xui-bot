using Microsoft.Extensions.Configuration;

namespace Adminbot.Domain
{
    /// <summary>
    /// Configuration for the daily SQLite retention and maintenance pass that keeps <c>users.db</c> from growing
    /// without bound as Telegram updates, panel operations, and payment callbacks are recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The values are bound from the <c>DatabaseCleanup</c> configuration section. Configuration binding is
    /// case-insensitive in this application, so the documented JSON shape is:
    /// </para>
    /// <code>
    /// "DatabaseCleanup": { "Enabled": true, "RetentionDays": 30 }
    /// </code>
    /// <para>
    /// Every "days" value is a positive whole number counted back from the current UTC time. A retention value that
    /// is not positive is a configuration error: the cleanup pass refuses to run rather than deleting recent data.
    /// </para>
    /// <para>
    /// Row deletion and payload compaction are separate windows on purpose. Operation rows are audit evidence and are
    /// kept for <see cref="RetentionDays" />, while the large JSON blobs those rows carry are only needed while the
    /// operation can still be recovered, so they are compacted after <see cref="PayloadRetentionDays" />.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var options = DatabaseCleanupOptions.FromConfiguration(configuration);
    /// if (options.Enabled) await runner.RunOnceAsync(DateTime.UtcNow, compactor, token);
    /// </code>
    /// </example>
    public sealed class DatabaseCleanupOptions
    {
        /// <summary>Configuration section name that binds this model.</summary>
        public const string SectionName = "DatabaseCleanup";

        /// <summary>
        /// Gets or sets a value indicating whether the scheduled cleanup pass runs.
        /// </summary>
        /// <remarks>
        /// The default is <c>true</c>. When <c>false</c>, the hosted service records one startup message and performs
        /// no deletion, compaction, or SQLite maintenance. Per-feature retention in the dashboard and website outbox
        /// keeps working independently of this switch.
        /// </remarks>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the number of days terminal operation and outbox rows are retained before deletion.
        /// </summary>
        /// <remarks>
        /// Must be positive; the default is 30. Only rows whose operation has provably completed are candidates, so an
        /// active, unresolved, ambiguous, or manual-review row is never deleted at any age.
        /// </remarks>
        public int RetentionDays { get; set; } = 30;

        /// <summary>
        /// Gets or sets the number of days terminal rows keep their large JSON payload columns.
        /// </summary>
        /// <remarks>
        /// Must be positive; the default is 2. Compaction replaces stored request, response, IPN, client, snapshot, and
        /// mutation payload blobs with <c>null</c> and keeps the row's identifiers, statuses, amounts, and timestamps.
        /// It therefore never removes audit evidence that an operator or the finance team can act on, only the bulky
        /// copies of provider or panel documents that are no longer reachable by any recovery path.
        /// </remarks>
        public int PayloadRetentionDays { get; set; } = 2;

        /// <summary>
        /// Gets or sets the number of days terminal Telegram update receipts are retained for deduplication.
        /// </summary>
        /// <remarks>
        /// Must be positive; the default is 7, which matches the documented Bot API redelivery window. Queued,
        /// running, and uncertain receipts are never candidates at any age.
        /// </remarks>
        public int InboxRetentionDays { get; set; } = 7;

        /// <summary>
        /// Gets or sets the number of rows one cleanup statement may touch.
        /// </summary>
        /// <remarks>
        /// Must be positive; the default is 2000. Batching keeps the write lock on <c>users.db</c> short so Telegram
        /// handlers never wait behind a mass deletion.
        /// </remarks>
        public int BatchSize { get; set; } = 2000;

        /// <summary>
        /// Gets or sets the maximum number of batches one table may consume per pass.
        /// </summary>
        /// <remarks>
        /// Must be positive; the default is 20, so at most 40 000 rows are removed from a single table per day. A
        /// larger backlog is drained over consecutive passes instead of blocking the database in one long transaction.
        /// </remarks>
        public int MaxBatchesPerTable { get; set; } = 20;

        /// <summary>
        /// Gets or sets the delay before the first cleanup pass after startup.
        /// </summary>
        /// <remarks>
        /// The default is 10 minutes. The delay keeps the first pass away from host startup, migration, and the
        /// initial burst of queued Telegram updates. Negative values are treated as zero.
        /// </remarks>
        public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Gets or sets the delay between cleanup passes.
        /// </summary>
        /// <remarks>
        /// The default is one day. Values below one minute are raised to one minute so a misconfigured interval cannot
        /// turn maintenance into a hot loop.
        /// </remarks>
        public TimeSpan Interval { get; set; } = TimeSpan.FromDays(1);

        /// <summary>
        /// Gets or sets a value indicating whether <c>VACUUM</c> and <c>ANALYZE</c> run after a pass that changed rows.
        /// </summary>
        /// <remarks>
        /// The default is <c>true</c>. VACUUM returns the pages freed by the deletions to the operating system and
        /// rewrites the file compactly, which is what actually shrinks an oversized database. It needs free disk space
        /// equal to the database size and an exclusive lock, so it only runs when at least one row was deleted or
        /// compacted, and a busy result is logged as deferred rather than retried inside the same pass.
        /// </remarks>
        public bool MaintenanceEnabled { get; set; } = true;

        /// <summary>
        /// Reads and validates the cleanup options from configuration.
        /// </summary>
        /// <param name="configuration">
        /// Application configuration containing the optional <c>DatabaseCleanup</c> section. A missing section yields
        /// the documented defaults, which means the cleanup pass runs daily with a 30-day retention window.
        /// </param>
        /// <returns>
        /// A validated options instance. The returned object is a snapshot; later configuration reloads are picked up
        /// by the next call, not by the already returned instance.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="configuration" /> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <see cref="Enabled" /> is true and any configured day, batch, or interval value is out of range.
        /// Startup fails loudly instead of silently retaining data forever or deleting too much.
        /// </exception>
        /// <remarks>
        /// Validation is intentionally skipped when <see cref="Enabled" /> is false, so an operator can park the
        /// feature with a placeholder value without blocking application startup.
        /// </remarks>
        /// <example>
        /// <code>
        /// var options = DatabaseCleanupOptions.FromConfiguration(configuration);
        /// _logger.LogInformation("Database cleanup window. retentionDays={RetentionDays}", options.RetentionDays);
        /// </code>
        /// </example>
        public static DatabaseCleanupOptions FromConfiguration(IConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            var section = configuration.GetSection(SectionName);
            var options = section.Exists() ? section.Get<DatabaseCleanupOptions>() ?? new DatabaseCleanupOptions() : new DatabaseCleanupOptions();
            options.Validate();
            return options;
        }

        /// <summary>
        /// Validates every retention window and batch limit before any database statement is issued.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a value is out of range while <see cref="Enabled" /> is true.
        /// </exception>
        /// <remarks>
        /// The largest permitted retention window is 10 years. Longer windows are rejected because they almost always
        /// indicate a units mistake such as seconds supplied where days are expected.
        /// </remarks>
        public void Validate()
        {
            if (!Enabled)
                return;
            if (RetentionDays <= 0 || RetentionDays > 3650)
                throw new InvalidOperationException("DatabaseCleanup:RetentionDays must be between 1 and 3650.");
            if (PayloadRetentionDays <= 0 || PayloadRetentionDays > 3650)
                throw new InvalidOperationException("DatabaseCleanup:PayloadRetentionDays must be between 1 and 3650.");
            if (InboxRetentionDays <= 0 || InboxRetentionDays > 3650)
                throw new InvalidOperationException("DatabaseCleanup:InboxRetentionDays must be between 1 and 3650.");
            if (BatchSize <= 0 || BatchSize > 100000)
                throw new InvalidOperationException("DatabaseCleanup:BatchSize must be between 1 and 100000.");
            if (MaxBatchesPerTable <= 0 || MaxBatchesPerTable > 1000)
                throw new InvalidOperationException("DatabaseCleanup:MaxBatchesPerTable must be between 1 and 1000.");
        }
    }
}
