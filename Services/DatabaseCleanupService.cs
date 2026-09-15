using Adminbot.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Scheduled host that runs the retention and SQLite maintenance pass once per day.
/// </summary>
/// <remarks>
/// <para>
/// The service is intentionally thin: it owns the schedule, the configuration snapshot for that schedule, and the
/// error containment, while <see cref="DatabaseCleanupRunner" /> owns every retention rule. That split keeps the
/// policy testable against a temporary database without starting a host.
/// </para>
/// <para>
/// The first pass runs after <see cref="DatabaseCleanupOptions.InitialDelay" /> so it never competes with host startup,
/// migration, or the first burst of queued Telegram updates. A failed pass logs its error and waits for the next
/// interval rather than retrying in a tight loop; the following pass re-reads configuration and is idempotent.
/// </para>
/// <para>
/// A regression guard: every pass runs inside its own dependency-injection scope because the website outbox compactor
/// is a scoped service. The scope is disposed before the pass returns, so no scoped dependency outlives the pass.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Registered in Program.cs; no manual start is required.
/// services.AddSingleton&lt;DatabaseCleanupRunner&gt;();
/// services.AddHostedService&lt;DatabaseCleanupService&gt;();
/// </code>
/// </example>
public sealed class DatabaseCleanupService : BackgroundService
{
    /// <summary>
    /// Smallest accepted interval between passes, so a misconfigured value cannot turn maintenance into a hot loop.
    /// </summary>
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);

    private readonly DatabaseCleanupRunner _runner;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DatabaseCleanupService> _logger;

    /// <summary>
    /// Creates the scheduled cleanup host.
    /// </summary>
    /// <param name="runner">Retention runner that performs the per-table work.</param>
    /// <param name="scopeFactory">
    /// Scope factory used to resolve the scoped website outbox compactor for the duration of one pass. The runner
    /// itself must not capture a scoped service.
    /// </param>
    /// <param name="configuration">Application configuration read at startup and again before each pass.</param>
    /// <param name="logger">Structured logger for schedule, pass outcome, and retained error reasons.</param>
    /// <exception cref="ArgumentNullException">Thrown when any dependency is null.</exception>
    public DatabaseCleanupService(
        DatabaseCleanupRunner runner,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<DatabaseCleanupService> logger)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Runs the delayed first cleanup pass and then repeats it on the configured interval until shutdown.
    /// </summary>
    /// <param name="stoppingToken">Host shutdown token; it also cancels an in-flight pass between tables.</param>
    /// <returns>A task that completes when the host stops or when cleanup is disabled by configuration.</returns>
    /// <remarks>
    /// <para>
    /// When <c>DatabaseCleanup:Enabled</c> is false the method logs one message and returns immediately, so the host
    /// keeps running with no maintenance thread. When the configuration is invalid the same happens with an error log,
    /// which keeps a bad retention value from crash-looping the entire bot.
    /// </para>
    /// <para>
    /// Side effects: deletes terminal rows, compacts stored JSON payloads, and may rewrite <c>users.db</c>. No Telegram
    /// message, panel request, or payment call is ever made by this service or by the runner it calls.
    /// </para>
    /// <example>
    /// <code>
    /// // The host calls this automatically; tests call DatabaseCleanupRunner.RunOnceAsync instead.
    /// await service.StartAsync(CancellationToken.None);
    /// </code>
    /// </example>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = ReadOptions();
        if (options == null)
            return;

        _logger.LogInformation(
            "Database cleanup scheduled. retentionDays={RetentionDays}, payloadRetentionDays={PayloadRetentionDays}, inboxRetentionDays={InboxRetentionDays}, intervalHours={IntervalHours}",
            options.RetentionDays,
            options.PayloadRetentionDays,
            options.InboxRetentionDays,
            options.Interval.TotalHours);

        var delay = options.InitialDelay < TimeSpan.Zero ? TimeSpan.Zero : options.InitialDelay;
        if (delay > TimeSpan.Zero && !await DelayAsync(delay, stoppingToken))
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A per-pass scope keeps the scoped website outbox compactor alive exactly for the pass and releases it
                // before the interval delay, so a long-running host never accumulates scoped instances.
                using var scope = _scopeFactory.CreateScope();
                var compactor = scope.ServiceProvider.GetService<ITerminalOutboxCompactor>();
                await _runner.RunOnceAsync(DateTime.UtcNow, compactor, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The next interval retries. Falling back to a longer wait is unnecessary because the interval is
                // already a day, and a crash here would take the whole bot down with it for a housekeeping problem.
                _logger.LogError(ex, "Database cleanup pass failed. nextPassInHours={NextPassInHours}", options.Interval.TotalHours);
            }

            var nextOptions = ReadOptions();
            if (nextOptions == null)
                return;
            options = nextOptions;
            if (stoppingToken.IsCancellationRequested)
                return;
            if (!await DelayAsync(options.Interval, stoppingToken))
                return;
        }
    }

    /// <summary>
    /// Reads and validates the cleanup configuration for one scheduling decision.
    /// </summary>
    /// <returns>
    /// The validated options, or null when cleanup is disabled or misconfigured; both cases are logged before
    /// returning so the reason is visible in the journal.
    /// </returns>
    /// <remarks>
    /// Configuration is re-read before each pass and before each delay, so raising the retention window or disabling
    /// the feature takes effect without restarting the host.
    /// </remarks>
    private DatabaseCleanupOptions ReadOptions()
    {
        DatabaseCleanupOptions options;
        try
        {
            options = DatabaseCleanupOptions.FromConfiguration(_configuration);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Database cleanup is not running because its configuration is invalid.");
            return null;
        }

        if (options.Enabled)
            return options;

        _logger.LogInformation("Database cleanup is disabled by configuration. No retention work will run.");
        return null;
    }

    /// <summary>
    /// Waits for the configured interval while treating shutdown as a normal end of the schedule.
    /// </summary>
    /// <param name="delay">
    /// Requested wait. Values below <see cref="MinimumInterval" /> are raised to it so a misconfigured interval cannot
    /// create a maintenance hot loop.
    /// </param>
    /// <param name="stoppingToken">Host shutdown token.</param>
    /// <returns>True when the delay elapsed; false when the host is stopping and the scheduler should exit.</returns>
    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        var effective = delay < MinimumInterval ? MinimumInterval : delay;
        try
        {
            await Task.Delay(effective, stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
