using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Adminbot.Services.Telemetry;

/// <summary>Single-owner bounded UTF-8 JSONL buffer, rotation and retention implementation.</summary>
/// <remarks>Only the telemetry task accesses this type. Application buffers are bounded to 64 KiB plus one 32 KiB line. Retention scans at most 8192 directory entries and 4096 owned files, deletes at most 64 files per pass, and stops after a 100 ms maintenance budget between filesystem operations. An inaccessible or over-budget directory is a recoverable writer failure, never a handler failure.</remarks>
internal sealed class LatencyTelemetryFileWriter
{
    /// <summary>Maximum UTF-8 bytes of one complete JSONL record.</summary>
    public const int MaximumLineBytes = 32 * 1024;
    /// <summary>Maximum buffered bytes before an asynchronous file flush.</summary>
    private const int BufferBytes = 64 * 1024;
    /// <summary>Maximum number of owned file metadata entries retained per maintenance pass.</summary>
    private const int MaximumFiles = 4096;
    /// <summary>Maximum examined directory entries per maintenance pass, including unrelated entries.</summary>
    private const int MaximumDirectoryEntries = 8192;
    /// <summary>Startup-bound validated limits.</summary>
    private readonly LatencyTelemetryOptions _options;
    /// <summary>Absolute dedicated telemetry directory.</summary>
    private readonly string _directory;
    /// <summary>Injected UTC clock used for daily rotation, filenames and retention.</summary>
    private readonly TimeProvider _timeProvider;
    /// <summary>Per-process filename uniqueness without revealing runtime secrets.</summary>
    private readonly string _session = Guid.NewGuid().ToString("N")[..8];
    /// <summary>Reusable serializer buffer for one bounded record.</summary>
    private readonly ArrayBufferWriter<byte> _line = new(MaximumLineBytes);
    /// <summary>Reusable asynchronous file batch buffer.</summary>
    private readonly ArrayBufferWriter<byte> _buffer = new(BufferBytes);
    /// <summary>Cached compact-family serializer settings preserving relevant unavailable measurements explicitly.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { LatencyTelemetryProjection.Configure } },
        Converters = { new UtcTimestampConverter() }
    };
    /// <summary>Current asynchronous file stream; null while recovering or before first use.</summary>
    private FileStream _stream;
    /// <summary>Absolute active file path, excluded from retention deletion.</summary>
    private string _activePath;
    /// <summary>UTC day owning the current file.</summary>
    private DateOnly _day;
    /// <summary>Current file bytes, including application-buffered bytes.</summary>
    private long _currentBytes;
    /// <summary>All owned bytes from the last bounded scan, plus subsequently admitted bytes.</summary>
    private long _totalBytes;
    /// <summary>Filename rotation number for this process.</summary>
    private int _rotation;
    /// <summary>Records buffered or written but not yet successfully flushed.</summary>
    public long PendingRecords { get; private set; }
    /// <summary>Cumulative successfully flushed records in this process.</summary>
    public long FlushedRecords { get; private set; }
    /// <summary>Whether an active file stream exists.</summary>
    public bool IsOpen => _stream != null;

    /// <summary>Creates writer state without accessing the filesystem.</summary>
    /// <param name="options">Validated immutable-for-this-service limits.</param>
    /// <param name="directory">Absolute application's dedicated Telemetry subdirectory.</param>
    /// <param name="timeProvider">Optional UTC clock for deterministic rotation tests; null uses the system clock.</param>
    public LatencyTelemetryFileWriter(LatencyTelemetryOptions options, string directory, TimeProvider timeProvider = null)
    {
        _options = options;
        _directory = directory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Serializes and buffers one validated observation, rotating before a UTC day or size boundary.</summary>
    /// <param name="observation">Payload-free version-one event from the writer's validated channel read.</param>
    /// <param name="cancellationToken">Writer shutdown-deadline token, not the originating handler token.</param>
    /// <returns>True when the complete line was admitted to the bounded buffer; false for an oversized serialized record.</returns>
    /// <remarks>File capacity is reserved before buffering. A write or flush can fail; the caller accounts uncertain pending records and starts a fresh file rather than replaying ambiguous writes.</remarks>
    /// <exception cref="IOException">Storage cannot admit this line within the configured limits.</exception>
    /// <exception cref="UnauthorizedAccessException">The directory or file cannot be secured or accessed.</exception>
    public async ValueTask<bool> AppendAsync(LatencyTelemetryEvent observation, CancellationToken cancellationToken)
    {
        _line.Clear();
        if (observation.SerializationFields == 0)
            observation = observation with { SerializationFields = LatencyTelemetryProjection.FieldsForEvent(observation.EventType) };
        using (var json = new Utf8JsonWriter(_line)) JsonSerializer.Serialize(json, observation, JsonOptions);
        if (_line.WrittenCount + 1 > MaximumLineBytes) return false;
        _line.GetSpan(1)[0] = (byte)'\n';
        _line.Advance(1);
        var day = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
        if (_stream != null && (_day != day || _currentBytes + _line.WrittenCount > _options.MaxFileBytes))
            await CloseAsync(cancellationToken).ConfigureAwait(false);
        if (_stream == null) Open(day, _line.WrittenCount);
        if (_totalBytes + _line.WrittenCount > _options.MaxTotalBytes)
            Maintain(_line.WrittenCount);
        if (_totalBytes + _line.WrittenCount > _options.MaxTotalBytes)
            throw new IOException("Telemetry storage limit is exhausted.");
        if (_buffer.WrittenCount + _line.WrittenCount > BufferBytes)
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        _buffer.Write(_line.WrittenSpan);
        _currentBytes += _line.WrittenCount;
        _totalBytes += _line.WrittenCount;
        PendingRecords++;
        return true;
    }

    /// <summary>Asynchronously writes the bounded application buffer and flushes the file without fsync guarantees.</summary>
    /// <param name="cancellationToken">Writer shutdown-deadline token.</param>
    /// <returns>A task completing after complete lines are flushed to the operating-system file cache.</returns>
    /// <remarks>Power-loss durability is not promised; abrupt process termination can lose the last flush interval. Failed flushes leave PendingRecords available for explicit uncertainty/loss accounting.</remarks>
    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        if (_stream == null || PendingRecords == 0) return;
        if (_buffer.WrittenCount != 0)
        {
            await _stream.WriteAsync(_buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            _buffer.Clear();
        }
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        FlushedRecords += PendingRecords;
        PendingRecords = 0;
    }

    /// <summary>Flushes and closes an active file before normal rotation or graceful shutdown.</summary>
    /// <param name="cancellationToken">Writer shutdown-deadline token.</param>
    /// <returns>A task completing after the stream is released.</returns>
    public async ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        if (_stream != null) await _stream.DisposeAsync().ConfigureAwait(false);
        _stream = null;
        _activePath = null;
        _currentBytes = 0;
    }

    /// <summary>Discards ambiguous pending data and closes the unbuffered stream after a failure.</summary>
    /// <returns>The count of complete records whose persistence is unavailable or uncertain.</returns>
    /// <remarks>No recursive logging, flush, retry or network request occurs. Future recovery always creates a new file so a partial prior line cannot corrupt later lines.</remarks>
    public long Abort()
    {
        var lost = PendingRecords;
        PendingRecords = 0;
        _buffer.Clear();
        var stream = _stream;
        _stream = null;
        _activePath = null;
        _currentBytes = 0;
        try { stream?.Dispose(); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return lost;
    }

    /// <summary>Runs bounded retention and refreshes owned-file byte accounting.</summary>
    /// <param name="reserveBytes">Additional bytes that must fit, or zero for periodic maintenance.</param>
    /// <remarks>Only latency-*.jsonl files are owned. Unrelated files are never deleted; links and excess enumeration fail closed. If a bounded pass cannot free enough room, producers continue and the recovery loop tries again later.</remarks>
    /// <exception cref="IOException">The dedicated directory exceeds bounded enumeration or contains an owned symlink.</exception>
    public void Maintain(int reserveBytes = 0)
    {
        EnsureDirectory();
        var started = Stopwatch.GetTimestamp();
        var files = new List<FileInfo>(Math.Min(MaximumFiles, 64));
        var examined = 0;
        long total = 0;
        foreach (var entry in new DirectoryInfo(_directory).EnumerateFileSystemInfos())
        {
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 100)
                throw new IOException("Telemetry maintenance time budget exceeded.");
            if (++examined > MaximumDirectoryEntries) throw new IOException("Telemetry directory enumeration limit exceeded.");
            if (entry is not FileInfo file || !file.Name.StartsWith("latency-", StringComparison.Ordinal)
                || !file.Name.EndsWith(".jsonl", StringComparison.Ordinal)) continue;
            if (file.LinkTarget != null) throw new IOException("Telemetry file link is not permitted.");
            if (files.Count >= MaximumFiles) throw new IOException("Telemetry file count limit exceeded.");
            if (OperatingSystem.IsLinux() && File.GetUnixFileMode(file.FullName) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
                File.SetUnixFileMode(file.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            total = checked(total + file.Length);
            files.Add(file);
        }
        total = checked(total + _buffer.WrittenCount);
        files.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-_options.RetentionDays);
        var removed = 0;
        foreach (var file in files)
        {
            if (removed >= 64 || Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 100) break;
            if (string.Equals(file.FullName, _activePath, StringComparison.Ordinal)) continue;
            if (file.LastWriteTimeUtc >= cutoff && total + reserveBytes <= _options.MaxTotalBytes) continue;
            var length = file.Length;
            file.Delete();
            total -= length;
            removed++;
        }
        _totalBytes = total;
    }

    /// <summary>Creates a unique UTC-named file only after a bounded retention pass admits it.</summary>
    /// <param name="day">Current writer UTC day, independent of historical event timestamps.</param>
    /// <param name="reserveBytes">Bytes required for the first line.</param>
    /// <remarks>UnixCreateMode prevents a world-readable creation window. Files are created exclusively and never overwritten or appended after recovery.</remarks>
    private void Open(DateOnly day, int reserveBytes)
    {
        Maintain(reserveBytes);
        if (_totalBytes + reserveBytes > _options.MaxTotalBytes) throw new IOException("Telemetry storage limit is exhausted.");
        var name = "latency-" + _timeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmssfffffff", CultureInfo.InvariantCulture)
            + "-" + _session + "-" + (++_rotation).ToString("D6", CultureInfo.InvariantCulture) + ".jsonl";
        var path = Path.Combine(_directory, name);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan, BufferSize = 1
        };
        if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        _stream = new FileStream(path, options);
        _activePath = path;
        _day = day;
        _currentBytes = 0;
    }

    /// <summary>Creates or secures the dedicated directory, rejecting symlink redirection.</summary>
    /// <remarks>Linux directories are 0700 and new files 0600. The persistent parent Data directory is owned by application deployment and is not altered.</remarks>
    private void EnsureDirectory()
    {
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (new DirectoryInfo(_directory).LinkTarget != null) throw new IOException("Telemetry directory link is not permitted.");
            File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else
        {
            Directory.CreateDirectory(_directory);
            if (new DirectoryInfo(_directory).LinkTarget != null) throw new IOException("Telemetry directory link is not permitted.");
        }
    }

    /// <summary>Writes every schema timestamp with a UTC suffix, including UTC ticks reconstructed by SQLite without DateTimeKind.</summary>
    /// <remarks>Unspecified internal timestamps follow the application's persisted-UTC convention; explicitly local values are converted, not merely relabeled.</remarks>
    private sealed class UtcTimestampConverter : JsonConverter<DateTime>
    {
        /// <summary>Reads an ISO timestamp and normalizes it to UTC.</summary>
        /// <param name="reader">Reader positioned on an ISO timestamp string.</param>
        /// <param name="typeToConvert">DateTime type selected by the serializer.</param>
        /// <param name="options">Serializer settings; no data is retained.</param>
        /// <returns>The timestamp normalized to UTC under the application's persisted-UTC convention.</returns>
        /// <exception cref="JsonException">The value is not a valid timestamp.</exception>
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (!reader.TryGetDateTime(out var value)) throw new JsonException("Invalid UTC timestamp.");
            return value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        /// <summary>Writes a UTC timestamp without allocating a formatted string.</summary>
        /// <param name="writer">Active JSON writer for one bounded telemetry line.</param>
        /// <param name="value">Internal UTC timestamp; unspecified persisted UTC ticks are relabeled without changing ticks.</param>
        /// <param name="options">Serializer settings; no data is retained.</param>
        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }
}
