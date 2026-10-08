using System;

namespace Adminbot.Services.Telemetry;

/// <summary>Startup-bound limits for payload-free latency collection shared by every bot family.</summary>
/// <remarks>Missing configuration enables collection with bounded defaults. Invalid limits are explicitly rejected, never silently clamped. Filesystem failures are handled separately and never prevent handlers from running.</remarks>
public sealed class LatencyTelemetryOptions
{
    /// <summary>Enables collection; false takes a no-channel, no-writer, no-sampler path.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>UTC days of telemetry retention; valid range is one through 365, default fourteen.</summary>
    public int RetentionDays { get; set; } = 14;
    /// <summary>Maximum bytes per JSONL file; valid range is 1 MiB through 256 MiB, default 25 MiB.</summary>
    public long MaxFileBytes { get; set; } = 25L * 1024 * 1024;
    /// <summary>Maximum combined bytes of owned telemetry files; between two file limits and 2 GiB, default 500 MiB.</summary>
    public long MaxTotalBytes { get; set; } = 500L * 1024 * 1024;
    /// <summary>Maximum queued observations; valid range is 64 through 65536, default 8192.</summary>
    public int ChannelCapacity { get; set; } = 8192;
    /// <summary>Health sampling interval in seconds; valid range is five through 300, default thirty.</summary>
    public int SampleIntervalSeconds { get; set; } = 30;
    /// <summary>Buffered file flush interval in seconds; valid range is one through thirty, default two.</summary>
    public int FlushIntervalSeconds { get; set; } = 2;
    /// <summary>Maximum graceful writer drain duration in seconds; valid range is one through thirty, default five.</summary>
    public int ShutdownFlushSeconds { get; set; } = 5;
    /// <summary>Slow-operation threshold in milliseconds; valid range is 100 through 300000, default 2000.</summary>
    public double SlowOperationMs { get; set; } = 2000;
    /// <summary>Slow-update threshold in milliseconds; valid range is 100 through 300000, default 5000.</summary>
    public double SlowUpdateMs { get; set; } = 5000;

    /// <summary>Validates startup values and returns a private snapshot insulated from later configuration mutation.</summary>
    /// <returns>A nonnull independent options instance with exactly the supplied values.</returns>
    /// <remarks>Validation concerns resource bounds only; no directories or files are opened.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">One limit is outside its documented range.</exception>
    /// <example><code>var snapshot = configuration.LatencyTelemetry.ValidateAndSnapshot();</code></example>
    public LatencyTelemetryOptions ValidateAndSnapshot()
    {
        ValidateRange(RetentionDays, 1, 365, nameof(RetentionDays));
        ValidateRange(MaxFileBytes, 1024 * 1024, 256L * 1024 * 1024, nameof(MaxFileBytes));
        ValidateRange(MaxTotalBytes, 2 * MaxFileBytes, 2L * 1024 * 1024 * 1024, nameof(MaxTotalBytes));
        ValidateRange(ChannelCapacity, 64, 65536, nameof(ChannelCapacity));
        ValidateRange(SampleIntervalSeconds, 5, 300, nameof(SampleIntervalSeconds));
        ValidateRange(FlushIntervalSeconds, 1, 30, nameof(FlushIntervalSeconds));
        ValidateRange(ShutdownFlushSeconds, 1, 30, nameof(ShutdownFlushSeconds));
        ValidateRange(SlowOperationMs, 100, 300000, nameof(SlowOperationMs));
        ValidateRange(SlowUpdateMs, 100, 300000, nameof(SlowUpdateMs));
        return new LatencyTelemetryOptions
        {
            Enabled = Enabled, RetentionDays = RetentionDays, MaxFileBytes = MaxFileBytes,
            MaxTotalBytes = MaxTotalBytes, ChannelCapacity = ChannelCapacity,
            SampleIntervalSeconds = SampleIntervalSeconds, FlushIntervalSeconds = FlushIntervalSeconds,
            ShutdownFlushSeconds = ShutdownFlushSeconds, SlowOperationMs = SlowOperationMs, SlowUpdateMs = SlowUpdateMs
        };
    }

    /// <summary>Rejects a nonfinite or out-of-range resource limit without changing its value.</summary>
    /// <param name="value">Configured value in the units documented on its property.</param>
    /// <param name="minimum">Inclusive lower resource bound.</param>
    /// <param name="maximum">Inclusive upper resource bound.</param>
    /// <param name="name">Internal configuration property name, never user input.</param>
    /// <exception cref="ArgumentOutOfRangeException">The supplied value is not finite or is outside the bounds.</exception>
    private static void ValidateRange(double value, double minimum, double maximum, string name)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, value, $"Must be between {minimum} and {maximum}.");
    }
}
