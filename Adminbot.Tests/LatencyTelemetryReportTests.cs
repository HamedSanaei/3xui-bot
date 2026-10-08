using Adminbot.Services.Telemetry;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Xunit;

/// <summary>Exercises the actual non-serving JSONL analyzer, including loss visibility and hostile input projection.</summary>
/// <remarks>Every test uses isolated temporary files; no host, configuration, Telegram client, or production storage is constructed.</remarks>
public sealed class LatencyTelemetryReportTests
{
    /// <summary>Uses the same camelCase v1 wire shape as the production writer.</summary>
    private static readonly JsonSerializerOptions WireOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Protects all six report sections and prevents HTTP/decorator/SDK records from inflating update counts.</summary>
    /// <returns>A task completing after a real read-only scan.</returns>
    [Fact]
    public async Task Versioned_records_produce_all_reports_without_double_counting()
    {
        using var archive = new Archive();
        var now = DateTime.UtcNow.AddMinutes(-1);
        var trace = Guid.NewGuid().ToString("N");
        var bot = "GozargahNetwork_Bot";
        await archive.WriteAsync(
            Row("telegram_update_received", now, bot) with { TraceId = trace },
            Row("telegram_update_completed", now.AddSeconds(1), bot) with
            {
                TraceId = trace, ApplicationMs = 9000, HandlerMs = 4000, QueueWaitMs = 5000,
                StageMs = new() { ["telegram_send"] = 2000, ["sqlite_read"] = 1000 },
                UnattributedHandlerMs = 1000, FirstResponseAttemptMs = 500,
                FirstResponseCompletedMs = 2500, FirstResponseAcknowledgedMs = 2500,
                TimingQuality = "measured_wall_time_not_cpu"
            },
            Row("telegram_request_completed", now, bot) with { Method = "sendMessage", DurationMs = 2000 },
            Row("telegram_api_request_completed", now, bot) with { Method = "sendMessage", DurationMs = 2001 },
            Row("telegram_foreground_request_completed", now, bot) with { Outcome = "foreground_budget_expired", TimeoutCategory = "foreground_budget" },
            Row("telegram_poll_failed", now, bot) with { FailureClassification = "http_5xx", HttpStatusCode = 502, DegradedSinceUtc = now },
            Row("telegram_poll_failed", now.AddSeconds(1), bot) with { FailureClassification = "dns", DegradedSinceUtc = now },
            Row("telegram_poll_recovered", now.AddSeconds(3), bot) with { DegradedSinceUtc = now, RecoveryMs = 3000, ConsecutiveFailures = 2 },
            Row("telegram_poll_completed", now.AddSeconds(3), bot),
            Row("sqlite_operation_completed", now, bot) with { Stage = "sqlite_write", DurationMs = 20, SqliteErrorCode = 5, Outcome = "failed" },
            Row("sqlite_busy_retry", now, bot) with { Category = "inbox", BusyWaitMs = 100, Outcome = "retrying" });
        var (code, text) = await archive.ReportAsync("--hours", "24", "--bot", bot);
        Assert.Equal(0, code);
        Assert.Contains("bot=GozargahNetwork_Bot updates=1 count=1", text);
        Assert.Contains("slow=1 foregroundTimeouts=1 pollFailures=2 bottleneck=queue_wait", text);
        Assert.Contains("trace=" + trace, text);
        Assert.Contains("telegram_send=2000", text);
        Assert.Contains("firstAcknowledged=2500", text);
        Assert.Contains("bot/endpoint/method=GozargahNetwork_Bot/unknown/sendMessage/headers events=1", text);
        Assert.Contains("bot/endpoint/method=GozargahNetwork_Bot/unknown/sendMessage/sdk_validated events=1", text);
        Assert.Contains("failures=2 recoveryMs=3000", text);
        Assert.Contains("sequence=http_5xx:HTTP502>dns", text);
        Assert.Contains("busyIncidents=1", text);
        Assert.Contains("retryEvents=1 busyWaitMs=100", text);
        Assert.Contains("incompleteTracesObserved=0", text);
        Assert.Contains("6. Data quality", text);
    }

    /// <summary>Malformed, future-schema, oversized, and truncated records must not terminate or poison subsequent records.</summary>
    /// <returns>A task completing after the scanner drains the hostile lines.</returns>
    [Fact]
    public async Task Bad_lines_are_counted_and_scanning_recovers_at_the_next_newline()
    {
        using var archive = new Archive();
        var row = Row("telegram_update_completed", DateTime.UtcNow.AddMinutes(-1), "tenant-123") with { ApplicationMs = 1 };
        var json = JsonSerializer.Serialize(row, WireOptions);
        await File.WriteAllTextAsync(archive.PathFor("001.jsonl"),
            "{broken\n" + JsonSerializer.Serialize(row with { SchemaVersion = 999 }, WireOptions) + "\n" +
            new string('x', 256 * 1024 + 1) + "\n" + json + "\n" + "{unfinished", new UTF8Encoding(false));
        var (code, text) = await archive.ReportAsync();
        Assert.Equal(0, code);
        Assert.Contains("updates=1 count=1", text);
        Assert.Contains("malformed=1 truncated=1 oversized=1 unknownSchemas=1", text);
        Assert.Contains("missingStageSummaries=1", text);
    }

    /// <summary>Unknown fields and labels containing credentials, URLs, controls, SQL, or customer contents must never be echoed.</summary>
    /// <returns>A task completing after safe field projection.</returns>
    [Fact]
    public async Task Hostile_fields_are_withheld_in_every_report_section()
    {
        using var archive = new Archive();
        const string secret = "PASSWORD:do-not-print\u001b[31m https://api.example/bot123:SECRET/customer";
        var stamp = DateTime.UtcNow.AddMinutes(-1).ToString("O", CultureInfo.InvariantCulture);
        string Malicious(string type) => JsonSerializer.Serialize(new
        {
            schemaVersion = 1, timestampUtc = stamp, eventType = type,
            botId = secret, traceId = secret, method = secret, category = secret, operation = secret,
            outcome = secret, failureClassification = secret, timingQuality = secret,
            body = secret, password = secret, sql = secret, exceptionMessage = secret,
            endpointType = secret, migrationState = secret, failoverTrigger = secret,
            rawState = secret, actorTelegramUserId = secret, telegramUserId = secret, endpointUrl = secret,
            applicationMs = 6000, durationMs = 1, stageMs = new Dictionary<string, double> { [secret] = 6000 }
        });
        await File.WriteAllTextAsync(archive.PathFor("001.jsonl"), string.Join('\n', new[]
        {
            Malicious("telegram_update_completed"), Malicious("telegram_request_completed"),
            Malicious("telegram_poll_failed"), Malicious("sqlite_operation_completed"),
            Malicious("telegram_endpoint_migration"), Malicious("telegram_endpoint_outage"), Malicious(secret)
        }) + "\n", new UTF8Encoding(false));
        var (code, text) = await archive.ReportAsync();
        Assert.Equal(0, code);
        Assert.DoesNotContain("PASSWORD", text);
        Assert.DoesNotContain("api.example", text);
        Assert.DoesNotContain("SECRET", text);
        Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
        Assert.Contains("bot=bot#", text);
        Assert.Contains("trace=unavailable", text);
        Assert.Contains("outcome=other", text);
        Assert.Contains("unknownEvents=1", text);
    }

    /// <summary>Bot-like ASCII secrets and customer-number-shaped tenant labels are not trusted simply because they resemble runtime ids.</summary>
    /// <returns>A task verifying separate per-bot statistics without disclosing arbitrary archive labels.</returns>
    /// <remarks>Unknown identities remain pseudonymous even when selected; filtering is not permission to print a secret-shaped label.</remarks>
    [Fact]
    public async Task Bot_shaped_untrusted_ascii_labels_remain_pseudonymous_when_filtered()
    {
        using var archive = new Archive();
        var now = DateTime.UtcNow.AddMinutes(-1);
        await archive.WriteAsync(
            Row("telegram_update_completed", now, "PRIVATE_API_KEY_778899_bot") with { ApplicationMs = 1 },
            Row("telegram_update_completed", now, "tenant-989121234567") with { ApplicationMs = 2 });
        var (code, text) = await archive.ReportAsync();
        Assert.Equal(0, code);
        Assert.DoesNotContain("PRIVATE_API_KEY", text);
        Assert.DoesNotContain("989121234567", text);
        Assert.Equal(2, text.Split('\n').Count(line => line.StartsWith("bot=bot#", StringComparison.Ordinal) && line.Contains("updates=1")));
        var selected = await archive.ReportAsync("--bot", "tenant-989121234567");
        Assert.Contains("updates=1", selected.Text);
        Assert.DoesNotContain("989121234567", selected.Text);
        Assert.DoesNotContain("PRIVATE_API_KEY", selected.Text);
    }

    /// <summary>Actual local deadline vocabularies are counted without treating caller shutdown or healthy long-poll waiting as latency failures.</summary>
    /// <returns>A task verifying reported interactive deadlines and unchanged measured polling duration.</returns>
    [Fact]
    public async Task Actual_deadline_categories_and_healthy_long_poll_have_distinct_report_semantics()
    {
        using var archive = new Archive();
        var now = DateTime.UtcNow.AddMinutes(-1);
        await archive.WriteAsync(
            Row("telegram_update_completed", now, "GozargahNetwork_Bot") with { ApplicationMs = 1 },
            Row("telegram_foreground_request_completed", now, "GozargahNetwork_Bot") with
            { Outcome = "failed", TimeoutCategory = "foreground", CancellationSource = "foreground_budget" },
            Row("telegram_foreground_request_completed", now, "GozargahNetwork_Bot") with
            { Outcome = "callback_policy_timeout", TimeoutCategory = "callback_best_effort", CancellationSource = "callback_policy" },
            Row("telegram_foreground_request_completed", now, "GozargahNetwork_Bot") with
            { Outcome = "cancelled", TimeoutCategory = "foreground", CancellationSource = "caller" },
            Row("telegram_api_request_completed", now, "GozargahNetwork_Bot") with
            { Method = "getUpdates", Category = "polling", Stage = "telegram_polling", DurationMs = 50000 },
            Row("telegram_poll_completed", now, "GozargahNetwork_Bot") with { DurationMs = 50000, ConsecutiveFailures = 0 });
        var (code, text) = await archive.ReportAsync();
        Assert.Equal(0, code);
        Assert.Contains("foregroundTimeouts=2 pollFailures=0", text);
        var pollingApi = Assert.Single(text.Split('\n'), line => line.StartsWith("bot/endpoint/method=GozargahNetwork_Bot/unknown/getUpdates/", StringComparison.Ordinal));
        Assert.Contains("max=50000 ms", pollingApi);
        Assert.Contains("slow=0", pollingApi);
    }

    /// <summary>Global worker contention remains visible when diagnosing one bot because shared SQLite contention can affect every bot.</summary>
    /// <returns>A task verifying controlled worker and transaction categories without inventing a bot identity.</returns>
    [Fact]
    public async Task Bot_filter_preserves_global_database_contention_and_transaction_lifetime()
    {
        using var archive = new Archive();
        var now = DateTime.UtcNow.AddMinutes(-1);
        await archive.WriteAsync(
            Row("sqlite_busy_retry", now, null) with { Category = "payment_settlement_notification", Operation = "sqlite_local", DurationMs = 70, BusyWaitMs = 70, SqliteErrorCode = 5 },
            Row("sqlite_transaction_completed", now, null) with { Category = "xui_renewal_recovery", Operation = "transaction_lifetime", DurationMs = 400, Outcome = "rolled_back", TimingQuality = "inclusive_transaction_lifetime_not_handler_stage" });
        var (code, text) = await archive.ReportAsync("--bot", "GozargahNetwork_Bot");
        Assert.Equal(0, code);
        Assert.Contains("global/payment_settlement_notification/sqlite_busy_retry/sqlite_local", text);
        Assert.Contains("global/xui_renewal_recovery/sqlite_transaction_completed/transaction_lifetime", text);
        Assert.Contains("retryEvents=1 busyWaitMs=70", text);
        Assert.DoesNotContain("bot=", text, StringComparison.Ordinal);
    }


    /// <summary>Bot filtering isolates all bot families while global loss remains visible and cumulative gauges are not summed.</summary>
    /// <returns>A task completing after a filtered scan.</returns>
    [Fact]
    public async Task Bot_and_time_filters_preserve_global_quality_and_missing_values()
    {
        using var archive = new Archive();
        var now = DateTime.UtcNow.AddMinutes(-1);
        await archive.WriteAsync(
            Row("telegram_update_completed", now, "tenant-1") with { ApplicationMs = 42 },
            Row("telegram_update_completed", now, "tenant-2") with { ApplicationMs = 9000 },
            Row("telegram_update_completed", now.AddDays(-2), "tenant-1") with { ApplicationMs = 9000 },
            Row("telegram_update_completed", now, "tenant-1") with { Recovered = true },
            Row("process_health", now, null) with { DroppedEvents = 7, WriterFailures = 2 },
            Row("telemetry_loss", now, null) with { DroppedEvents = 9, WriterFailures = 2, FullChannelDroppedEvents = 4 });
        var (code, text) = await archive.ReportAsync("--bot", "tenant-1");
        Assert.Equal(0, code);
        Assert.Contains("updates=2 count=1", text);
        Assert.Single(text.Split('\n'), line => line.StartsWith("bot=", StringComparison.Ordinal) && line.Contains(" updates=", StringComparison.Ordinal));
        Assert.Contains("outsideWindow=1 filtered=1", text);
        Assert.Contains("droppedEventsMaxGauge=9", text);
        Assert.Contains("writerFailuresMaxGauge=2", text);
        Assert.Contains("legacyMissingSessionProvenance: counterRecords=2", text);
        Assert.Contains("missingApplicationSummaries=1", text);
        Assert.Contains("recovered=1", text);
    }

    /// <summary>Process restarts with overlapping cumulative gauges must sum each session's maximum, never snapshots or the global maximum.</summary>
    /// <returns>A task completing after the two-session archive and legacy provenance are reported.</returns>
    [Fact]
    public async Task Session_counter_maxima_are_summed_and_legacy_provenance_remains_separate()
    {
        using var archive = new Archive();
        var now = DateTime.UtcNow.AddMinutes(-1);
        var first = new string('a', 32);
        var second = new string('b', 32);
        await archive.WriteAsync(
            Row("process_health", now, null) with { SessionId = first, DroppedEvents = 7, WriterFailures = 2, FullChannelDroppedEvents = 3, WriteDroppedEvents = 2, RejectedEvents = 1, ShutdownDroppedEvents = 1 },
            Row("telemetry_loss", now, null) with { SessionId = first, DroppedEvents = 9, WriterFailures = 3, FullChannelDroppedEvents = 4, WriteDroppedEvents = 2, RejectedEvents = 1, ShutdownDroppedEvents = 2 },
            Row("process_health", now, null) with { SessionId = second, DroppedEvents = 8, WriterFailures = 1, FullChannelDroppedEvents = 2, WriteDroppedEvents = 3, RejectedEvents = 2, ShutdownDroppedEvents = 1 },
            Row("telemetry_loss", now, null) with { SessionId = second, DroppedEvents = 8, WriterFailures = 1, FullChannelDroppedEvents = 2, WriteDroppedEvents = 3, RejectedEvents = 2, ShutdownDroppedEvents = 1 },
            Row("process_health", now, null) with { DroppedEvents = 5, WriterFailures = 2 },
            Row("process_health", now, null) with { DroppedEvents = 3, WriterFailures = 1 });
        var (code, text) = await archive.ReportAsync();
        Assert.Equal(0, code);
        Assert.Contains("sessionCounterMaximaSums: sessions=2 droppedEvents=17 writerFailures=4 fullChannel=6 write=5 rejected=3 shutdown=3", text);
        Assert.Contains("legacyMissingSessionProvenance: counterRecords=2 droppedEventsMaxGauge=5 writerFailuresMaxGauge=2", text);
        Assert.Contains("unknown restart provenance", text);
    }

    /// <summary>A hostile stream of process ids cannot make cumulative-counter aggregation unbounded or silently merge sessions.</summary>
    /// <returns>A task completing after session-cap omissions are reported.</returns>
    [Fact]
    public async Task Session_counter_cardinality_is_bounded_and_overflow_is_visible()
    {
        using var archive = new Archive();
        var now = DateTime.UtcNow.AddMinutes(-1);
        await archive.WriteAsync(Enumerable.Range(0, 513).Select(i => Row("process_health", now, null) with
        { SessionId = i.ToString("x32", CultureInfo.InvariantCulture), DroppedEvents = 1 }).ToArray());
        var (code, text) = await archive.ReportAsync();
        Assert.Equal(0, code);
        Assert.Contains("sessionCounterMaximaSums: sessions=512 droppedEvents=512", text);
        Assert.Contains("sessionCounterRecords=1", text);
    }

    /// <summary>Assistant ids remain readable and repeated cumulative SQLite busy-wait values must not be summed as individual delays.</summary>
    /// <returns>A task completing after assistant and retry observations are aggregated.</returns>
    [Fact]
    public async Task Assistant_identity_and_individual_busy_retry_delays_are_preserved()
    {
        using var archive = new Archive();
        var now = DateTime.UtcNow.AddMinutes(-1);
        await archive.WriteAsync(
            Row("telegram_update_completed", now, "sales-assistant") with { ApplicationMs = 1 },
            Row("sqlite_busy_retry", now, "sales-assistant") with { Category = "inbox", DurationMs = 50, BusyWaitMs = 50, BusyRetryCount = 1, Outcome = "retrying" },
            Row("sqlite_busy_retry", now, "sales-assistant") with { Category = "inbox", DurationMs = 150, BusyWaitMs = 200, BusyRetryCount = 2, Outcome = "retrying" });
        var (code, text) = await archive.ReportAsync("--bot", "sales-assistant");
        Assert.Equal(0, code);
        Assert.Contains("bot=sales-assistant updates=1", text);
        Assert.Contains("retryEvents=2 busyWaitMs=200", text);
        Assert.DoesNotContain("busyWaitMs=250", text);
    }

    /// <summary>The most frequent bottleneck is not the category with the largest sum from one extreme outlier.</summary>
    /// <returns>A task verifying per-update category attribution without double-counting inclusive handler duration.</returns>
    /// <remarks>Two Telegram-bound updates outweigh one much longer SQLite outlier in frequency, not in elapsed totals.</remarks>
    [Fact]
    public async Task Frequent_bottleneck_counts_updates_not_summed_outlier_duration()
    {
        using var archive = new Archive();
        var now = DateTime.UtcNow.AddMinutes(-1);
        await archive.WriteAsync(
            Row("telegram_update_completed", now, "tenant-1") with { ApplicationMs = 100, HandlerMs = 100, StageMs = new() { ["telegram_send"] = 90, ["sqlite_read"] = 10 } },
            Row("telegram_update_completed", now, "tenant-1") with { ApplicationMs = 200, HandlerMs = 200, StageMs = new() { ["telegram_send"] = 180, ["sqlite_read"] = 20 } },
            Row("telegram_update_completed", now, "tenant-1") with { ApplicationMs = 10000, HandlerMs = 10000, StageMs = new() { ["sqlite_read"] = 10000 } });
        var (code, text) = await archive.ReportAsync();
        Assert.Equal(0, code);
        Assert.Contains("bottleneck=telegram_send bottleneckUpdates=2 stageTotalMs=270", text);
    }

    /// <summary>Duplicate admission completion still closes trace coverage but cannot inflate executed-update counts or bias latency quantiles.</summary>
    /// <returns>A task completing after duplicate summaries are excluded from latency statistics and top ranking.</returns>
    [Fact]
    public async Task Duplicate_admission_summaries_are_quality_counts_not_executed_updates()
    {
        using var archive = new Archive();
        var now = DateTime.UtcNow.AddMinutes(-1);
        var trace = Guid.NewGuid().ToString("N");
        var duplicateTrace = Guid.NewGuid().ToString("N");
        await archive.WriteAsync(
            Row("telegram_update_received", now, "tenant-1") with { TraceId = trace },
            Row("telegram_update_completed", now, "tenant-1") with { TraceId = trace, ApplicationMs = 100, StageMs = new() { ["business_processing"] = 100 } },
            Row("telegram_update_received", now, "tenant-1") with { TraceId = duplicateTrace },
            Row("telegram_update_completed", now, "tenant-1") with { TraceId = duplicateTrace, Outcome = "duplicate", ApplicationMs = 0.01 });
        var (code, text) = await archive.ReportAsync("--bot", "tenant-1");
        Assert.Equal(0, code);
        Assert.Contains("updates=1 count=1 P50~=100 P95~=100 P99~=100 max=100 ms", text);
        Assert.Contains("duplicateSummaries=1", text);
        Assert.Contains("trackedTraces=2 incompleteTracesObserved=0", text);
        var top = text.Split('\n').Where(line => line.StartsWith("bot=", StringComparison.Ordinal) && line.Contains(" trace=", StringComparison.Ordinal)).ToArray();
        Assert.Single(top);
        Assert.DoesNotContain("trace=" + duplicateTrace, top[0]);
    }

    /// <summary>Recovered slow handlers without a receiver origin must remain visible without inventing end-to-end application latency.</summary>
    /// <returns>A task completing after recovered handler-only ranking and application-only quantiles are reported.</returns>
    [Fact]
    public async Task Recovered_handlers_rank_by_explicit_handler_only_basis_without_biasing_application_quantiles()
    {
        using var archive = new Archive();
        var now = DateTime.UtcNow.AddMinutes(-1);
        var recoveredTrace = Guid.NewGuid().ToString("N");
        await archive.WriteAsync(
            Row("telegram_update_completed", now, "tenant-1") with { ApplicationMs = 100, TraceId = Guid.NewGuid().ToString("N"), StageMs = new() { ["business_processing"] = 100 } },
            Row("telegram_update_completed", now, "tenant-1") with { ApplicationMs = null, HandlerMs = 10000, Recovered = true, TraceId = recoveredTrace, StageMs = new() { ["sqlite_read"] = 10000 } });
        var (code, text) = await archive.ReportAsync("--bot", "tenant-1");
        Assert.Equal(0, code);
        Assert.Contains("updates=2 count=1 P50~=100 P95~=100 P99~=100 max=100 ms missingApplication=1 slow=1", text);
        Assert.Contains("missingApplicationSummaries=1", text);
        var top = text.Split('\n').Where(line => line.StartsWith("bot=", StringComparison.Ordinal) && line.Contains(" trace=", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, top.Length);
        Assert.Contains("trace=" + recoveredTrace, top[0]);
        Assert.Contains("application=unavailable outcome=completed rankingBasis=handler_only rankingMs=10000", top[0]);
        Assert.Contains("rankingBasis=application rankingMs=100", top[1]);
    }

    /// <summary>Top twenty ranking is exact, independently of explicitly approximate histogram quantiles.</summary>
    /// <returns>A task completing after all records are ranked.</returns>
    [Fact]
    public async Task Top_twenty_is_exact_and_quantiles_are_explicitly_approximate()
    {
        using var archive = new Archive();
        var now = DateTime.UtcNow.AddMinutes(-1);
        await archive.WriteAsync(Enumerable.Range(1, 100).Select(i => Row("telegram_update_completed", now, "tenant-1") with
        {
            TraceId = i.ToString("x32", CultureInfo.InvariantCulture), ApplicationMs = i,
            StageMs = new() { ["business_processing"] = i }
        }).ToArray());
        var (code, text) = await archive.ReportAsync("--bot", "tenant-1");
        Assert.Equal(0, code);
        var ranked = text.Split('\n').Where(line => line.StartsWith("bot=", StringComparison.Ordinal) && line.Contains(" trace=", StringComparison.Ordinal)).ToArray();
        Assert.Equal(20, ranked.Length);
        Assert.Contains("application=100 outcome=completed", ranked[0]);
        Assert.Contains("application=81 outcome=completed", ranked[^1]);
        Assert.Contains("P50~=", text);
        Assert.Contains("upper-bound approximation", text);
        Assert.Contains("max=100 ms", text);
    }

    /// <summary>Bounded bot and trace cardinality must be observable rather than silently retaining arbitrary archive growth.</summary>
    /// <returns>A task completing after capped state is exercised.</returns>
    [Fact]
    public async Task Cardinality_limits_are_visible_and_incomplete_traces_are_observed()
    {
        using var archive = new Archive();
        var now = DateTime.UtcNow.AddMinutes(-1);
        await using (var writer = new StreamWriter(archive.PathFor("001.jsonl"), false, new UTF8Encoding(false)))
        {
            for (var i = 0; i < 20001; i++)
                await writer.WriteLineAsync(JsonSerializer.Serialize(Row("telegram_update_received", now, "tenant-1") with
                { TraceId = i.ToString("x32", CultureInfo.InvariantCulture) }, WireOptions));
            for (var i = 2; i <= 130; i++)
                await writer.WriteLineAsync(JsonSerializer.Serialize(Row("telegram_update_completed", now, "tenant-" + i) with { ApplicationMs = 1 }, WireOptions));
        }
        var (code, text) = await archive.ReportAsync();
        Assert.Equal(0, code);
        Assert.Contains("trackedTraces=20000 incompleteTracesObserved=20000", text);
        Assert.Contains("botRecords=2", text);
        Assert.Contains("traceRecords=1", text);
        Assert.Equal(128, text.Split('\n').Count(line => line.StartsWith("bot=", StringComparison.Ordinal) && line.Contains(" updates=", StringComparison.Ordinal)));
    }

    /// <summary>Exact mode selection, duplicate options, missing values, and secret-bearing invalid arguments fail before any I/O.</summary>
    /// <returns>A task completing after secret-free validation responses are asserted.</returns>
    [Fact]
    public async Task Invalid_arguments_fail_safely_and_mode_selection_is_exact()
    {
        Assert.True(LatencyTelemetryReportCli.IsRequested(new[] { "telemetry-report" }));
        Assert.False(LatencyTelemetryReportCli.IsRequested(new[] { "--telemetry-report" }));
        Assert.False(LatencyTelemetryReportCli.IsRequested(new[] { "TELEMETRY-REPORT" }));
        foreach (var args in new[]
        {
            new[] { "telemetry-report", "--hours", "0" },
            new[] { "telemetry-report", "--hours", "24", "--hours", "48" },
            new[] { "telemetry-report", "--bot", "123:TOKEN-SECRET" },
            new[] { "telemetry-report", "--directory" },
            new[] { "telemetry-report", "--unknown=PASSWORD-SECRET" }
        })
        {
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            Assert.Equal(2, await LatencyTelemetryReportCli.RunAsync(args, output, CancellationToken.None));
            Assert.StartsWith("Telemetry report: INVALID_ARGUMENTS (", output.ToString());
            Assert.DoesNotContain("SECRET", output.ToString());
        }
    }

    /// <summary>Cancellation exits without accessing storage or constructing serving dependencies.</summary>
    /// <returns>A task completing after the stable cancellation exit code is observed.</returns>
    [Fact]
    public async Task Cancellation_returns_a_stable_non_serving_exit_code()
    {
        using var archive = new Archive();
        using var source = new CancellationTokenSource();
        source.Cancel();
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var code = await LatencyTelemetryReportCli.RunAsync(new[] { "telemetry-report", "--directory", archive.Directory }, output, source.Token);
        Assert.Equal(130, code);
        Assert.Equal("Telemetry report: CANCELLED" + Environment.NewLine, output.ToString());
    }

    /// <summary>Cloud/Local latency quantiles must stay independent by method and boundary, with legacy routes explicitly unknown.</summary>
    /// <returns>A task verifying endpoint comparisons and closed incident labels from the real analyzer.</returns>
    [Fact]
    public async Task Endpoint_quantiles_and_incidents_preserve_closed_dimensions_and_legacy_unknown()
    {
        using var archive = new Archive();
        var now = DateTime.UtcNow.AddMinutes(-1);
        var bot = "GozargahNetwork_Bot";
        await archive.WriteAsync(
            Row("telegram_api_request_completed", now, bot) with { EndpointType = "cloud", EndpointGeneration = 1, Method = "sendMessage", DurationMs = 100 },
            Row("telegram_api_request_completed", now, bot) with { EndpointType = "cloud", EndpointGeneration = 1, Method = "sendMessage", DurationMs = 200 },
            Row("telegram_api_request_completed", now, bot) with { EndpointType = "local", EndpointGeneration = 2, Method = "sendMessage", DurationMs = 10 },
            Row("telegram_request_completed", now, bot) with { EndpointType = "local", EndpointGeneration = 2, Method = "sendMessage", DurationMs = 5 },
            Row("telegram_api_request_completed", now, bot) with { Method = "sendMessage", DurationMs = 900 },
            Row("telegram_endpoint_health", now, bot) with { EndpointType = "local", EndpointGeneration = 2, MigrationState = "Local", HealthCheckDurationMs = 3, LastSuccessUtc = now },
            Row("telegram_endpoint_outage", now.AddSeconds(1), bot) with { EndpointType = "local", EndpointGeneration = 2, MigrationState = "LocalUnavailable", FailoverTrigger = "automatic_outage" },
            Row("telegram_endpoint_recovered", now.AddSeconds(2), bot) with { EndpointType = "cloud", EndpointGeneration = 3, MigrationState = "CloudRecovered", FailoverTrigger = "automatic_outage", FailoverDurationMs = 600000, CloudReuseRemainingMs = 0 },
            Row("telegram_endpoint_migration", now, bot) with { EndpointType = "SECRET_URL", MigrationState = "SECRET_STATE", FailoverTrigger = "SECRET_ACTOR" });
        var (code, text) = await archive.ReportAsync();
        Assert.Equal(0, code);
        var cloud = Assert.Single(text.Split('\n'), line => line.StartsWith("bot/endpoint/method=" + bot + "/cloud/sendMessage/sdk_validated", StringComparison.Ordinal));
        Assert.Contains("events=2 count=2", cloud);
        foreach (var percentile in new[] { "P50~=", "P95~=", "P99~=" })
        {
            var value = double.Parse(cloud.Split(percentile, StringSplitOptions.None)[1].Split(' ')[0], CultureInfo.InvariantCulture);
            var expected = percentile == "P50~=" ? 100d : 200d;
            Assert.InRange(value, expected, expected * 1.05);
        }
        Assert.Contains("max=200 ms", cloud);
        Assert.Contains("bot/endpoint/method=" + bot + "/local/sendMessage/sdk_validated events=1 count=1 P50~=10", text);
        Assert.Contains("bot/endpoint/method=" + bot + "/local/sendMessage/headers events=1 count=1 P50~=5", text);
        Assert.Contains("bot/endpoint/method=" + bot + "/unknown/sendMessage/sdk_validated events=1 count=1", text);
        Assert.Contains("6. Data quality", text);
        Assert.Contains("7. Endpoint health", text);
        Assert.Contains("healthEvents=1 migrations=0 outages=1 recoveries=0", text);
        Assert.Contains("state=CloudRecovered trigger=automatic_outage", text);
        Assert.Contains("endpointEvent=telegram_endpoint_recovered", text);
        Assert.DoesNotContain("SECRET", text);
        Assert.Contains("endpoint=unknown", text);
        Assert.Contains("state=unknown trigger=unknown", text);
    }

    /// <summary>Endpoint history remains capped under many transitions and hostile optional fields are never projected.</summary>
    /// <returns>A task verifying exact omission accounting and newest-event state independent of scan order.</returns>
    [Fact]
    public async Task Endpoint_history_is_bounded_and_latest_state_uses_event_time()
    {
        using var archive = new Archive();
        var now = DateTime.UtcNow.AddMinutes(-10);
        var rows = Enumerable.Range(0, 102).Reverse().Select(i => Row("telegram_endpoint_migration", now.AddSeconds(i), "sales-assistant") with
        { EndpointType = "local", EndpointGeneration = i + 1, MigrationState = i == 101 ? "Local" : "SwitchingToLocal", FailoverTrigger = "manual" }).ToArray();
        await archive.WriteAsync(rows);
        var (code, text) = await archive.ReportAsync();
        Assert.Equal(0, code);
        Assert.Contains("generation=102 state=Local", text);
        Assert.Contains("limit=100 omitted=2", text);
        Assert.Equal(100, text.Split('\n').Count(line => line.StartsWith("endpointEvent=", StringComparison.Ordinal)));
    }

    /// <summary>Creates a minimal real schema record with a known successful outcome.</summary>
    /// <param name="type">Known record family.</param><param name="utc">Explicit UTC timestamp.</param>
    /// <param name="bot">Internal bot id, nullable for global health records.</param><returns>A serializable v1 event.</returns>
    private static LatencyTelemetryEvent Row(string type, DateTime utc, string? bot) => new()
    { EventType = type, TimestampUtc = utc, BotId = bot, Outcome = "completed" };

    /// <summary>Owns only isolated test storage and removes it after each behavioral test.</summary>
    private sealed class Archive : IDisposable
    {
        /// <summary>Temporary telemetry directory, never an application Data directory.</summary>
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "latency-report-" + Guid.NewGuid().ToString("N"));
        /// <summary>Creates an empty isolated archive.</summary>
        public Archive() => System.IO.Directory.CreateDirectory(Directory);
        /// <summary>Resolves a compile-time fixture filename beneath the temporary directory.</summary>
        /// <param name="name">Safe fixture filename.</param><returns>Absolute fixture path.</returns>
        public string PathFor(string name) => System.IO.Path.Combine(Directory, name);
        /// <summary>Writes real serialized schema records with complete newline terminators.</summary>
        /// <param name="rows">Versioned fixture records.</param><returns>A task completing after fixture bytes are written.</returns>
        public async Task WriteAsync(params LatencyTelemetryEvent[] rows)
        {
            await using var writer = new StreamWriter(PathFor("001.jsonl"), false, new UTF8Encoding(false));
            foreach (var row in rows) await writer.WriteLineAsync(JsonSerializer.Serialize(row, WireOptions));
        }
        /// <summary>Runs the public production command against the isolated archive.</summary>
        /// <param name="options">Additional validated CLI options.</param><returns>Exit code and captured safe report.</returns>
        public async Task<(int Code, string Text)> ReportAsync(params string[] options)
        {
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            var args = new[] { "telemetry-report", "--directory", Directory }.Concat(options).ToArray();
            return (await LatencyTelemetryReportCli.RunAsync(args, output, CancellationToken.None), output.ToString());
        }
        /// <summary>Deletes only this test's temporary archive.</summary>
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
