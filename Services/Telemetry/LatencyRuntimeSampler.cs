using System;
using System.Diagnostics;
using System.Buffers.Text;
using System.IO;

namespace Adminbot.Services.Telemetry;

/// <summary>Writer-only process sampler; it never runs on update, request or database producers.</summary>
/// <remarks>CPU is normalized across logical processors, GC pause is an interval delta, and unsupported available-memory measurement remains null.</remarks>
internal sealed class LatencyRuntimeSampler : IDisposable
{
    /// <summary>Process handle reused for every sample.</summary>
    private readonly Process _process = Process.GetCurrentProcess();
    /// <summary>Previous monotonic sample timestamp; zero means no baseline.</summary>
    private long _lastTimestamp;
    /// <summary>Previous successful process CPU inspection timestamp; failures do not shorten the CPU sample interval.</summary>
    private long _lastCpuTimestamp;
    /// <summary>Previous process CPU consumed, in milliseconds.</summary>
    private double _lastCpuMs;
    /// <summary>Previous total managed GC pause milliseconds.</summary>
    private double _lastPauseMs;

    /// <summary>Captures reliable available process and runtime measurements without triggering garbage collection.</summary>
    /// <returns>A process_health observation; OS-specific unavailable metrics are null.</returns>
    /// <remarks>Only the single telemetry writer calls this method, approximately every configured thirty seconds.</remarks>
    public LatencyTelemetryEvent Capture()
    {
        var now = Stopwatch.GetTimestamp();
        var pauseMs = GC.GetTotalPauseDuration().TotalMilliseconds;
        double? cpu = null;
        long? workingSet = null;
        try
        {
            _process.Refresh();
            var cpuMs = _process.TotalProcessorTime.TotalMilliseconds;
            if (_lastCpuTimestamp != 0)
            {
                var elapsedMs = Stopwatch.GetElapsedTime(_lastCpuTimestamp, now).TotalMilliseconds;
                if (elapsedMs > 0) cpu = Math.Max(0, (cpuMs - _lastCpuMs) * 100 / elapsedMs / Environment.ProcessorCount);
            }
            _lastCpuMs = cpuMs;
            _lastCpuTimestamp = now;
            workingSet = _process.WorkingSet64;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Process inspection is optional and must not turn a health sample into a writer failure.
        }
        var observation = new LatencyTelemetryEvent
        {
            EventType = "process_health", CpuPercent = cpu, WorkingSetBytes = workingSet,
            ManagedHeapBytes = GC.GetTotalMemory(false), AvailableMemoryBytes = ReadAvailableMemory(),
            ThreadPoolThreads = System.Threading.ThreadPool.ThreadCount,
            ThreadPoolPendingWorkItems = System.Threading.ThreadPool.PendingWorkItemCount,
            GcCollections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)],
            GcPauseMs = _lastTimestamp == 0 ? null : Math.Max(0, pauseMs - _lastPauseMs)
        };
        _lastTimestamp = now;
        _lastPauseMs = pauseMs;
        return observation;
    }

    /// <summary>Reads Linux MemAvailable from a fixed, non-secret OS file with bounded line and byte work.</summary>
    /// <returns>Available physical memory bytes when Linux provides a valid value, otherwise null.</returns>
    /// <remarks>No estimate is substituted on unsupported platforms or an unreadable proc filesystem.</remarks>
    private static long? ReadAvailableMemory()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            using var stream = new FileStream("/proc/meminfo", FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024);
            Span<byte> bytes = stackalloc byte[2048];
            var length = stream.Read(bytes);
            var content = bytes[..length];
            var prefix = "MemAvailable:"u8;
            var index = content.IndexOf(prefix);
            if (index < 0) return null;
            var value = content[(index + prefix.Length)..];
            while (!value.IsEmpty && value[0] == (byte)' ') value = value[1..];
            return Utf8Parser.TryParse(value, out long kilobytes, out var consumed)
                && value[consumed..].StartsWith(" kB"u8)
                && kilobytes >= 0 && kilobytes <= long.MaxValue / 1024 ? kilobytes * 1024 : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Releases the sampler's process handle after the writer has stopped.</summary>
    public void Dispose() => _process.Dispose();
}
