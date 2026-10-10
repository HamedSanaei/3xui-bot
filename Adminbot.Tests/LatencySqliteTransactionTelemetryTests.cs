using System.Text.Json;
using Adminbot.Services.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>Exercises real SQLite transaction lifetime separately from command and exclusive handler-stage accounting.</summary>
/// <remarks>Only diagnostic clocks are controlled. EF/provider begin, SQL writes/reads, commit, rollback and disposal execute normally against an isolated in-memory SQLite database; no global service or production database is touched.</remarks>
public sealed class LatencySqliteTransactionTelemetryTests
{
    /// <summary>Shared schema parsing metadata avoids per-record serializer configuration allocation.</summary>
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);
    /// <summary>A held async business boundary belongs to transaction lifetime, not SQLite execution stages; each actual terminal endpoint emits once.</summary>
    /// <param name="asynchronous">Whether provider begin/commands/terminal boundaries use EF's async or sync APIs.</param>
    /// <param name="terminal">Fixed completed, rolled_back or disposed operation selected by the fixture.</param>
    /// <returns>A task asserting exact controlled lifetime, unchanged SQLite mutation outcome and idempotent payload-free disposal observation.</returns>
    /// <remarks>A second context without interceptors produces a global EF disposal event; the observer must ignore its unowned transaction identity.</remarks>
    [Theory]
    [InlineData(false, "completed")]
    [InlineData(true, "completed")]
    [InlineData(false, "rolled_back")]
    [InlineData(true, "rolled_back")]
    [InlineData(false, "disposed")]
    [InlineData(true, "disposed")]
    public async Task Actual_sqlite_transaction_lifetime_includes_await_but_does_not_claim_command_stage(bool asynchronous, string terminal)
    {
        var root = Path.Combine(Path.GetTempPath(), "adminbot-sqlite-latency-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new ControlledClock();
            using var telemetry = new LatencyTelemetryService(new LatencyTelemetryOptions { ChannelCapacity = 256 }, root,
                NullLogger<LatencyTelemetryService>.Instance);
            using var transactions = new LatencySqliteTransactionInterceptor(telemetry, clock);
            var commands = new LatencySqliteCommandInterceptor(telemetry);
            var options = new DbContextOptionsBuilder<DbContext>().UseSqlite("Data Source=:memory:")
                .AddInterceptors(commands, transactions, transactions.ConnectionCleanupInterceptor).Options;
            await using var context = new DbContext(options);
            await context.Database.OpenConnectionAsync();
            await context.Database.ExecuteSqlRawAsync("CREATE TABLE tx_probe (Value INTEGER NOT NULL)");
            using var scope = TelegramUpdateLatencyScope.Push(7, "owned", 21, TimeSpan.FromDays(1), null,
                timeProvider: clock, telemetry: telemetry, traceId: UpdateTelemetryTracker.TraceIdentity("owned", 21));
            await using var transaction = asynchronous
                ? await context.Database.BeginTransactionAsync()
                : context.Database.BeginTransaction();
            if (asynchronous) await context.Database.ExecuteSqlRawAsync("INSERT INTO tx_probe VALUES (1)");
            else context.Database.ExecuteSqlRaw("INSERT INTO tx_probe VALUES (1)");

            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var heldBusinessOperation = HoldBusinessOperationAsync(scope, release.Task);
            Assert.False(heldBusinessOperation.IsCompleted);
            Assert.Equal("external_http", scope.CurrentStageName);
            clock.Advance(TimeSpan.FromMilliseconds(350));
            release.SetResult();
            await heldBusinessOperation;
            if (terminal == "completed")
            {
                if (asynchronous) await transaction.CommitAsync();
                else transaction.Commit();
            }
            else if (terminal == "rolled_back")
            {
                if (asynchronous) await transaction.RollbackAsync();
                else transaction.Rollback();
            }
            if (asynchronous) await transaction.DisposeAsync();
            else transaction.Dispose();
            // Disposal after commit/rollback and repeated disposal must not add a second lifetime observation.
            await transaction.DisposeAsync();
            scope.Dispose();
            var snapshot = scope.CaptureTelemetry();
            var persistedRows = await context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM tx_probe").SingleAsync();
            Assert.Equal(terminal == "completed" ? 1 : 0, persistedRows);

            var unownedOptions = new DbContextOptionsBuilder<DbContext>().UseSqlite("Data Source=:memory:").Options;
            await using (var unownedContext = new DbContext(unownedOptions))
            {
                await using var unownedTransaction = await unownedContext.Database.BeginTransactionAsync();
            }
            await telemetry.StartAsync(default);
            await telemetry.StopAsync(default);
            var json = string.Join('\n', Directory.GetFiles(telemetry.StorageDirectory, "latency-*.jsonl").Select(File.ReadAllText));
            var events = json.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonSerializer.Deserialize<LatencyTelemetryEvent>(line, WireOptions)!).ToArray();
            var lifetime = Assert.Single(events, item => item.Operation == "transaction_lifetime");
            Assert.Equal("sqlite_transaction_completed", lifetime.EventType);
            Assert.Equal("sqlite_transaction", lifetime.Stage);
            Assert.Equal(terminal, lifetime.Outcome);
            Assert.Equal(350d, lifetime.DurationMs);
            Assert.Equal("inclusive_transaction_lifetime_not_handler_stage", lifetime.TimingQuality);
            Assert.Equal(scope.TraceId, lifetime.TraceId);
            Assert.Equal("owned", lifetime.BotId);
            Assert.Equal(21, lifetime.UpdateId);
            Assert.Equal(7, lifetime.Sequence);
            Assert.Equal(350d, snapshot.HandlerMs);
            Assert.Equal(350d, snapshot.StageMs.GetValueOrDefault("external_http"));
            Assert.Equal(0d, snapshot.StageMs.GetValueOrDefault("sqlite_read"));
            Assert.Equal(0d, snapshot.StageMs.GetValueOrDefault("sqlite_write"));
            Assert.DoesNotContain("sqlite_transaction", snapshot.StageMs.Keys);
            Assert.Contains(events, item => item.TraceId == scope.TraceId && item.Operation == "transaction_begin");
            Assert.Contains(events, item => item.TraceId == scope.TraceId && item.Stage == "sqlite_write" && item.Operation == "sqlite_write");
            Assert.DoesNotContain("tx_probe", json, StringComparison.Ordinal);
            Assert.Equal(0, telemetry.DroppedEvents);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Independent worker categories survive asynchronous SQLite boundaries and remain visible in a filtered bot report.</summary>
    /// <returns>A task verifying real committed rows, cross-worker isolation and global diagnostic visibility.</returns>
    /// <remarks>No global writer binding is installed. Production interceptors aggregate actual healthy SQLite work
    /// before enqueue while preserving async-local ownership. A diagnostic clock makes transaction lifetimes deterministically fast; committed database rows and categories remain independent.</remarks>
    [Fact]
    public async Task Concurrent_background_sqlite_workers_keep_categories_and_global_report_visibility()
    {
        var root = Path.Combine(Path.GetTempPath(), "adminbot-worker-latency-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var telemetry = new LatencyTelemetryService(new LatencyTelemetryOptions { SlowOperationMs = 300000 }, root, NullLogger<LatencyTelemetryService>.Instance);
            var commands = new LatencySqliteCommandInterceptor(telemetry);
            using var transactions = new LatencySqliteTransactionInterceptor(telemetry, new ControlledClock());
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var arrivals = 0;
            Func<Task> barrier = () =>
            {
                if (Interlocked.Increment(ref arrivals) == 2) ready.SetResult();
                return ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            };
            await Task.WhenAll(RunBackgroundWorkerAsync(LatencySqliteOperationCategory.PaymentSettlementNotification, commands, transactions, barrier),
                RunBackgroundWorkerAsync(LatencySqliteOperationCategory.XuiRenewalRecovery, commands, transactions, barrier));
            Assert.Null(TelegramUpdateLatencyScope.Current);
            await telemetry.StartAsync(default);
            await telemetry.StopAsync(default);
            using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            var result = await LatencyTelemetryReportCli.RunAsync(
                ["telemetry-report", "--directory", telemetry.StorageDirectory, "--bot", "GozargahNetwork_Bot"], output, default);
            Assert.Equal(0, result);
            var report = output.ToString();
            Assert.Contains("global/payment_settlement_notification/sqlite_background_aggregate/transaction_lifetime", report);
            Assert.Contains("global/xui_renewal_recovery/sqlite_background_aggregate/transaction_lifetime", report);
            Assert.DoesNotContain("bot=", report, StringComparison.Ordinal);
            Assert.DoesNotContain("worker_probe", report);
            var rows = Directory.EnumerateFiles(telemetry.StorageDirectory, "*.jsonl").SelectMany(File.ReadLines)
                .Select(line => JsonSerializer.Deserialize<LatencyTelemetryEvent>(line, WireOptions)!).ToArray();
            Assert.DoesNotContain(rows, row => row.EventType is "sqlite_operation_completed" or "sqlite_transaction_completed");
            var lifetimes = rows.Where(row => row.Operation == "transaction_lifetime").ToArray();
            Assert.Equal(2, lifetimes.Length);
            Assert.All(lifetimes, row =>
            {
                Assert.Null(row.BotId); Assert.Null(row.TraceId);
                Assert.Equal(1, row.ObservationCount);
                Assert.Equal(1, row.DurationBucketCounts!.Sum());
            });
            Assert.Equal(0, telemetry.DroppedEvents);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    /// <summary>Commits one real SQLite mutation while another categorized worker holds its own independent transaction.</summary>
    /// <param name="category">Closed background operation category, never a tenant/customer identifier.</param>
    /// <param name="commands">Shared production command diagnostics interceptor.</param>
    /// <param name="transactions">Shared production transaction diagnostics interceptor.</param>
    /// <param name="barrier">Bounded asynchronous fixture barrier proving interleaved ownership.</param>
    /// <returns>A task completing after the committed SQLite row invariant is observed.</returns>
    /// <remarks>The category is installed inside this async invocation, preserving caller and sibling ExecutionContexts.</remarks>
    private static async Task RunBackgroundWorkerAsync(LatencySqliteOperationCategory category,
        LatencySqliteCommandInterceptor commands, LatencySqliteTransactionInterceptor transactions, Func<Task> barrier)
    {
        using var owner = LatencySqliteOperationScope.Push(category);
        var options = new DbContextOptionsBuilder<DbContext>().UseSqlite("Data Source=:memory:")
            .AddInterceptors(commands, transactions, transactions.ConnectionCleanupInterceptor).Options;
        await using var db = new DbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE worker_probe (Value INTEGER NOT NULL)");
        await using var transaction = await db.Database.BeginTransactionAsync();
        await barrier();
        await db.Database.ExecuteSqlRawAsync("INSERT INTO worker_probe VALUES (1)");
        await transaction.CommitAsync();
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM worker_probe").SingleAsync());
    }

    /// <summary>Holds an actual incomplete awaited boundary while a real provider transaction remains open.</summary>
    /// <param name="scope">Existing exclusive stage scope, not a replacement tracing system.</param>
    /// <param name="release">Fixture-owned asynchronous barrier, initially incomplete.</param>
    /// <returns>A task completing only when the fixture releases the barrier.</returns>
    /// <remarks>The external_http category represents waited business/network wall time, never CPU or database execution.</remarks>
    private static async Task HoldBusinessOperationAsync(TelegramUpdateLatencyScope scope, Task release)
    {
        using var boundary = scope.Measure(TelegramUpdateStage.ExternalHttp);
        await release;
    }

    /// <summary>Controls only diagnostic UTC and monotonic timestamps, not actual SQLite provider execution.</summary>
    private sealed class ControlledClock : TimeProvider
    {
        /// <summary>Fixture-local monotonic ticks, safe across the barrier continuation.</summary>
        private long _ticks;
        /// <inheritdoc />
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        /// <inheritdoc />
        public override long GetTimestamp() => Volatile.Read(ref _ticks);
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());
        /// <summary>Advances lifetime and scope clocks by an exact elapsed duration.</summary>
        /// <param name="duration">Nonnegative controlled elapsed interval.</param>
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }
}
