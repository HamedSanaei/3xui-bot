using Adminbot.Domain;
using Adminbot.Services.TelegramEndpoints;
using Microsoft.EntityFrameworkCore;

/// <summary>Bounded users.db diagnostic retention and conservative SQLite maintenance.</summary>
/// <remarks>Never deletes financial, order, wallet, referral, trial, XUI operation, bot-state or reminder-cycle tables.
/// Existing feature-specific outbox retention remains authoritative. Full VACUUM and auto-vacuum conversion are offline operator actions.</remarks>
public sealed class DatabaseCleanupService : BackgroundService
{
    /// <summary>Immutable-options factory; each batch owns its context and releases the writer before the next batch.</summary>
    private readonly UserDbContextFactory _factory;
    /// <summary>Existing inbox retention owner, shared with hourly scheduler maintenance.</summary>
    private readonly TelegramUpdateInboxStore _inbox;
    /// <summary>Startup configuration, enabled only by an explicit DatabaseCleanup section.</summary>
    private readonly DatabaseCleanupOptions _options;
    /// <summary>Standard application logger; routine scans stay Debug to avoid Telegram noise.</summary>
    private readonly ILogger<DatabaseCleanupService> _logger;
    /// <summary>Serializes manual/test and hosted scan entry without overlapping cleanup lifetimes.</summary>
    private readonly SemaphoreSlim _scan = new(1, 1);

    /// <summary>Creates a cleanup worker without reading, pruning or compacting any database.</summary>
    /// <param name="factory">Required users.db factory, never the credentials database.</param>
    /// <param name="inbox">Existing bot-scoped inbox owner with protected receipt predicates.</param>
    /// <param name="configuration">Startup application configuration; no configuration file is rewritten.</param>
    /// <param name="logger">Standard local/operational logger; no secrets or SQL values are logged.</param>
    /// <remarks>Hosting begins only after application migrations. No schema changes or financial workers are invoked.</remarks>
    /// <exception cref="ArgumentException">Enabled configuration bounds are invalid.</exception>
    public DatabaseCleanupService(UserDbContextFactory factory, TelegramUpdateInboxStore inbox,
        AppConfig configuration, ILogger<DatabaseCleanupService> logger)
    {
        _factory = factory; _inbox = inbox; _options = configuration.DatabaseCleanup ?? new(); _logger = logger;
        _options.Validate();
    }

    /// <summary>Runs delayed, configuration-driven cleanup independently of Telegram handlers.</summary>
    /// <param name="stoppingToken">Hosted lifetime; cancellation stops scheduling and future batches.</param>
    /// <returns>A task completing when disabled or the host shuts down.</returns>
    /// <remarks>Failed scans retain durable data and wait a full configured interval; no rapid retry or external mutation occurs.</remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;
        try
        {
            await Task.Delay(_options.InitialDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var counts = await RunOnceAsync(stoppingToken);
                    _logger.LogDebug("users.db disposable retention finished. Inbox={Inbox} Payloads={Payloads} History={History} Alerts={Alerts}",
                        counts["inbox"], counts["payloads"], counts["history"], counts["alerts"]);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning("users.db diagnostic cleanup deferred. ExceptionType={ExceptionType}", ex.GetType().Name);
                }
                await Task.Delay(_options.Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>Prunes only allowlisted disposable records, preserving permanent deduplication and current-operation evidence.</summary>
    /// <param name="token">Local scan cancellation; no external API or wallet mutation occurs.</param>
    /// <returns>Payload-free removal counts; disabled/concurrent scans return zero counts without writes.</returns>
    /// <remarks>Each table has independent batch limits. Active/unknown/uncertain states never match deletion. Endpoint
    /// history keeps the latest twenty receipts per exact bot identity and every receipt of its current operation.</remarks>
    /// <example><code>var counts = await cleanup.RunOnceAsync(shutdownToken);</code></example>
    public async Task<IReadOnlyDictionary<string, int>> RunOnceAsync(CancellationToken token)
    {
        var counts = new Dictionary<string, int> { ["inbox"] = 0, ["payloads"] = 0, ["history"] = 0, ["alerts"] = 0 };
        if (!_options.Enabled || !await _scan.WaitAsync(0, token)) return counts;
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-_options.RetentionDays);
            var payloadCutoff = DateTime.UtcNow.AddDays(-_options.PayloadRetentionDays);
            counts["payloads"] = await BatchesAsync(async (db, ct) =>
            {
                var query = db.TelegramUpdateInbox.Where(x => x.Payload != null && x.CompletedAtUtc < payloadCutoff &&
                    (x.Status == "completed" || x.Status == "completed_with_error" || x.Status == "completed_with_review"));
                var ids = await query.OrderBy(x => x.Sequence).Select(x => x.Sequence).Take(_options.BatchSize).ToArrayAsync(ct);
                return ids.Length == 0 ? 0 : await query.Where(x => ids.Contains(x.Sequence))
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Payload, (string)null), ct);
            }, token);
            counts["inbox"] = await _inbox.PruneAsync(token);
            counts["alerts"] = await BatchesAsync(async (db, ct) =>
            {
                var query = db.TelegramEndpointAlerts.Where(x => x.Status == TelegramEndpointAlertStatus.Delivered &&
                    x.DeliveredAtUtc < cutoff && x.ClaimId == null && x.LeaseUntilUtc == null &&
                    db.TelegramEndpointAlertReceipts.Any(r => r.IncidentKey == x.IncidentKey));
                var ids = await query.OrderBy(x => x.Id).Select(x => x.Id).Take(_options.BatchSize).ToArrayAsync(ct);
                return ids.Length == 0 ? 0 : await query.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(ct);
            }, token);
            counts["history"] = await BatchesAsync(async (db, ct) =>
            {
                var query = db.TelegramEndpointHistory.Where(x => x.CreatedAtUtc < cutoff &&
                    !db.TelegramEndpointStates.Any(s => s.BotId == x.BotId && s.TelegramBotId == x.TelegramBotId && s.OperationId == x.OperationId) &&
                    db.TelegramEndpointHistory.Where(h => h.BotId == x.BotId && h.TelegramBotId == x.TelegramBotId && h.Id > x.Id)
                        .OrderBy(h => h.Id).Skip(19).Any());
                var ids = await query.OrderBy(x => x.Id).Select(x => x.Id).Take(_options.BatchSize).ToArrayAsync(ct);
                return ids.Length == 0 ? 0 : await query.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(ct);
            }, token);
            if (_options.MaintenanceEnabled) await MaintainAsync(token);
            return counts;
        }
        finally { _scan.Release(); }
    }

    /// <summary>Releases SQLite context/write ownership between bounded batches.</summary>
    /// <param name="batch">Safe allowlisted read-then-conditional mutation; no external or financial action is allowed.</param>
    /// <param name="token">Cancellation checked before every batch.</param>
    /// <returns>Affected rows capped by BatchSize times MaxBatchesPerTable.</returns>
    /// <remarks>An empty batch takes no write turn. SQLite alone arbitrates concurrent instances.</remarks>
    /// <example><code>var removed = await BatchesAsync(batch, token); // The delegate selects bounded keys and rechecks its predicates.</code></example>
    private async Task<int> BatchesAsync(Func<UserDbContext, CancellationToken, Task<int>> batch, CancellationToken token)
    {
        var total = 0;
        for (var i = 0; i < _options.MaxBatchesPerTable; i++)
        {
            var count = await SqliteOperation.RunAsync(async ct =>
            {
                await using var db = _factory.CreateDbContext();
                return await batch(db, ct);
            }, token);
            total += count;
            if (count == 0) break;
            await Task.Yield();
        }
        return total;
    }

    /// <summary>Checkpoints without waiting for readers and reclaims a bounded number of already-free incremental-vacuum pages.</summary>
    /// <param name="token">Host cancellation checked before local maintenance commands.</param>
    /// <returns>A task completing after local maintenance; busy readers remain active and WAL files are never deleted manually.</returns>
    /// <remarks>Does not run full VACUUM, change journal/durability settings, or convert auto_vacuum on a live database.
    /// Databases with auto_vacuum=NONE require one explicit offline compaction; see docs/database-cleanup.md.</remarks>
    private async Task MaintainAsync(CancellationToken token)
    {
        await using var db = _factory.CreateDbContext();
        await db.Database.OpenConnectionAsync(token);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandTimeout = 2;
        command.CommandText = "PRAGMA auto_vacuum;";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) == 2)
        {
            command.CommandText = $"PRAGMA incremental_vacuum({_options.BatchSize});";
            await using var vacuum = await command.ExecuteReaderAsync(token);
            while (await vacuum.ReadAsync(token)) { }
        }
        // Checkpoint after reclamation so its smaller logical page count can reach the main file in
        // this scan. PASSIVE never forces/truncates a WAL that an active reader still needs.
        command.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) { }
    }
}
