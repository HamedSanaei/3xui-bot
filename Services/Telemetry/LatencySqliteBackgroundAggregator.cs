namespace Adminbot.Services.Telemetry;

/// <summary>Counts healthy uncorrelated SQLite boundaries in fixed atomic duration histograms before event allocation.</summary>
/// <remarks>Each completion increments exactly one counter without locks, retries, I/O or channel admission. Categories and operations have fixed cardinality; unknown values collapse into background/local buckets. Only the writer drains counters, so every completion belongs to exactly one snapshot. Histogram bounds, not exact individual durations, are retained.</remarks>
internal sealed class LatencySqliteBackgroundAggregator
{
    /// <summary>Fixed microsecond-to-minute logarithmic histogram, covering every allowed slow threshold.</summary>
    internal const int BucketCount = 30;
    /// <summary>Upper duration bounds in milliseconds; schema-one aggregate readers use these same fixed bounds.</summary>
    private static readonly double[] UpperBounds = Enumerable.Range(0, BucketCount).Select(index => 0.001 * (1L << index)).ToArray();
    /// <summary>Fixed command, transaction-boundary and logical-operation dimensions.</summary>
    private static readonly string[] Operations =
    [
        "sqlite_read", "sqlite_write", "transaction_begin", "transaction_commit", "transaction_rollback",
        "savepoint_create", "savepoint_rollback", "savepoint_release", "transaction_lifetime",
        "inbox", "wallet", "payment", "xui_operation", "persistence", "lookup", "sqlite_local"
    ];
    /// <summary>Fixed worker ownership categories; untagged or unrecognized work uses the last bucket.</summary>
    private static readonly string[] Categories =
    [
        "tenant_manual_receipt_notification", "tenant_storefront_funding_alert", "tenant_discount_reservation",
        "payment_settlement_notification", "xui_renewal_recovery", "sqlite_background"
    ];
    /// <summary>Constant-size counters, indexed by worker, boundary and duration bin; disabled collection creates none.</summary>
    private readonly long[] _counts = new long[Categories.Length * Operations.Length * BucketCount];

    /// <summary>Adds one known-fast completed background boundary with no retained event or ambient identity.</summary>
    /// <param name="operation">Compile-time command, transaction or allowlisted logical category; unknown values become sqlite_local.</param>
    /// <param name="category">Optional fixed worker category; null or unknown values become sqlite_background.</param>
    /// <param name="durationMs">Validated nonnegative finite elapsed milliseconds below the configured slow threshold.</param>
    /// <remarks>The service establishes success, lack of correlation and retry-free execution before calling. One atomic increment is the entire mutable producer operation.</remarks>
    /// <example><code>aggregator.Add("sqlite_read", LatencySqliteOperationScope.Current, durationMs);</code></example>
    internal void Add(string operation, string category, double durationMs)
    {
        var operationIndex = Array.IndexOf(Operations, operation);
        if (operationIndex < 0) operationIndex = Operations.Length - 1;
        var categoryIndex = Array.IndexOf(Categories, category);
        if (categoryIndex < 0) categoryIndex = Categories.Length - 1;
        var bucket = Array.BinarySearch(UpperBounds, durationMs);
        if (bucket < 0) bucket = ~bucket;
        Interlocked.Increment(ref _counts[(categoryIndex * Operations.Length + operationIndex) * BucketCount + Math.Min(bucket, BucketCount - 1)]);
    }

    /// <summary>Drains each atomic bin once into bounded writer-owned immutable snapshots.</summary>
    /// <param name="timestampUtc">Writer's UTC snapshot instant; never an individual operation timestamp.</param>
    /// <param name="windowSeconds">Nonnegative measured seconds since the previous drain.</param>
    /// <returns>At most ninety-six populated worker/boundary snapshots; an idle drain creates no event or histogram array.</returns>
    /// <remarks>Concurrent increments are assigned to this or the next drain, never dropped or counted twice. Independent bins represent a sampled window rather than a simultaneous database snapshot.</remarks>
    internal IEnumerable<LatencyTelemetryEvent> Drain(DateTime timestampUtc, double windowSeconds)
    {
        for (var group = 0; group < Categories.Length * Operations.Length; group++)
        {
            long[] counts = null;
            long total = 0;
            for (var bucket = 0; bucket < BucketCount; bucket++)
            {
                var count = Interlocked.Exchange(ref _counts[group * BucketCount + bucket], 0);
                if (count == 0) continue;
                counts ??= new long[BucketCount];
                counts[bucket] = count;
                total += count;
            }
            if (counts == null) continue;
            var operation = Operations[group % Operations.Length];
            yield return new LatencyTelemetryEvent
            {
                EventType = "sqlite_background_aggregate", TimestampUtc = timestampUtc,
                Category = Categories[group / Operations.Length], Operation = operation,
                Stage = operation is "sqlite_read" or "sqlite_write" ? operation
                    : operation == "transaction_lifetime" ? "sqlite_transaction" : null,
                Outcome = "completed", TimingQuality = "aggregated_histogram_upper_bounds",
                ObservationCount = total, DurationBucketCounts = counts, WindowSeconds = windowSeconds
            };
        }
    }

    /// <summary>Releases remaining histogram counts during aborted or unstarted shutdown without creating event snapshots.</summary>
    /// <returns>The number of populated aggregate records discarded, not an extrapolated count of lost individual detail lines.</returns>
    /// <remarks>Only shutdown cleanup calls this method after admission closes; its work and memory are bounded by the fixed counter array.</remarks>
    internal long Discard()
    {
        long discarded = 0;
        for (var group = 0; group < Categories.Length * Operations.Length; group++)
        {
            var populated = false;
            for (var bucket = 0; bucket < BucketCount; bucket++)
                populated |= Interlocked.Exchange(ref _counts[group * BucketCount + bucket], 0) != 0;
            if (populated) discarded++;
        }
        return discarded;
    }

    /// <summary>Gets the schema-one upper bound for one fixed duration histogram bin.</summary>
    /// <param name="bucket">Zero-based validated index below BucketCount.</param>
    /// <returns>The bin's upper duration bound in milliseconds; the first bin includes zero.</returns>
    /// <remarks>Reports must label these measurements approximate rather than claiming individual maximum durations.</remarks>
    internal static double UpperBound(int bucket) => UpperBounds[bucket];

    /// <summary>Validates an immutable histogram and its exact contributing-operation count without allocation.</summary>
    /// <param name="counts">Required fixed-length nonnegative histogram counts.</param>
    /// <param name="observationCount">Positive exact count of successful boundaries represented by the histogram.</param>
    /// <returns>True only when all bins are valid, the sum cannot overflow and equals the advertised count.</returns>
    /// <remarks>Rejecting malformed summaries prevents unsafe or fabricated percentile weights from reaching storage and reporting.</remarks>
    internal static bool IsValid(ReadOnlySpan<long> counts, long? observationCount)
    {
        if (counts.Length != BucketCount || observationCount is not > 0) return false;
        long total = 0;
        foreach (var count in counts)
        {
            if (count < 0 || count > long.MaxValue - total) return false;
            total += count;
        }
        return total == observationCount;
    }
}
