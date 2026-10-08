using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Adminbot.Services.Telemetry;

/// <summary>Reads local versioned telemetry without constructing configuration, a host, or any bot client.</summary>
/// <remarks>All state is bounded. Unknown text is withheld, and only explicitly projected identifier and vocabulary fields reach output.</remarks>
public static class LatencyTelemetryReportCli
{
    /// <summary>The exact non-serving command selector.</summary>
    public const string Mode = "telemetry-report";
    /// <summary>Maximum UTF-8 bytes in one accepted record; larger lines are drained without allocation.</summary>
    private const int MaxLineBytes = 256 * 1024;
    /// <summary>Cardinality limits prevent hostile or long-lived archives from growing report memory.</summary>
    private const int MaxBots = 128, MaxGroups = 512, MaxTraces = 20000, MaxFiles = 4096, MaxEpisodes = 100, MaxSessions = 512;
    /// <summary>Closed stage vocabulary; unknown stage names are never printed.</summary>
    private static readonly HashSet<string> Stages = new(StringComparer.Ordinal)
    {
        "telegram_send", "telegram_edit", "telegram_upload", "telegram_callback_ack", "telegram_polling",
        "telegram_lookup", "telegram_probe", "sqlite_read", "sqlite_write", "sqlite_busy_retry", "database_wait",
        "payment_gateway", "xui_read", "xui_write", "external_http", "lock_wait", "business_processing",
        "telegram_membership", "site_lookup",
        "unattributed", "admission", "admission_persistence", "queue_wait", "dispatch_delay", "claim", "final_persistence",
        "receiver_checks", "claim_to_handler", "post_handler", "admission_non_db_time", "admission_total",
        "sqlite_transaction", "handler_start", "handler_end", "business_recovery"
    };
    /// <summary>Telegram methods admitted to public report labels, never URLs or SDK payloads.</summary>
    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
    {
        "getUpdates", "sendMessage", "editMessageText", "editMessageCaption", "editMessageReplyMarkup", "sendPhoto",
        "sendDocument", "sendMediaGroup", "answerCallbackQuery", "deleteMessage", "deleteMessages", "getMe",
        "getChat", "getChatMember", "getChatAdministrators", "getFile", "sendVideo", "sendAnimation", "sendAudio",
        "sendVoice", "sendSticker", "sendPoll", "copyMessage", "forwardMessage", "setMyCommands", "setWebhook",
        "editMessageMedia", "getChatMemberCount", "copyMessages", "setChatMenuButton", "setMyDescription",
        "setMyShortDescription", "getUserProfilePhotos",
        "deleteWebhook", "getWebhookInfo", "pinChatMessage", "unpinChatMessage", "sendChatAction", "other", "unknown"
    };
    /// <summary>Closed outcome and failure labels shared by report sections.</summary>
    private static readonly HashSet<string> Labels = new(StringComparer.Ordinal)
    {
        "success", "succeeded", "completed", "failed", "failure", "cancelled", "canceled", "timeout", "timed_out",
        "foreground_timeout", "foreground_budget", "caller", "shutdown", "http_timeout", "transport_timeout",
        "network", "network_error", "http_error", "api_error", "transient", "permanent", "conflict", "rate_limit",
        "transport_failure", "tls", "tls_record_integrity", "connection", "dns", "socket", "unknown", "none",
        "not_modified", "recovered", "incomplete", "complete", "partial", "unavailable", "recovery", "handler_failed",
        "foreground_deadline", "transport", "api", "caller_cancelled", "application_shutdown", "read", "write",
        "select", "insert", "update", "delete", "transaction", "commit", "rollback", "begin", "busy", "locked",
        "http_429", "http_5xx", "telegram_api_rejection", "caller_cancellation", "callback_policy_timeout",
        "transport_cancellation", "unexpected", "uncertain", "degraded", "measured_wall_time", "provider_duration",
        "monotonic", "recovered_estimate", "recovered_unavailable", "persisted_utc_estimate", "retrying",
        "foreground_budget_expired", "telegram_api_error", "transport_error", "unexpected_error", "in_progress",
        "measured_wall_time_not_cpu", "inclusive_wall_time", "metadata_capacity_exceeded", "inbox", "wallet",
        "payment", "xui_operation", "persistence", "lookup", "sqlite_local",
        "foreground_budget_timeout", "startup_probe_timeout", "http_rejection", "execution_failed", "execution_cancelled",
        "creation_requires_review", "bot_transport_unavailable", "process_interrupted",
        "process_interrupted_pre_restart_timing_unavailable", "pre_claim_timing_unavailable", "receiver_control_path_no_inbox",
        "aborted", "boundary_end_unavailable", "live_handler_interrupted_timing_incomplete", "commit_observation_after_claim",
        "no_running_claim", "recovered_during_shutdown", "inclusive_transaction_lifetime_not_handler_stage",
        "transaction_end_unavailable_connection_cleanup", "rolled_back", "disposed",
        "tenant_manual_receipt_notification", "tenant_storefront_funding_alert", "tenant_discount_reservation",
        "payment_settlement_notification", "xui_renewal_recovery", "sqlite_background"
    };
    /// <summary>Recognized v1 event families; unknown families count as quality loss instead of being echoed.</summary>
    private static readonly HashSet<string> Events = new(StringComparer.Ordinal)
    {
        "telegram_update_received", "telegram_update_admission_started", "telegram_update_persisted", "telegram_update_waiting",
        "telegram_update_claimed", "telegram_update_handler_started", "telegram_update_first_response_attempt",
        "telegram_update_first_response_completed", "telegram_update_handler_completed", "telegram_update_inbox_completed",
        "telegram_update_completed", "telegram_request_completed", "telegram_foreground_request_completed", "latency_stage_completed",
        "unattributed_handler_time", "sqlite_operation_completed", "sqlite_busy_retry", "sqlite_transaction_completed",
        "telegram_poll_completed", "telegram_poll_failed", "telegram_poll_recovered", "telegram_poll_backoff",
        "telegram_receiver_started", "telegram_receiver_stopped", "telegram_receiver_health", "process_health", "telemetry_loss",
        "telemetry_writer_failure", "telemetry_started", "telemetry_stopped", "telemetry_incident",
        "telegram_api_request_completed", "telegram_receiver_startup", "telegram_update_first_response_acknowledged",
        "telegram_timeline_metadata_lost"
    };
    /// <summary>Closed SQLite boundary categories; logical caller names not on this list remain other.</summary>
    private static readonly HashSet<string> DatabaseOperations = new(StringComparer.Ordinal)
    {
        "sqlite_read", "sqlite_write", "transaction_begin", "transaction_commit", "transaction_rollback",
        "savepoint_create", "savepoint_rollback", "savepoint_release", "transaction", "transaction_lifetime",
        "inbox", "wallet", "payment", "xui_operation", "persistence", "lookup", "sqlite_local"
    };
    /// <summary>Closed lifecycle duration projections, distinct from inclusive handler summaries.</summary>
    private static readonly (string Field, string Stage)[] TimelineFields =
    {
        ("receiverToAdmissionMs", "receiver_checks"), ("admissionPersistenceMs", "admission_persistence"),
        ("queueWaitMs", "queue_wait"), ("dispatchDelayMs", "dispatch_delay"),
        ("claimMs", "claim"), ("claimedToHandlerMs", "claim_to_handler"), ("finalPersistenceMs", "final_persistence"),
        ("postHandlerMs", "post_handler")
    };

    /// <summary>Recognizes only the exact report selector so serving arguments cannot accidentally select reporting.</summary>
    /// <param name="args">Raw application arguments, optionally null.</param>
    /// <returns>True when the exact ordinal selector is present.</returns>
    /// <example><code>if (LatencyTelemetryReportCli.IsRequested(args)) return;</code></example>
    public static bool IsRequested(IReadOnlyCollection<string> args) => args?.Contains(Mode, StringComparer.Ordinal) == true;

    /// <summary>Executes a read-only, bounded-memory report over v1 JSONL telemetry.</summary>
    /// <param name="args">Exact selector and optional --hours (1..8760), --bot, and --directory arguments; duplicates are refused.</param>
    /// <param name="output">Required output destination; no arbitrary record text or exception messages are written.</param>
    /// <param name="token">Cancellation propagated through scanning and report generation.</param>
    /// <returns>0 for a completed report, 2 for invalid arguments, 1 for inaccessible storage, or 130 for cancellation.</returns>
    /// <remarks>The default directory is AppContext.BaseDirectory/Data/Telemetry. No files are modified. Quantiles approximate available application latency only; duplicate admissions are excluded. Top twenty ranks exact application durations or explicitly labeled recovered-handler-only durations when receiver origin is unavailable.</remarks>
    /// <exception cref="ArgumentNullException">The output writer is null.</exception>
    /// <example><code>await LatencyTelemetryReportCli.RunAsync(new[] { "telemetry-report", "--hours", "24", "--bot", "GozargahNetwork_Bot" }, Console.Out, cancellationToken);</code></example>
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!TryParse(args, out var hours, out var bot, out var directory, out var error))
        {
            await output.WriteLineAsync("Telemetry report: INVALID_ARGUMENTS (" + error + ")");
            return 2;
        }
        try
        {
            token.ThrowIfCancellationRequested();
            if (!Directory.Exists(directory))
            {
                await output.WriteLineAsync("Telemetry report: DIRECTORY_UNAVAILABLE");
                return 1;
            }
            var report = new Report(DateTime.UtcNow, hours, bot);
            var files = new List<string>();
            foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                var info = new FileInfo(path);
                if (info.LinkTarget != null || (info.Attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
                { report.Quality.SkippedUnsafeFiles++; continue; }
                // Unix FIFOs/sockets/devices have zero stat length. Do not open one just because its name ends in JSONL.
                // Empty regular writer files are also skipped; private archive permissions are the mutation trust boundary.
                if (info.Length == 0) { report.Quality.EmptyOrSpecialFiles++; continue; }
                if (files.Count == MaxFiles) { report.Quality.FileLimit++; continue; }
                files.Add(path);
            }
            files.Sort(StringComparer.Ordinal);
            var buffer = new byte[16 * 1024];
            var line = new byte[MaxLineBytes];
            foreach (var path in files)
            {
                token.ThrowIfCancellationRequested();
                try { await ScanFileAsync(path, buffer, line, report, token); }
                catch (IOException) { report.Quality.UnreadableFiles++; }
                catch (UnauthorizedAccessException) { report.Quality.UnreadableFiles++; }
            }
            await report.WriteAsync(output, token);
            return report.Quality.UnreadableFiles > 0 ? 1 : 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await output.WriteLineAsync("Telemetry report: CANCELLED");
            return 130;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            await output.WriteLineAsync("Telemetry report: STORAGE_UNAVAILABLE");
            return 1;
        }
    }

    /// <summary>Validates arguments before accessing storage without echoing supplied values.</summary>
    /// <param name="args">Raw command arguments.</param><param name="hours">Validated lookback hours.</param>
    /// <param name="bot">Optional validated bot filter, never a token.</param><param name="directory">Resolved storage path.</param>
    /// <param name="error">Stable secret-free error code.</param><returns>True only for a complete supported argument list.</returns>
    /// <remarks>Option names are ordinal. Both relative and absolute directory paths resolve against the current directory.</remarks>
    private static bool TryParse(string[] args, out int hours, out string bot, out string directory, out string error)
    {
        hours = 24; bot = null; directory = Path.Combine(AppContext.BaseDirectory, "Data", "Telemetry"); error = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (args == null) { error = "missing_arguments"; return false; }
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (option == null || !seen.Add(option)) { error = "duplicate_or_invalid_option"; return false; }
            if (option == Mode) continue;
            if (option is not ("--hours" or "--bot" or "--directory")) { error = "unknown_option"; return false; }
            if (++i == args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
            { error = "missing_value"; return false; }
            if (option == "--hours")
            {
                if (!int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out hours) || hours is < 1 or > 8760)
                { error = "invalid_hours"; return false; }
            }
            else if (option == "--bot")
            {
                if (!IsIdentifier(args[i])) { error = "invalid_bot"; return false; }
                bot = args[i];
            }
            else
            {
                try { directory = Path.GetFullPath(args[i]); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                { error = "invalid_directory"; return false; }
            }
        }
        if (!seen.Contains(Mode)) { error = "missing_mode"; return false; }
        return true;
    }

    /// <summary>Reads bytes into fixed buffers and drains oversized lines, tolerating active writer truncation.</summary>
    /// <param name="path">Input archive path, never printed.</param><param name="buffer">Reusable read buffer.</param>
    /// <param name="line">Reusable capped record buffer.</param><param name="report">Bounded accumulator.</param>
    /// <param name="token">Scan cancellation.</param><returns>A task completing when the file snapshot reaches EOF.</returns>
    /// <remarks>An unterminated final line is counted as truncated and excluded, even if it currently parses.</remarks>
    private static async Task ScanFileAsync(string path, byte[] buffer, byte[] line, Report report, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget != null || (info.Attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
        { report.Quality.SkippedUnsafeFiles++; return; }
        if (info.Length == 0) { report.Quality.EmptyOrSpecialFiles++; return; }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
        report.Quality.Files++;
        var remaining = stream.Length;
        var length = 0;
        var oversized = false;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
            if (read == 0) break;
            remaining -= read;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == (byte)'\n')
                {
                    report.Quality.Lines++;
                    if (oversized) report.Quality.Oversized++;
                    else if (length > 0) report.Accept(line.AsMemory(0, length));
                    else report.Quality.Empty++;
                    length = 0; oversized = false;
                }
                else if (!oversized)
                {
                    if (length == line.Length) oversized = true;
                    else line[length++] = buffer[i];
                }
            }
            token.ThrowIfCancellationRequested();
        }
        if (length > 0 || oversized)
        {
            report.Quality.Lines++; report.Quality.Truncated++;
            if (oversized) report.Quality.Oversized++;
        }
    }

    /// <summary>Checks bounded ASCII identifiers, rejecting whitespace, controls, tokens, and URLs.</summary>
    /// <param name="value">Untrusted identifier.</param><returns>True for 1..80 ASCII alphanumeric, underscore, or hyphen characters.</returns>
    private static bool IsIdentifier(string value) => !string.IsNullOrEmpty(value) && value.Length <= 80 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    /// <summary>Preserves fixed known runtime identities and pseudonymizes every other archive label.</summary>
    /// <param name="value">Untrusted archive bot id; suffixes and operator filters do not establish a trusted registry source.</param>
    /// <returns>A fixed known bot identity or stable opaque alias, never arbitrary archive customer text.</returns>
    /// <remarks>All bots remain separately grouped. --bot filters by the stored operational id but does not authorize
    /// echoing an unknown label; this also prevents disclosure when a secret resembles a valid selector.</remarks>
    private static string BotLabel(string value)
    {
        if (value is "vpnetiranbot" or "GozargahNetwork_Bot" or "sales-assistant") return value;
        Span<byte> identity = stackalloc byte[384]; // Text admits at most 128 UTF-16 characters.
        var length = Encoding.UTF8.GetBytes(value.AsSpan(), identity);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(identity[..length], hash);
        return "bot#" + Convert.ToHexString(hash[..8]);
    }

    /// <summary>Decodes one field from the capped JSON line and admits at most 128 characters to retained state.</summary>
    /// <param name="record">Parsed object from a line already bounded to MaxLineBytes.</param><param name="name">Compile-time property name.</param><returns>A bounded string or null for unavailable/oversized fields.</returns>
    /// <remarks>JsonElement exposes string decoding, not Utf8JsonReader.CopyString. Temporary decoded memory is bounded by the input line; oversized strings never reach report state or output.</remarks>
    private static string Text(JsonElement record, string name)
    {
        if (!record.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return text?.Length <= 128 ? text : null;
    }
    /// <summary>Projects a string onto a closed output vocabulary.</summary>
    /// <param name="value">Untrusted string.</param><param name="allowed">Compile-time allowlist.</param><returns>An allowed label or other.</returns>
    private static string Label(string value, HashSet<string> allowed) => value != null && allowed.Contains(value) ? value : "other";
    /// <summary>Reads nonnegative finite millisecond measurements, excluding implausible values above seven days.</summary>
    /// <param name="record">Parsed object.</param><param name="name">Compile-time property name.</param><returns>A valid measurement or null, never fabricated zero.</returns>
    private static double? Number(JsonElement record, string name) => record.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var value) && double.IsFinite(value) && value >= 0 && value <= 604800000 ? value : null;
    /// <summary>Reads a nonnegative integer counter without rounding fractional values.</summary>
    /// <param name="record">Parsed object.</param><param name="name">Compile-time property name.</param><returns>The integer or zero when unavailable.</returns>
    private static long Count(JsonElement record, string name) => record.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v) && v >= 0 ? v : 0;
    /// <summary>Reads explicit UTC or offset timestamps; local unspecified times are refused.</summary>
    /// <param name="record">Parsed object.</param><param name="name">Compile-time property name.</param><returns>UTC timestamp or null.</returns>
    private static DateTime? Utc(JsonElement record, string name)
    {
        var text = Text(record, name);
        return text != null && (text.EndsWith('Z') || text.Length >= 6 && text[^3] == ':' && text[^6] is '+' or '-') &&
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date.UtcDateTime : null;
    }
    /// <summary>Formats milliseconds invariantly, retaining missing data as unavailable.</summary>
    /// <param name="value">Measurement in milliseconds, optionally unavailable.</param><returns>A safe numeric label.</returns>
    private static string Ms(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unavailable";
    /// <summary>Formats UTC timestamps without culture-dependent or input-controlled text.</summary>
    /// <param name="value">UTC instant, optionally unavailable.</param><returns>An ISO UTC label.</returns>
    private static string Time(DateTime? value) => value?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) ?? "unavailable";

    /// <summary>Fixed logarithmic histogram; bucket upper bounds approximate quantiles within 5% above 0.1 ms.</summary>
    private sealed class Histogram
    {
        /// <summary>Fixed per-bucket observation counts, independent of input length.</summary>
        private readonly long[] buckets = new long[512];
        /// <summary>Precomputed bucket boundary values avoid repeated logarithm/exponential work during streaming.</summary>
        private static readonly double[] UpperBounds = Enumerable.Range(0, 512).Select(i => 0.1 * Math.Pow(1.05, i)).ToArray();
        /// <summary>Exact number of accepted duration observations.</summary>
        public long Count;
        /// <summary>Exact maximum accepted milliseconds; unavailable until Count is nonzero.</summary>
        public double Max;
        /// <summary>Adds one validated measurement without retaining individual observations.</summary>
        /// <param name="value">Nonnegative milliseconds.</param>
        public void Add(double value)
        {
            var index = Array.BinarySearch(UpperBounds, value);
            if (index < 0) index = ~index;
            buckets[Math.Min(index, 511)]++; Count++; Max = Math.Max(Max, value);
        }
        /// <summary>Computes a rank's histogram upper bound, capped at the exact maximum.</summary>
        /// <param name="fraction">Requested percentile in 0..1.</param><returns>Approximate milliseconds or unavailable for no data.</returns>
        public double? Quantile(double fraction)
        {
            if (Count == 0) return null;
            var rank = (long)Math.Ceiling(Count * fraction); long sum = 0;
            for (var i = 0; i < buckets.Length; i++) { sum += buckets[i]; if (sum >= rank) return Math.Min(Max, UpperBounds[i]); }
            return Max;
        }
        /// <summary>Formats all required aggregate latency metrics.</summary>
        /// <returns>Safe invariant count/percentiles/max labels.</returns>
        public string Summary() => $"count={Count} P50~={Ms(Quantile(.50))} P95~={Ms(Quantile(.95))} P99~={Ms(Quantile(.99))} max={Ms(Count == 0 ? null : Max)} ms";
    }

    /// <summary>Per-bot aggregates keep update metrics independent of request and poll counts.</summary>
    private sealed class BotStats
    {
        /// <summary>Histogram of only available receiver-to-completion measurements.</summary>
        public readonly Histogram Application = new();
        /// <summary>Summed exclusive and labelled lifecycle diagnostics; not a count of bottleneck occurrences.</summary>
        public readonly Dictionary<string, double> StageTotals = new(StringComparer.Ordinal);
        /// <summary>One longest known noninclusive category per executed summary, producing the requested most-frequent bottleneck.</summary>
        public readonly Dictionary<string, long> BottleneckCounts = new(StringComparer.Ordinal);
        /// <summary>Exact executed-summary, known-slow, foreground-timeout, poll-failure, missing-stage and recovery counts; duplicate admissions are excluded.</summary>
        public long Updates, Slow, Timeouts, PollFailures, MissingStages, Recovered;
    }
    /// <summary>Request or database group aggregates with exact failure, slow, and busy counters.</summary>
    private sealed class Group
    {
        /// <summary>Bounded latency histogram for this safe group.</summary>
        public readonly Histogram Duration = new();
        /// <summary>Exact group event, failure, slow, contention and retry counts.</summary>
        public long Events, Failed, Slow, Busy, Retries;
        /// <summary>Total wait milliseconds from explicit busy-retry events, never duplicate terminal summaries.</summary>
        public double BusyWait;
        /// <summary>Exact counts keyed only by the closed outcome vocabulary.</summary>
        public readonly Dictionary<string, long> Outcomes = new(StringComparer.Ordinal);
    }
    /// <summary>Bounded rolling polling episode with a capped failure classification sequence.</summary>
    private sealed class Episode
    {
        /// <summary>Safe bot label owning this polling episode.</summary>
        public string Bot;
        /// <summary>Degraded start and most recently observed failure UTC instants.</summary>
        public DateTime Start, Last;
        /// <summary>UTC successful recovery, null while open.</summary>
        public DateTime? End;
        /// <summary>Observed failures; explicit recovery counts fill episodes whose start precedes the window.</summary>
        public long Failures;
        /// <summary>Observed monotonic recovery milliseconds when the writer supplied them.</summary>
        public double? RecoveryMs;
        /// <summary>First sixteen allowlisted failure classifications, optionally with valid HTTP codes.</summary>
        public readonly List<string> Sequence = new();
        /// <summary>Number of subsequent sequence entries omitted by the cap.</summary>
        public long SequenceOmitted;
    }
    /// <summary>Only safe projected fields of an exact top-twenty update summary are retained.</summary>
    /// <param name="Bot">Safe bot label.</param><param name="Trace">Validated opaque correlation id or unavailable.</param>
    /// <param name="Timestamp">Observation UTC time.</param><param name="Duration">Exact ranking milliseconds: application if available, otherwise recovered handler only.</param>
    /// <param name="Application">Application milliseconds, null when the receiver origin is unavailable.</param>
    /// <param name="RankingBasis">Closed application or handler_only label describing comparable known time, not inferred end-to-end latency.</param>
    /// <param name="Outcome">Closed outcome label.</param><param name="Breakdown">Only projected bounded timing fields.</param>
    private sealed record SlowUpdate(string Bot, string Trace, DateTime Timestamp, double Duration, double? Application, string RankingBasis, string Outcome, string Breakdown);
    /// <summary>Explicit quality accounting makes every bounded or unavailable portion visible.</summary>
    private sealed class Quality
    {
        /// <summary>Exact file, record, coverage, loss-gauge and capacity-omission counters for the scan.</summary>
        public long Files, Lines, Empty, Malformed, Truncated, Oversized, UnknownSchema, UnknownEvent, InvalidTimestamp, OutsideWindow,
            Filtered, Accepted, UnreadableFiles, FileLimit, BotLimit, GroupLimit, TraceLimit, MissingTrace, MissingApplication,
            MissingStages, LossEvents, WriterFailureEvents, Dropped, WriterFailures, EpisodeLimit, OutOfOrder,
            FullChannelDropped, WriteDropped, Rejected, ShutdownDropped;
        /// <summary>Rejected links/unsafe entries and zero-length regular or special entries skipped before file opening.</summary>
        public long SkippedUnsafeFiles, EmptyOrSpecialFiles;
        /// <summary>Counter records without trusted session provenance, plus counter records omitted by the session cap.</summary>
        public long LossCounterRecordsWithoutSession, SessionLimit;
        /// <summary>Duplicate-admission terminal summaries excluded from execution latency and update counts.</summary>
        public long DuplicateSummaries;
    }

    /// <summary>Fixed per-process cumulative-counter maxima; repeated health snapshots never add duplicate loss.</summary>
    private sealed class SessionGauges
    {
        /// <summary>Maxima corresponding to the compile-time CounterFields projection.</summary>
        public readonly long[] Values = new long[6];
    }
    /// <summary>Ordered allowlist of cumulative per-process loss and writer counters.</summary>
    private static readonly string[] CounterFields =
    {
        "droppedEvents", "writerFailures", "fullChannelDroppedEvents",
        "writeDroppedEvents", "rejectedEvents", "shutdownDroppedEvents"
    };

    /// <summary>Bounded streaming aggregation and safe human-readable reporting.</summary>
    private sealed class Report
    {
        /// <summary>Scan quality counters shared by byte scanning and record projection.</summary>
        public readonly Quality Quality = new();
        /// <summary>Frozen UTC report window.</summary>
        private readonly DateTime until, since;
        /// <summary>Optional validated raw bot filter, never echoed.</summary>
        private readonly string filter;
        /// <summary>At most 128 owning-bot aggregate rows.</summary>
        private readonly Dictionary<string, BotStats> bots = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>At most 512 Telegram bot/method/boundary latency series.</summary>
        private readonly Dictionary<string, Group> telegram = new(StringComparer.Ordinal);
        /// <summary>At most 512 SQLite bot/category/event latency series.</summary>
        private readonly Dictionary<string, Group> database = new(StringComparer.Ordinal);
        /// <summary>At most 20000 safe trace keys and compact milestone/final-summary flags.</summary>
        private readonly Dictionary<string, byte> traces = new(StringComparer.Ordinal);
        /// <summary>At most 512 validated process-session ids and cumulative loss-counter maxima.</summary>
        private readonly Dictionary<string, SessionGauges> sessions = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>At most one active failure episode per admitted bot.</summary>
        private readonly Dictionary<string, Episode> activeEpisodes = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>First 100 observed polling episodes, with omissions counted explicitly.</summary>
        private readonly List<Episode> episodes = new();
        /// <summary>Largest twenty known durations, using application time or recovered-handler-only fallback with an explicit ranking basis.</summary>
        private readonly List<SlowUpdate> top = new();
        /// <summary>Freezes the report window once, avoiding drift across large files.</summary>
        /// <param name="now">End of the UTC window.</param><param name="hours">Lookback hours.</param><param name="bot">Optional raw internal bot filter.</param>
        public Report(DateTime now, int hours, string bot) { until = now; since = now.AddHours(-hours); filter = bot; }

        /// <summary>Consumes one complete JSON line with strict schema and safe-field projection.</summary>
        /// <param name="line">Bounded UTF-8 JSON line, valid only for this call.</param>
        /// <remarks>Parsing failures are quality counts, not exceptions or raw-output diagnostics.</remarks>
        public void Accept(ReadOnlyMemory<byte> line)
        {
            try
            {
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
                var r = document.RootElement;
                if (r.ValueKind != JsonValueKind.Object) { Quality.Malformed++; return; }
                if (!r.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != 1)
                { Quality.UnknownSchema++; return; }
                var type = Text(r, "eventType");
                if (type == null || !Events.Contains(type)) { Quality.UnknownEvent++; return; }
                var utc = Utc(r, "timestampUtc");
                if (!utc.HasValue) { Quality.InvalidTimestamp++; return; }
                if (utc < since || utc > until) { Quality.OutsideWindow++; return; }
                var rawBot = Text(r, "botId");
                var global = rawBot == null || type.StartsWith("telemetry_", StringComparison.Ordinal) || type == "process_health";
                if (!global && filter != null && !string.Equals(rawBot, filter, StringComparison.OrdinalIgnoreCase)) { Quality.Filtered++; return; }
                Quality.Accepted++;
                ObserveLossCounters(r);
                if (type == "telemetry_loss") Quality.LossEvents++;
                if (type == "telemetry_writer_failure") Quality.WriterFailureEvents++;
                if (global)
                {
                    if (type.StartsWith("sqlite_", StringComparison.Ordinal)) AddDatabase(r, type, "global");
                    return;
                }
                var bot = BotLabel(rawBot);
                if (!bots.TryGetValue(bot, out var stats))
                {
                    if (bots.Count >= MaxBots) { Quality.BotLimit++; return; }
                    bots.Add(bot, stats = new BotStats());
                }
                if (type.StartsWith("telegram_update_", StringComparison.Ordinal)) TrackTrace(r, type, bot);
                if (type == "telegram_update_completed") AddUpdate(r, bot, stats, utc.Value);
                else if (type == "telegram_foreground_request_completed")
                {
                    var timeout = Text(r, "timeoutCategory");
                    var outcome = Text(r, "outcome");
                    var source = Text(r, "cancellationSource");
                    if (source is not ("caller" or "shutdown" or "host_shutdown") &&
                        (timeout is "foreground" or "callback_best_effort" or "foreground_budget" or "callback_policy" or "callback_policy_timeout"
                        || outcome is "timeout" or "timed_out" or "foreground_timeout" or "foreground_budget_expired" or "callback_policy_timeout")) stats.Timeouts++;
                }
                else if (type is "telegram_request_completed" or "telegram_api_request_completed") AddTelegram(r, bot, type);
                else if (type.StartsWith("telegram_poll_", StringComparison.Ordinal) || type == "telegram_receiver_health") AddPoll(r, type, bot, stats, utc.Value);
                else if (type.StartsWith("sqlite_", StringComparison.Ordinal)) AddDatabase(r, type, bot);
            }
            catch (JsonException) { Quality.Malformed++; }
            catch (InvalidOperationException) { Quality.Malformed++; }
            catch (FormatException) { Quality.Malformed++; }
        }

        /// <summary>Separates process-session maxima from legacy counters whose restart provenance is unavailable.</summary>
        /// <param name="r">Parsed event containing optional cumulative gauges and a writer-assigned session id.</param>
        /// <remarks>Session ids must be 32 hexadecimal characters. Overflow records are counted, never merged with another session.</remarks>
        private void ObserveLossCounters(JsonElement r)
        {
            var hasCounters = false;
            foreach (var name in CounterFields)
                if (r.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count >= 0)
                { hasCounters = true; break; }
            if (!hasCounters) return;
            var session = Text(r, "sessionId");
            if (session == null || session.Length != 32 || !session.All(char.IsAsciiHexDigit))
            {
                Quality.LossCounterRecordsWithoutSession++;
                Quality.Dropped = Math.Max(Quality.Dropped, Count(r, "droppedEvents"));
                Quality.WriterFailures = Math.Max(Quality.WriterFailures, Count(r, "writerFailures"));
                Quality.FullChannelDropped = Math.Max(Quality.FullChannelDropped, Count(r, "fullChannelDroppedEvents"));
                Quality.WriteDropped = Math.Max(Quality.WriteDropped, Count(r, "writeDroppedEvents"));
                Quality.Rejected = Math.Max(Quality.Rejected, Count(r, "rejectedEvents"));
                Quality.ShutdownDropped = Math.Max(Quality.ShutdownDropped, Count(r, "shutdownDroppedEvents"));
                return;
            }
            if (!sessions.TryGetValue(session, out var gauges))
            {
                if (sessions.Count == MaxSessions) { Quality.SessionLimit++; return; }
                sessions.Add(session, gauges = new SessionGauges());
            }
            for (var i = 0; i < CounterFields.Length; i++)
                gauges.Values[i] = Math.Max(gauges.Values[i], Count(r, CounterFields[i]));
        }

        /// <summary>Sums each admitted process's maximum gauge exactly, without Int64 overflow on hostile numeric inputs.</summary>
        /// <param name="index">Compile-time index into CounterFields.</param><returns>Exact decimal sum across admitted sessions.</returns>
        private decimal SessionTotal(int index) => sessions.Values.Sum(g => (decimal)g.Values[index]);

        /// <summary>Tracks bounded trace identities and final-summary coverage without retaining payloads.</summary>
        /// <param name="r">Parsed event.</param><param name="type">Known event family.</param><param name="bot">Safe bot label.</param>
        private void TrackTrace(JsonElement r, string type, string bot)
        {
            var trace = Trace(r);
            if (trace == "unavailable") { Quality.MissingTrace++; return; }
            var key = bot + "/" + trace;
            if (!traces.TryGetValue(key, out var flags) && traces.Count >= MaxTraces) { Quality.TraceLimit++; return; }
            traces[key] = (byte)(flags | (type == "telegram_update_completed" ? 2 : 1));
        }
        /// <summary>Allows only UUID or hexadecimal correlation ids, never arbitrary customer text.</summary>
        /// <param name="r">Parsed event.</param><returns>Safe trace id or unavailable.</returns>
        private static string Trace(JsonElement r)
        {
            var trace = Text(r, "traceId");
            return trace != null && (trace.Length == 32 && trace.All(char.IsAsciiHexDigit) || trace.Length == 36 && Guid.TryParseExact(trace, "D", out _)) ? trace : "unavailable";
        }
        /// <summary>Excludes duplicate admissions, adds exclusive stage metrics, and ranks available application or recovered-handler time explicitly.</summary>
        /// <param name="r">Completed update summary.</param><param name="bot">Safe bot identity.</param><param name="stats">Bot accumulator.</param><param name="utc">Event UTC time.</param>
        private void AddUpdate(JsonElement r, string bot, BotStats stats, DateTime utc)
        {
            if (Text(r, "outcome") == "duplicate") { Quality.DuplicateSummaries++; return; }
            stats.Updates++;
            var isRecovered = r.TryGetProperty("recovered", out var recovered) && recovered.ValueKind == JsonValueKind.True;
            if (isRecovered) stats.Recovered++;
            var app = Number(r, "applicationMs");
            var ranking = app ?? (isRecovered ? Number(r, "handlerMs") : null);
            if (!app.HasValue) Quality.MissingApplication++;
            else stats.Application.Add(app.Value);
            if (ranking >= 5000) stats.Slow++;
            var retainTop = ranking.HasValue && (top.Count < 20 || ranking > top[^1].Duration);
            var breakdown = retainTop ? new StringBuilder() : null;
            var bottleneck = (Stage: (string)null, Ms: -1d);
            foreach (var (field, stage) in TimelineFields)
            {
                var value = Number(r, field);
                if (value.HasValue)
                {
                    AddStage(stats, breakdown, stage, value.Value);
                    SelectBottleneck(ref bottleneck, stage, value.Value);
                }
            }
            var admission = Number(r, "admissionMs");
            var admissionDb = Number(r, "admissionPersistenceMs");
            if (admission.HasValue)
            {
                // The remainder is measured non-database admission time, not an assumption about CPU or a specific lock.
                var nonDatabaseMs = admissionDb.HasValue ? Math.Max(0, admission.Value - admissionDb.Value) : admission.Value;
                var category = admissionDb.HasValue ? "admission_non_db_time" : "admission_total";
                AddStage(stats, breakdown, category, nonDatabaseMs);
                SelectBottleneck(ref bottleneck, category, nonDatabaseMs);
            }
            var hasStages = false;
            if (r.TryGetProperty("stageMs", out var stages) && stages.ValueKind == JsonValueKind.Object)
            {
                foreach (var stage in Stages)
                {
                    if (!stages.TryGetProperty(stage, out var measurement) || measurement.ValueKind != JsonValueKind.Number || !measurement.TryGetDouble(out var duration) || !double.IsFinite(duration) || duration < 0 || duration > 604800000) continue;
                    AddStage(stats, breakdown, stage, duration);
                    SelectBottleneck(ref bottleneck, stage, duration);
                    hasStages = true;
                }
            }
            if (!hasStages) { stats.MissingStages++; Quality.MissingStages++; }
            var gap = Number(r, "unattributedHandlerMs");
            if (gap.HasValue)
            {
                AddStage(stats, breakdown, "unattributed", gap.Value);
                SelectBottleneck(ref bottleneck, "unattributed", gap.Value);
            }
            if (bottleneck.Stage != null)
                stats.BottleneckCounts[bottleneck.Stage] = stats.BottleneckCounts.GetValueOrDefault(bottleneck.Stage) + 1;
            if (!retainTop) return;
            breakdown.Append("handler=").Append(Ms(Number(r, "handlerMs"))).Append(" firstAttempt=").Append(Ms(Number(r, "firstResponseAttemptMs")))
                .Append(" firstCompleted=").Append(Ms(Number(r, "firstResponseCompletedMs"))).Append(" firstAcknowledged=").Append(Ms(Number(r, "firstResponseAcknowledgedMs")))
                .Append(" callbackAck=").Append(Ms(Number(r, "callbackAckMs"))).Append(" telegramTotal=").Append(Ms(Number(r, "totalTelegramMs")))
                .Append(" maxUnattributedGap=").Append(Ms(Number(r, "maxUnattributedGapMs"))).Append(" timingQuality=").Append(Label(Text(r, "timingQuality"), Labels));
            breakdown.Append(" gapStarted=").Append(Ms(Number(r, "maxGapStartedMs"))).Append(" gapEnded=").Append(Ms(Number(r, "maxGapEndedMs")))
                .Append(" gapBefore=").Append(Label(Text(r, "gapBeforeStage"), Stages)).Append(" gapAfter=").Append(Label(Text(r, "gapAfterStage"), Stages))
                .Append(" persistenceOutcome=").Append(Label(Text(r, "persistenceOutcome"), Labels));
            breakdown.Append(" receivedUtc=").Append(Time(Utc(r, "receivedAtUtc"))).Append(" handlerStartedUtc=").Append(Time(Utc(r, "handlerStartedAtUtc")))
                .Append(" inboxCompletedUtc=").Append(Time(Utc(r, "inboxCompletedAtUtc")));
            top.Add(new SlowUpdate(bot, Trace(r), utc, ranking.Value, app, app.HasValue ? "application" : "handler_only", Label(Text(r, "outcome"), Labels), breakdown.ToString()));
            top.Sort((a, b) => b.Duration.CompareTo(a.Duration));
            if (top.Count > 20) top.RemoveAt(20);
        }
        /// <summary>Selects one measured longest category without counting inclusive handler totals as bottlenecks.</summary>
        /// <param name="current">Current measured winner for one update; a negative duration means unavailable.</param>
        /// <param name="stage">Required controlled lifecycle or exclusive stage label.</param>
        /// <param name="value">Known nonnegative elapsed milliseconds from the parsed summary.</param>
        /// <remarks>Ties use ordinal category order so report results do not depend on dictionary insertion order.</remarks>
        private static void SelectBottleneck(ref (string Stage, double Ms) current, string stage, double value)
        {
            if (value > current.Ms || (value == current.Ms && string.CompareOrdinal(stage, current.Stage) < 0))
                current = (stage, value);
        }

        /// <summary>Adds exclusive stage totals and a bounded safe top-update breakdown.</summary>
        /// <param name="stats">Owning bot metrics.</param><param name="text">Safe output accumulator.</param><param name="stage">Allowlisted stage.</param><param name="value">Validated milliseconds.</param>
        private static void AddStage(BotStats stats, StringBuilder text, string stage, double value)
        {
            stats.StageTotals.TryGetValue(stage, out var previous); stats.StageTotals[stage] = previous + value;
            if (text != null) text.Append(stage).Append('=').Append(Ms(value)).Append(' ');
        }
        /// <summary>Gets a cardinality-capped per-bot method or database group.</summary>
        /// <param name="groups">Target group dictionary.</param><param name="key">Safe projected grouping key.</param><returns>Existing/new group or null when the cap is reached.</returns>
        private Group GetGroup(Dictionary<string, Group> groups, string key)
        {
            if (groups.TryGetValue(key, out var group)) return group;
            if (groups.Count >= MaxGroups) { Quality.GroupLimit++; return null; }
            groups.Add(key, group = new Group()); return group;
        }
        /// <summary>Aggregates headers-only and SDK-validated request records in separate series, never summing them.</summary>
        /// <param name="r">Request record.</param><param name="bot">Safe owning bot label.</param><param name="type">Known headers or SDK record family.</param>
        /// <remarks>Healthy long polling retains its measured duration but never counts as a slow interactive request.</remarks>
        private void AddTelegram(JsonElement r, string bot, string type)
        {
            var method = Label(Text(r, "method"), Methods);
            var group = GetGroup(telegram, bot + "/" + method + "/" + (type == "telegram_request_completed" ? "headers" : "sdk_validated"));
            if (group == null) return;
            AddDuration(group, r, method == "getUpdates" ? double.PositiveInfinity : 2000);
        }
        /// <summary>Adds exact event/outcome counts and histogram latency for one group.</summary>
        /// <param name="group">Bounded group.</param><param name="r">Parsed record.</param><param name="slowMs">Explicit slow threshold in milliseconds.</param>
        private static void AddDuration(Group group, JsonElement r, double slowMs)
        {
            group.Events++;
            var outcome = Label(Text(r, "outcome"), Labels);
            group.Outcomes.TryGetValue(outcome, out var count); group.Outcomes[outcome] = count + 1;
            if (outcome is not ("success" or "succeeded" or "completed" or "not_modified")) group.Failed++;
            var duration = Number(r, "durationMs");
            if (duration.HasValue) { group.Duration.Add(duration.Value); if (duration >= slowMs) group.Slow++; }
        }
        /// <summary>Aggregates SQLite categories and busy incidents without reading SQL or arbitrary operation names.</summary>
        /// <param name="r">Database record.</param><param name="type">Known SQLite family.</param><param name="bot">Safe bot id.</param>
        private void AddDatabase(JsonElement r, string type, string bot)
        {
            var category = Label(Text(r, "category"), Labels);
            if (category == "other") category = Label(Text(r, "stage"), Stages);
            var group = GetGroup(database, bot + "/" + category + "/" + type + "/" + Label(Text(r, "operation"), DatabaseOperations));
            if (group == null) return;
            AddDuration(group, r, 2000);
            var busy = type == "sqlite_busy_retry" || Count(r, "sqliteErrorCode") is 5 or 6;
            if (busy) group.Busy++;
            if (type == "sqlite_busy_retry")
            {
                group.Retries++;
                var wait = Number(r, "durationMs") ?? (Count(r, "busyRetryCount") <= 1 ? Number(r, "busyWaitMs") : null);
                if (wait.HasValue) group.BusyWait += wait.Value;
            }
        }
        /// <summary>Tracks failure sequences and explicit recovered/degraded periods per bot with bounded episode retention.</summary>
        /// <param name="r">Poll or receiver health record.</param><param name="type">Known event family.</param><param name="bot">Safe bot identity.</param><param name="stats">Bot metrics.</param><param name="utc">Event UTC timestamp.</param>
        private void AddPoll(JsonElement r, string type, string bot, BotStats stats, DateTime utc)
        {
            activeEpisodes.TryGetValue(bot, out var episode);
            if (type == "telegram_poll_failed")
            {
                stats.PollFailures++;
                if (episode == null)
                {
                    episode = new Episode { Bot = bot, Start = Utc(r, "degradedSinceUtc") ?? utc, Last = utc };
                    activeEpisodes[bot] = episode;
                    RememberEpisode(episode);
                }
                if (utc < episode.Last) Quality.OutOfOrder++;
                episode.Last = utc; episode.Failures++;
                var failure = Label(Text(r, "failureClassification"), Labels);
                var status = Count(r, "httpStatusCode");
                var safe = failure + (status is >= 100 and <= 599 ? ":HTTP" + status.ToString(CultureInfo.InvariantCulture) : "");
                if (episode.Sequence.Count < 16) episode.Sequence.Add(safe); else episode.SequenceOmitted++;
            }
            else if (type is "telegram_poll_recovered" or "telegram_poll_completed")
            {
                if (episode == null && type == "telegram_poll_recovered")
                {
                    var start = Utc(r, "degradedSinceUtc");
                    var recovery = Number(r, "recoveryMs");
                    episode = new Episode { Bot = bot, Start = start ?? (recovery.HasValue ? utc.AddMilliseconds(-recovery.Value) : utc), Last = utc, Failures = Count(r, "consecutiveFailures") };
                    RememberEpisode(episode);
                }
                if (episode != null)
                {
                    episode.End = utc; episode.RecoveryMs = Number(r, "recoveryMs") ?? Math.Max(0, (utc - episode.Start).TotalMilliseconds);
                    activeEpisodes.Remove(bot);
                }
            }
            else if (type == "telegram_receiver_health" && episode == null && Utc(r, "degradedSinceUtc") is DateTime degraded)
            {
                episode = new Episode { Bot = bot, Start = degraded, Last = utc, Failures = Count(r, "consecutiveFailures") };
                activeEpisodes[bot] = episode; RememberEpisode(episode);
            }
        }
        /// <summary>Retains only a bounded episode sample while keeping aggregate failures exact.</summary>
        /// <param name="episode">New episode; active state remains independently bounded by bot count.</param>
        private void RememberEpisode(Episode episode)
        {
            if (episodes.Count < MaxEpisodes) episodes.Add(episode); else Quality.EpisodeLimit++;
        }

        /// <summary>Writes the six required report groups from allowlisted fields and explicitly labels all approximations and caps.</summary>
        /// <param name="output">Operator output writer.</param><param name="token">Cancellation before each output section/row.</param>
        /// <returns>A task completing after the safe report is written.</returns>
        public async Task WriteAsync(TextWriter output, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            await output.WriteLineAsync($"Telemetry report UTC {Time(since)} .. {Time(until)}");
            await output.WriteLineAsync("Read-only; application latency is local receiver-to-inbox completion, not Telegram server delivery. All durations are ms. Quantiles (~) use 512 fixed log buckets: upper-bound approximation <=5% above 0.1 ms; maxima/top20 exact for accepted records.");
            await output.WriteLineAsync($"Limits: lineBytes={MaxLineBytes} files={MaxFiles} bots={MaxBots} method/category groups={MaxGroups} trackedTraces={MaxTraces} episodes={MaxEpisodes} sessions={MaxSessions} failureSequence=16 top=20. Slow thresholds: updates>=5000 ms, operations>=2000 ms (report thresholds, not runtime configuration).");
            await output.WriteLineAsync("1. Per-bot executed updates (duplicate admissions excluded; quantiles and maximum use available application latency ONLY, missingApplication counts unavailable summaries; slow may use recovered handler time)");
            foreach (var pair in bots.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var s = pair.Value;
                var dominant = s.BottleneckCounts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).FirstOrDefault();
                await output.WriteLineAsync($"bot={pair.Key} updates={s.Updates} {s.Application.Summary()} missingApplication={s.Updates - s.Application.Count} slow={s.Slow} foregroundTimeouts={s.Timeouts} pollFailures={s.PollFailures} bottleneck={(dominant.Key ?? "unavailable")} bottleneckUpdates={dominant.Value} stageTotalMs={Ms(dominant.Key == null ? null : s.StageTotals[dominant.Key])} recovered={s.Recovered} missingStages={s.MissingStages}");
            }
            await output.WriteLineAsync("2. Exact top20 slowest known update durations (rankingBasis=application when available, handler_only for recovered updates without receiver origin; handler-only does NOT claim end-to-end latency. Exclusive stageMs; inclusive handler/telegram references must not be summed)");
            foreach (var update in top)
            {
                token.ThrowIfCancellationRequested();
                await output.WriteLineAsync($"bot={update.Bot} trace={update.Trace} utc={Time(update.Timestamp)} application={Ms(update.Application)} outcome={update.Outcome} rankingBasis={update.RankingBasis} rankingMs={Ms(update.Duration)} {update.Breakdown}");
            }
            await output.WriteLineAsync("3. Telegram requests per bot/method/boundary (headers-only and fully SDK-validated await are separate series, NEVER summed; neither proves customer delivery)");
            foreach (var pair in telegram.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                await output.WriteLineAsync($"bot/method={pair.Key} events={pair.Value.Events} {pair.Value.Duration.Summary()} failed={pair.Value.Failed} slow={pair.Value.Slow} outcomes={string.Join(',', pair.Value.Outcomes.OrderBy(p => p.Key).Select(p => p.Key + ':' + p.Value.ToString(CultureInfo.InvariantCulture)))}");
            }
            await output.WriteLineAsync("4. Polling episodes / failure sequence / recovery / degraded periods (bounded first-episode sample; a poll success closes a failure episode)");
            foreach (var episode in episodes)
            {
                token.ThrowIfCancellationRequested();
                await output.WriteLineAsync($"bot={episode.Bot} degradedSinceUtc={Time(episode.Start)} lastFailureUtc={Time(episode.Last)} recoveredUtc={Time(episode.End)} failures={episode.Failures} recoveryMs={Ms(episode.RecoveryMs)} degradedMs={Ms(Math.Max(0, ((episode.End ?? until) - episode.Start).TotalMilliseconds))} state={(episode.End.HasValue ? "recovered" : "open")} sequence={string.Join('>', episode.Sequence)} omittedFailures={episode.SequenceOmitted}");
            }
            await output.WriteLineAsync("5. SQLite per bot/category/event/boundary and busy incidents (SQL/parameters/connection strings are never read or printed; retries and command failures are separate observations)");
            foreach (var pair in database.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var g = pair.Value;
                await output.WriteLineAsync($"bot/category/event={pair.Key} events={g.Events} {g.Duration.Summary()} failed={g.Failed} slow={g.Slow} busyIncidents={g.Busy} retryEvents={g.Retries} busyWaitMs={Ms(g.BusyWait)}");
            }
            token.ThrowIfCancellationRequested();
            await output.WriteLineAsync("6. Data quality / loss / incomplete traces / missing instrumentation");
            await output.WriteLineAsync($"files={Quality.Files} lines={Quality.Lines} accepted={Quality.Accepted} empty={Quality.Empty} malformed={Quality.Malformed} truncated={Quality.Truncated} oversized={Quality.Oversized} unknownSchemas={Quality.UnknownSchema} unknownEvents={Quality.UnknownEvent} invalidTimestamps={Quality.InvalidTimestamp} outsideWindow={Quality.OutsideWindow} filtered={Quality.Filtered} unreadableFiles={Quality.UnreadableFiles}");
            await output.WriteLineAsync($"skippedUnsafeFiles={Quality.SkippedUnsafeFiles} emptyOrSpecialFiles={Quality.EmptyOrSpecialFiles}");
            await output.WriteLineAsync($"lossEvents={Quality.LossEvents} writerFailureEvents={Quality.WriterFailureEvents} sessionCounterMaximaSums: sessions={sessions.Count} droppedEvents={SessionTotal(0)} writerFailures={SessionTotal(1)} fullChannel={SessionTotal(2)} write={SessionTotal(3)} rejected={SessionTotal(4)} shutdown={SessionTotal(5)}");
            await output.WriteLineAsync($"legacyMissingSessionProvenance: counterRecords={Quality.LossCounterRecordsWithoutSession} droppedEventsMaxGauge={Quality.Dropped} writerFailuresMaxGauge={Quality.WriterFailures}");
            await output.WriteLineAsync($"trackedTraces={traces.Count} incompleteTracesObserved={traces.Count(p => (p.Value & 2) == 0)} summaryWithoutMilestones={traces.Count(p => p.Value == 2)} missingTraceRecords={Quality.MissingTrace} missingApplicationSummaries={Quality.MissingApplication} missingStageSummaries={Quality.MissingStages} duplicateSummaries={Quality.DuplicateSummaries}");
            await output.WriteLineAsync($"legacyMissingSessionLossReasonMaxGauges: fullChannel={Quality.FullChannelDropped} write={Quality.WriteDropped} rejected={Quality.Rejected} shutdown={Quality.ShutdownDropped}");
            await output.WriteLineAsync($"limitOmissions: files={Quality.FileLimit} botRecords={Quality.BotLimit} groupRecords={Quality.GroupLimit} traceRecords={Quality.TraceLimit} episodes={Quality.EpisodeLimit} sessionCounterRecords={Quality.SessionLimit} outOfOrderPollRecords={Quality.OutOfOrder}");
            await output.WriteLineAsync($"Quality caveats: process counters are summed maxima per validated session (cap={MaxSessions}), not per snapshot; these cumulative values can include loss before the lookback window. Legacy missing-session counters remain separate maxima with unknown restart provenance, never added to session totals. Incomplete traces may cross window/retention boundaries. Trace coverage is capped (not extrapolated). Missing fields stay unavailable; unknown labels become other; identities outside the fixed known-bot vocabulary are pseudonymized even when filtered. No customer contents, tokens, URLs, SQL, passwords, or arbitrary exception text are projected.");
        }
    }
}
