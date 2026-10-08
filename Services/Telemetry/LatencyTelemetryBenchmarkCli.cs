using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Types;

namespace Adminbot.Services.Telemetry;

/// <summary>Runs an isolated producer-plus-live-writer benchmark without starting configuration, receivers or the host.</summary>
/// <remarks>Measures only real instrumentation, not network or database latency. The diagnostic target is extra producer P95 below 1 ms; it is reported, never asserted as a CI timing guarantee. Every run uses and deletes a unique temporary child directory.</remarks>
public static class LatencyTelemetryBenchmarkCli
{
    /// <summary>Recognizes the explicit diagnostic command before normal application startup.</summary>
    /// <param name="args">Process arguments; only the first command is inspected.</param>
    /// <returns>True when telemetry-benchmark was selected.</returns>
    /// <example><code>if (LatencyTelemetryBenchmarkCli.IsRequested(args)) return await LatencyTelemetryBenchmarkCli.RunAsync(args, Console.Out);</code></example>
    public static bool IsRequested(string[] args) => args.Length > 0 && string.Equals(args[0], "telemetry-benchmark", StringComparison.OrdinalIgnoreCase);

    /// <summary>Measures enabled and disabled production instrumentation with warmup and a live JSONL writer.</summary>
    /// <param name="args">telemetry-benchmark, optional --iterations 100..1000000 and --directory scratch-root.</param>
    /// <param name="output">Required diagnostic output destination; no customer inputs or credentials are printed.</param>
    /// <param name="cancellationToken">Cancels measurement; writer shutdown and temporary-directory cleanup still run.</param>
    /// <returns>Zero for a valid loss-free measurement, one for drops, faults, cancellation, storage failure or incorrect Linux permissions, two for invalid arguments.</returns>
    /// <remarks>Per-sample Stopwatch intervals contain producer work only. Drain waits are outside samples. Process allocation includes the live writer; current-thread allocation describes the synchronous producer. Neither includes fabricated I/O waits. Full-queue losses are measured separately and must equal rejected writes exactly.</remarks>
    /// <example><code>dotnet run -c Release -- telemetry-benchmark --iterations 10000</code></example>
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken = default)
    {
        var iterations = 10000;
        var scratch = Path.GetTempPath();
        for (var index = 1; index < args.Length; index++)
        {
            if (args[index] == "--iterations" && ++index < args.Length && int.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is >= 100 and <= 1000000)
                iterations = value;
            else if (args[index] == "--directory" && ++index < args.Length && !string.IsNullOrWhiteSpace(args[index]))
                scratch = args[index];
            else
            {
                await output.WriteLineAsync("Usage: telemetry-benchmark [--iterations 100..1000000] [--directory scratch-root]");
                return 2;
            }
        }

        string directory = null;
        try
        {
            directory = Path.Combine(Path.GetFullPath(scratch), "adminbot-telemetry-benchmark-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var fullQueueValid = await ProbeFullQueueAsync(Path.Combine(directory, "full"), output);
            var disabled = await MeasureAsync(false, iterations, Path.Combine(directory, "disabled"), cancellationToken);
            var enabled = await MeasureAsync(true, iterations, Path.Combine(directory, "enabled"), cancellationToken);
            var permissionsValid = await CheckPermissionsAsync(Path.Combine(directory, "enabled", "Telemetry"), output);
            await output.WriteLineAsync("Method: Stopwatch per synchronous update producer; live asynchronous JSONL writer; 512 warmup updates/mode; bounded-batch drains excluded from producer timings.");
            await output.WriteLineAsync("Allocation method: GC.GetAllocatedBytesForCurrentThread per producer sample; GC.GetTotalAllocatedBytes(precise:true) across measured batch and final writer drain (process-wide, includes writer). No host, network, SQLite or synthetic delays.");
            await PrintAsync(output, "disabled", disabled);
            await PrintAsync(output, "enabled", enabled);
            var delta = Percentile(enabled.Durations, .95) - Percentile(disabled.Durations, .95);
            await output.WriteLineAsync(FormattableString.Invariant($"Extra producer P95={delta:F6} ms; target <1.000000 ms; targetMet={delta < 1}; producer allocation delta={(enabled.ProducerBytes - disabled.ProducerBytes) / (double)iterations:F2} bytes/update; process allocation delta={(enabled.ProcessBytes - disabled.ProcessBytes) / (double)iterations:F2} bytes/update."));
            var valid = fullQueueValid && permissionsValid && enabled.Drops == 0 && enabled.Failures == 0 && disabled.Drops == 0 && disabled.Failures == 0;
            await output.WriteLineAsync(valid ? "Measurement valid: no warmup/batch loss or writer faults; permissions valid for this platform." : "Measurement INVALID: drops, writer faults, incorrect full-queue accounting or unsafe Linux permissions invalidate results.");
            return valid ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException or ArgumentException or NotSupportedException)
        {
            await output.WriteLineAsync(exception is OperationCanceledException ? "Benchmark cancelled." : "Benchmark storage or argument failure; no reliable measurement available.");
            return 1;
        }
        finally
        {
            if (directory != null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Exercises deterministic channel saturation before a writer starts, separately from overhead samples.</summary>
    /// <param name="directory">Unique data directory owned by this run.</param>
    /// <param name="output">Benchmark output destination.</param>
    /// <returns>True when exactly 64 writes were accepted and all other attempts were counted as drops.</returns>
    /// <remarks>No producer I/O is possible because the service is never started.</remarks>
    private static async Task<bool> ProbeFullQueueAsync(string directory, TextWriter output)
    {
        using var service = CreateService(true, directory, 64);
        var observation = new LatencyTelemetryEvent { EventType = "telegram_update_completed", BotId = "benchmark", Outcome = "completed" };
        var accepted = 0;
        const int attempts = 256;
        for (var index = 0; index < attempts; index++) if (service.TryRecord(observation)) accepted++;
        var valid = accepted == 64 && service.ChannelDepth == 64 && service.DroppedEvents == attempts - accepted;
        await output.WriteLineAsync(FormattableString.Invariant($"Full-channel probe: attempts={attempts}; accepted={accepted}; drops={service.DroppedEvents}; exact={valid}. Excluded from measured batch."));
        return valid;
    }

    /// <summary>Measures one mode after warmup while the service's actual writer remains active.</summary>
    /// <param name="enabled">Whether the production writer and lifecycle collection are enabled.</param>
    /// <param name="iterations">Number of measured local updates.</param>
    /// <param name="directory">Unique temporary data directory for this mode.</param>
    /// <param name="cancellationToken">Cancellation checked between synchronous producer samples.</param>
    /// <returns>Sorted producer milliseconds, allocation totals and measured-batch loss counters.</returns>
    private static async Task<Measurement> MeasureAsync(bool enabled, int iterations, string directory, CancellationToken cancellationToken)
    {
        using var service = CreateService(enabled, directory, 8192);
        await service.StartAsync(cancellationToken);
        var tracker = new UpdateTelemetryTracker(service, 8192);
        var update = new Update { Id = 1 };
        var item = new TelegramUpdateWorkItem(1, new TelegramUpdateExecutionKey("benchmark", 1), update, DateTime.UtcNow, DateTime.UtcNow);
        try
        {
            for (var index = 0; index < 512; index++) Produce(tracker, service, item);
            await DrainAsync(service, cancellationToken);
            var durations = new double[iterations];
            var allocations = new double[iterations];
            long producerBytes = 0;
            var processStarted = GC.GetTotalAllocatedBytes(precise: true);
            for (var index = 0; index < iterations; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var started = Stopwatch.GetTimestamp();
                Produce(tracker, service, item);
                durations[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                allocations[index] = GC.GetAllocatedBytesForCurrentThread() - allocated;
                producerBytes += (long)allocations[index];
                if ((index & 127) == 127) await DrainAsync(service, cancellationToken);
            }
            await DrainAsync(service, cancellationToken);
            await service.StopAsync(CancellationToken.None);
            var processBytes = GC.GetTotalAllocatedBytes(precise: true) - processStarted;
            Array.Sort(durations);
            Array.Sort(allocations);
            return new Measurement(durations, allocations, producerBytes, processBytes, service.DroppedEvents, service.WriterFailures);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    /// <summary>Runs actual lifecycle, exclusive stage, visible-response and terminal-summary producer instrumentation.</summary>
    /// <param name="tracker">Mode-specific lifecycle owner.</param>
    /// <param name="service">Mode-specific live writer or disabled no-op service.</param>
    /// <param name="item">Reusable payload-free local work item, avoiding unrelated payload allocation.</param>
    /// <remarks>No database or network is simulated: only their already-completed boundary instrumentation is exercised. Disabled mode preserves existing handler scope instrumentation but skips lifecycle snapshot capture, matching the scheduler.</remarks>
    private static void Produce(UpdateTelemetryTracker tracker, LatencyTelemetryService service, TelegramUpdateWorkItem item)
    {
        using var received = tracker.Receive(item.Key.BotId, item.Update.Id, "Message");
        received?.BeginAdmission();
        received?.BeginAdmissionAttempt();
        tracker.Persisted(item.Sequence, item.AcceptedAtUtc);
        var tick = Stopwatch.GetTimestamp();
        var timeline = tracker.Claimed(item, tick, tick);
        timeline?.HandlerStarted();
        using var scope = TelegramUpdateLatencyScope.Push(item.Sequence, item.Key.BotId, item.Update.Id,
            TimeSpan.FromDays(1), null, telemetry: service, traceId: timeline?.TraceId);
        using (scope.Measure(TelegramUpdateStage.BusinessProcessing)) { }
        using (scope.Measure(TelegramUpdateStage.TelegramSend))
        {
            var request = scope.MeasureTelegramRequest(TelegramForegroundRequestKind.TextSend);
            request.Complete(TelegramForegroundRequestOutcome.Completed, null);
        }
        scope.Dispose();
        timeline?.HandlerCompleted(scope.CaptureTelemetry());
        timeline?.Completed(null, Stopwatch.GetTimestamp(), true);
    }

    /// <summary>Waits outside measured intervals for a live writer to consume a bounded batch.</summary>
    /// <param name="service">Actual mode-specific service.</param>
    /// <param name="cancellationToken">Caller cancellation checked during the drain.</param>
    /// <returns>A task completing after producer queue consumption, not a durability promise.</returns>
    private static async Task DrainAsync(LatencyTelemetryService service, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (service.ChannelDepth != 0) await Task.Delay(1, deadline.Token);
    }

    /// <summary>Creates resource-bounded benchmark options and the production JSONL service.</summary>
    /// <param name="enabled">Collection switch for this mode.</param>
    /// <param name="directory">Isolated temporary data root.</param>
    /// <param name="capacity">Validated channel capacity in events.</param>
    /// <returns>An unstarted service whose lifetime is owned by the caller.</returns>
    private static LatencyTelemetryService CreateService(bool enabled, string directory, int capacity) => new(new LatencyTelemetryOptions
    {
        Enabled = enabled, ChannelCapacity = capacity, MaxFileBytes = 256L * 1024 * 1024,
        MaxTotalBytes = 2L * 1024 * 1024 * 1024, FlushIntervalSeconds = 1
    }, directory, NullLogger<LatencyTelemetryService>.Instance);

    /// <summary>Checks actual writer-created Linux permissions outside benchmark samples.</summary>
    /// <param name="directory">Isolated enabled-mode dedicated telemetry directory containing flushed files.</param>
    /// <param name="output">Diagnostic output destination.</param>
    /// <returns>True for exact Linux directory 0700 and file 0600 modes, or when Unix permissions do not apply.</returns>
    /// <remarks>Checks real filesystem metadata; it does not assume the configured creation mode took effect. At least one writer-created file is required on Linux.</remarks>
    private static async Task<bool> CheckPermissionsAsync(string directory, TextWriter output)
    {
        if (!OperatingSystem.IsLinux())
        {
            await output.WriteLineAsync(OperatingSystem.IsWindows() ? "PermissionCheck=windows_not_applicable" : "PermissionCheck=non_linux_not_applicable");
            return true;
        }
        var expectedDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var expectedFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var paths = Directory.Exists(directory) ? Directory.GetFiles(directory, "latency-*.jsonl") : Array.Empty<string>();
        var valid = paths.Length > 0 && File.GetUnixFileMode(directory) == expectedDirectory;
        foreach (var path in paths) valid &= File.GetUnixFileMode(path) == expectedFile;
        await output.WriteLineAsync(valid ? "PermissionCheck=passed_linux_0700_0600" : "PermissionCheck=FAILED_linux_0700_0600");
        return valid;
    }

    /// <summary>Prints comparable duration quantiles, allocations and loss counters for one mode.</summary>
    /// <param name="output">Diagnostic text destination.</param>
    /// <param name="name">Fixed enabled or disabled label.</param>
    /// <param name="measurement">Sorted observed samples and measured allocation totals.</param>
    /// <returns>A task completing after the summary is written.</returns>
    private static Task PrintAsync(TextWriter output, string name, Measurement measurement) => output.WriteLineAsync(FormattableString.Invariant(
        $"{name}: N={measurement.Durations.Length}; P50={Percentile(measurement.Durations, .50):F6} ms; P95={Percentile(measurement.Durations, .95):F6} ms; P99={Percentile(measurement.Durations, .99):F6} ms; allocationP50={Percentile(measurement.Allocations, .50):F0} bytes; allocationP95={Percentile(measurement.Allocations, .95):F0} bytes; allocationP99={Percentile(measurement.Allocations, .99):F0} bytes; producerMean={measurement.ProducerBytes / (double)measurement.Durations.Length:F2} bytes/update; processMean={measurement.ProcessBytes / (double)measurement.Durations.Length:F2} bytes/update; drops={measurement.Drops}; writerFaults={measurement.Failures}."));

    /// <summary>Selects an exact nearest-rank quantile from bounded sorted benchmark samples.</summary>
    /// <param name="samples">Nonempty ascending duration or allocation samples in the caller's declared units.</param>
    /// <param name="quantile">Fraction between zero and one.</param>
    /// <returns>The observed nearest-rank sample, with no histogram approximation.</returns>
    private static double Percentile(double[] samples, double quantile) => samples[Math.Clamp((int)Math.Ceiling(samples.Length * quantile) - 1, 0, samples.Length - 1)];

    /// <summary>Observed sorted producer intervals and measured allocation/loss totals for one mode.</summary>
    /// <param name="Durations">Sorted per-update producer milliseconds.</param>
    /// <param name="Allocations">Sorted per-update producer allocation bytes.</param>
    /// <param name="ProducerBytes">Bytes allocated synchronously by producer samples only.</param>
    /// <param name="ProcessBytes">Process-wide allocations including live writer and final drain.</param>
    /// <param name="Drops">Event losses across warmup and measurement; any loss invalidates the run.</param>
    /// <param name="Failures">Writer faults across warmup and measurement.</param>
    private sealed record Measurement(double[] Durations, double[] Allocations, long ProducerBytes, long ProcessBytes, long Drops, long Failures);
}
