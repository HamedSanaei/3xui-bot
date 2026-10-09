using Adminbot.Services.Telemetry;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;

namespace Adminbot.Services.TelegramEndpoints;

/// <summary>Reusable identity-bound client facade selecting a fenced endpoint epoch for each complete operation.</summary>
/// <remarks>
/// Background workers may retain this facade: it resolves the active generation on each operation rather than retaining
/// an obsolete SDK client. Receiver views are pinned and reject stale epochs. Control views are reserved for explicit
/// migration probes/logout; they must never be handed to notification workers or business handlers.
/// Each generation shares the provider's socket pool. No operation is retried or transparently rerouted after starting.
/// </remarks>
internal sealed class EndpointRoutedTelegramBotClient : ITelegramBotClient
{
    /// <summary>Identity authority and lease owner; null only for an explicit control transport.</summary>
    private readonly TelegramEndpointRuntimeGate _gate;
    /// <summary>Provider-owned cached SDK lookup, never a per-request HttpClient factory.</summary>
    private readonly Func<TelegramEndpointRoute, ITelegramBotClient> _resolve;
    /// <summary>Configured canonical internal bot id and exact BotFather identity.</summary>
    private readonly string _botId;
    private readonly long _identity;
    /// <summary>Receiver epoch; null for reusable ordinary facades.</summary>
    private readonly long? _generation;
    /// <summary>Explicit control route; null means ordinary admission is enforced.</summary>
    private readonly TelegramEndpointRoute? _control;
    /// <summary>Allows only receiver-origin requests during staged destination startup.</summary>
    private readonly bool _receiver;
    /// <summary>Restricts a disabled-tenant capability view to three safe read-only SDK request types.</summary>
    private readonly bool _probe;
    /// <summary>Trusted Local file mapping and optional nonblocking operational writer.</summary>
    private readonly TelegramEndpointRoutingOptions _options;
    private readonly LatencyTelemetryService _telemetry;
    /// <summary>Protects event forwarding, timeout overrides, and bounded file bindings without I/O under the lock.</summary>
    private readonly object _sync = new();
    /// <summary>Strongly associates returned TGFile objects with the generation that looked them up, without retaining the objects.</summary>
    private readonly ConditionalWeakTable<TGFile, FileBinding> _files = new();
    /// <summary>Compatibility path bindings capped at 512 entries; paths are private in-memory data and never persisted.</summary>
    private readonly Dictionary<string, FileBinding> _paths = new(StringComparer.Ordinal);
    private readonly Queue<(string Path, long Serial)> _pathOrder = new();
    private long _fileSerial;
    /// <summary>Only the most recently used SDK client receives this facade's event subscribers.</summary>
    private ITelegramBotClient _attached;
    private AsyncEventHandler<ApiRequestEventArgs> _making;
    private AsyncEventHandler<ApiResponseEventArgs> _received;
    private TimeSpan? _timeout;
    private IExceptionParser _parser;
    /// <summary>Canonical internal id used by foreground telemetry, never the numeric SDK identity.</summary>
    internal string CanonicalBotId => _botId;
    /// <summary>Whether request-level cancellation provenance is needed for enabled telemetry.</summary>
    internal bool IsTelemetryEnabled => _telemetry?.Enabled == true && !LatencyTelemetrySuppression.IsActive;

    /// <summary>Creates an identity-bound routed, pinned-receiver, or explicit control view.</summary>
    /// <param name="botId">Exact canonical registry id, not a customer-supplied username.</param>
    /// <param name="identity">Positive BotFather id extracted from the current token.</param>
    /// <param name="gate">Required ordinary admission gate; may be null only with an explicit control route.</param>
    /// <param name="resolve">Cached generation SDK lookup over the shared HTTP pool.</param>
    /// <param name="options">Validated trusted origins and optional read-only filesystem mapping.</param>
    /// <param name="telemetry">Optional shared JSONL writer; never performs producer-side I/O.</param>
    /// <param name="generation">Required positive receiver epoch, or null for an ordinary reusable facade.</param>
    /// <param name="receiver">True only for the runtime's controlled pinned receiver view.</param>
    /// <param name="control">Explicit migration/notifier route, or null to enforce ordinary admission.</param>
    /// <param name="probe">True only for the authorized disabled-tenant capability path; sends/file transfers are rejected.</param>
    /// <remarks>Construction issues no request and does not activate either endpoint.</remarks>
    internal EndpointRoutedTelegramBotClient(string botId, long identity, TelegramEndpointRuntimeGate gate,
        Func<TelegramEndpointRoute, ITelegramBotClient> resolve, TelegramEndpointRoutingOptions options,
        LatencyTelemetryService telemetry, long? generation = null, bool receiver = false, TelegramEndpointRoute? control = null, bool probe = false)
    {
        _botId = botId; _identity = identity; _gate = gate; _resolve = resolve; _options = options;
        _telemetry = telemetry; _generation = generation; _receiver = receiver; _control = control; _probe = probe;
    }

    /// <inheritdoc />
    public bool LocalBotServer => CurrentRoute().Endpoint == TelegramEndpointType.Local;
    /// <inheritdoc />
    public long BotId => _identity;
    /// <summary>Gets or sets the existing SDK transport timeout across this facade's future endpoint generations.</summary>
    /// <remarks>No timeout is changed unless an existing caller explicitly sets this property.</remarks>
    public TimeSpan Timeout
    {
        get => Client(CurrentRoute()).Timeout;
        set { lock (_sync) { _timeout = value; if (_attached != null) _attached.Timeout = value; } }
    }
    /// <inheritdoc />
    public IExceptionParser ExceptionsParser
    {
        get => Client(CurrentRoute()).ExceptionsParser;
        set { lock (_sync) { _parser = value; if (_attached != null) _attached.ExceptionsParser = value; } }
    }
    /// <inheritdoc />
    public event AsyncEventHandler<ApiRequestEventArgs> OnMakingApiRequest
    {
        add { lock (_sync) { _making += value; if (_attached != null) _attached.OnMakingApiRequest += value; } }
        remove { lock (_sync) { _making -= value; if (_attached != null) _attached.OnMakingApiRequest -= value; } }
    }
    /// <inheritdoc />
    public event AsyncEventHandler<ApiResponseEventArgs> OnApiResponseReceived
    {
        add { lock (_sync) { _received += value; if (_attached != null) _attached.OnApiResponseReceived += value; } }
        remove { lock (_sync) { _received -= value; if (_attached != null) _attached.OnApiResponseReceived -= value; } }
    }

    /// <summary>Executes one SDK request on the admitted epoch and captures file provenance without examining payload content.</summary>
    /// <typeparam name="TResponse">Response type declared by the SDK request.</typeparam>
    /// <param name="request">Required strongly typed SDK request; its body is neither inspected nor logged.</param>
    /// <param name="cancellationToken">Existing caller/foreground/receiver deadline, forwarded unchanged.</param>
    /// <returns>The real SDK response; TGFile responses retain lookup epoch provenance privately.</returns>
    /// <exception cref="OperationCanceledException">A fenced/obsolete getUpdates receiver is terminated normally rather than entering a retry loop.</exception>
    /// <exception cref="BotTransportUnavailableException">The bot identity, endpoint admission, or pinned receiver epoch is unavailable before sending.</exception>
    /// <remarks>No automatic retry, endpoint replacement, or payload replay occurs if the response is ambiguous.</remarks>
    public async Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        if (_probe && request is not (GetMeRequest or GetChatRequest or GetChatAdministratorsRequest))
            throw new BotTransportUnavailableException("capability_probe_delivery_forbidden");
        var receiverRequest = _receiver && request is GetMeRequest or GetWebhookInfoRequest or DeleteWebhookRequest or GetUpdatesRequest or SetMyCommandsRequest;
        TelegramEndpointRuntimeGate.RequestLease lease;
        try
        {
            if (!_control.HasValue) await _gate.HydrateAsync(_botId, _identity, cancellationToken);
            lease = _control.HasValue ? null : _gate.AcquireRequest(_botId, _identity, _generation, receiverRequest, _probe);
        }
        catch (BotTransportUnavailableException) when (_receiver && request is GetUpdatesRequest)
        {
            // The SDK polling loop treats cancellation as normal termination, avoiding a fenced-generation retry loop.
            throw new OperationCanceledException("The Telegram endpoint receiver generation is fenced.");
        }
        using var admittedRequest = lease;
        var route = _control ?? lease.Route;
        using var context = IsTelemetryEnabled || TelegramEndpointRequestObservation.IsActive ?
            TelegramEndpointTelemetryContext.Push(route.Endpoint, route.Generation, route.MigrationState) : null;
        TelegramEndpointRequestObservation.Capture(route, context?.MigrationState);
        var response = await Client(route).SendRequest(request, cancellationToken);
        if (request is GetFileRequest && response is TGFile file) Bind(file, route);
        return response;
    }

    /// <summary>Tests the admitted endpoint with the SDK's unchanged API validation behavior.</summary>
    /// <param name="cancellationToken">Caller cancellation forwarded unchanged.</param>
    /// <returns>The SDK's authentication result; transport failures retain their existing exception behavior.</returns>
    /// <remarks>This does not log out, migrate, or start a receiver.</remarks>
    public async Task<bool> TestApi(CancellationToken cancellationToken = default)
    {
        if (!_control.HasValue) await _gate.HydrateAsync(_botId, _identity, cancellationToken);
        using var lease = _control.HasValue ? null : _gate.AcquireRequest(_botId, _identity, _generation, _receiver, _probe);
        var route = _control ?? lease.Route;
        using var context = IsTelemetryEnabled ? TelegramEndpointTelemetryContext.Push(route.Endpoint, route.Generation, route.MigrationState) : null;
        return await Client(route).TestApi(cancellationToken);
    }

    /// <summary>Downloads a looked-up Telegram file only through its original identity and endpoint generation.</summary>
    /// <param name="file">Required TGFile returned by this bot's GetFile; its path is private operational data.</param>
    /// <param name="destination">Required writable caller-owned stream; the facade never disposes it.</param>
    /// <param name="cancellationToken">Caller transfer cancellation, not a speculative foreground budget.</param>
    /// <returns>A task completing after real bytes have been copied to the destination.</returns>
    /// <exception cref="BotTransportUnavailableException">File provenance belongs to an obsolete generation or mapping is unavailable.</exception>
    /// <remarks>Local --local files use the existing read-only host volume mapping, not an unsupported HTTP file URL or arbitrary filesystem path.</remarks>
    public Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        _files.TryGetValue(file, out var binding);
        return DownloadAsync(file.FilePath, destination, binding, cancellationToken);
    }

    /// <summary>Downloads an existing SDK file path using bounded private lookup provenance.</summary>
    /// <param name="filePath">Required path obtained from this facade's GetFile, never a customer-authored filesystem path.</param>
    /// <param name="destination">Required writable caller-owned stream.</param>
    /// <param name="cancellationToken">Caller transfer cancellation forwarded unchanged.</param>
    /// <returns>A task completing after bytes are copied; empty or stale paths fail before any transfer.</returns>
    /// <remarks>Prefer the TGFile overload for strong lookup provenance. Relative Cloud paths retain SDK compatibility; Local paths must have been observed in a successful GetFile.</remarks>
    public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default)
    {
        FileBinding binding = null;
        if (filePath != null) lock (_sync) _paths.TryGetValue(filePath, out binding);
        return DownloadAsync(filePath, destination, binding, cancellationToken);
    }

    /// <summary>Performs the complete admitted transfer without rerouting or logging the file path/content.</summary>
    /// <param name="path">Private SDK file path.</param><param name="destination">Caller-owned writable stream.</param>
    /// <param name="binding">Known lookup provenance, or null for legacy relative Cloud paths.</param><param name="token">Caller cancellation.</param>
    /// <returns>A task completing after the real transfer.</returns>
    /// <exception cref="BotTransportUnavailableException">The file epoch or Local mapping is invalid.</exception>
    /// <exception cref="IOException">The existing mapped file cannot be read; callers retain responsibility for delivery state.</exception>
    /// <remarks>Local file copying is measured as business-processing I/O, not CPU time. Transfer failures never initiate replay.</remarks>
    private async Task DownloadAsync(string path, Stream destination, FileBinding binding, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096) throw new BotTransportUnavailableException("invalid_telegram_file_path");
        if (_probe) throw new BotTransportUnavailableException("capability_probe_delivery_forbidden");
        if (!_control.HasValue) await _gate.HydrateAsync(_botId, _identity, token);
        using var lease = _control.HasValue ? null : _gate.AcquireRequest(_botId, _identity, binding?.Generation ?? _generation);
        var route = _control ?? lease.Route;
        if (binding != null && (binding.Generation != route.Generation || binding.Endpoint != route.Endpoint))
            throw new BotTransportUnavailableException("obsolete_file_generation");
        using var context = IsTelemetryEnabled || TelegramEndpointRequestObservation.IsActive ?
            TelegramEndpointTelemetryContext.Push(route.Endpoint, route.Generation, route.MigrationState) : null;
        TelegramEndpointRequestObservation.Capture(route, context?.MigrationState);
        if (route.Endpoint == TelegramEndpointType.Cloud)
        {
            await Client(route).DownloadFile(path, destination, token);
            return;
        }
        if (binding == null) throw new BotTransportUnavailableException("unbound_local_file_path");
        var started = Stopwatch.GetTimestamp();
        Exception failure = null;
        using var stage = TelegramUpdateLatencyScope.Current?.Measure(TelegramUpdateStage.BusinessProcessing);
        try
        {
            var local = TelegramLocalFileMapper.Resolve(_options, path);
            await using var source = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, token);
        }
        catch (Exception error)
        {
            failure = error;
            // Filesystem exceptions can contain token-bearing directory names. Preserve type/semantics, never the path.
            if (error is UnauthorizedAccessException) throw new UnauthorizedAccessException("Local Telegram file access is denied.");
            if (error is IOException) throw new IOException("Local Telegram file transfer failed.");
            throw;
        }
        finally
        {
            var trace = UpdateTelemetryTracker.Current;
            _telemetry?.TryRecord(TelegramTransportDiagnostics.Classify(failure, null, token) with
            {
                EventType = "telegram_request_completed", BotId = _botId, TraceId = trace?.TraceId,
                UpdateId = trace?.UpdateId, Sequence = trace?.Sequence, Operation = "local_file_download",
                Category = "file_download", Stage = "business_processing", DurationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                EndpointType = "local", EndpointGeneration = route.Generation,
                MigrationState = context?.MigrationState, Attempt = 1
            });
        }
    }

    /// <summary>Reads route metadata without admitting an API operation.</summary>
    /// <returns>The explicit control route or live identity-bound route.</returns>
    /// <remarks>Used only for SDK properties; send/download paths acquire leases atomically instead.</remarks>
    private TelegramEndpointRoute CurrentRoute() => _control ?? _gate.GetRoute(_botId, _identity);

    /// <summary>Resolves the cached epoch SDK and forwards subscribers/explicit property overrides once.</summary>
    /// <param name="route">Admitted endpoint epoch.</param><returns>The existing cached SDK client, never a per-request HttpClient.</returns>
    /// <remarks>The provider lookup completes before this facade's short metadata lock; no network operation occurs under either lock.</remarks>
    private ITelegramBotClient Client(TelegramEndpointRoute route)
    {
        var client = _resolve(route);
        lock (_sync)
        {
            if (!ReferenceEquals(client, _attached))
            {
                if (_attached != null)
                {
                    if (_making != null) _attached.OnMakingApiRequest -= _making;
                    if (_received != null) _attached.OnApiResponseReceived -= _received;
                }
                _attached = client;
                if (_making != null) client.OnMakingApiRequest += _making;
                if (_received != null) client.OnApiResponseReceived += _received;
                if (_timeout.HasValue) client.Timeout = _timeout.Value;
                if (_parser != null) client.ExceptionsParser = _parser;
            }
        }
        return client;
    }

    /// <summary>Stores weak object and capped path provenance after successful file lookup.</summary>
    /// <param name="file">Actual SDK TGFile response; no content is read.</param><param name="route">Admitted lookup epoch.</param>
    /// <remarks>Paths never leave memory. FIFO eviction bounds compatibility lookup storage at 512 paths per facade.</remarks>
    private void Bind(TGFile file, TelegramEndpointRoute route)
    {
        if (string.IsNullOrWhiteSpace(file.FilePath) || file.FilePath.Length > 4096) return;
        lock (_sync)
        {
            var binding = new FileBinding(route.Endpoint, route.Generation, ++_fileSerial);
            _files.AddOrUpdate(file, binding);
            _paths[file.FilePath] = binding;
            _pathOrder.Enqueue((file.FilePath, binding.Serial));
            while (_pathOrder.Count > 512)
            {
                var old = _pathOrder.Dequeue();
                if (_paths.TryGetValue(old.Path, out var current) && current.Serial == old.Serial) _paths.Remove(old.Path);
            }
        }
    }

    /// <summary>Private immutable file lookup provenance, without token or customer identifiers.</summary>
    /// <param name="Endpoint">Lookup endpoint enum.</param><param name="Generation">Positive lookup epoch.</param><param name="Serial">Monotonic facade-local eviction serial.</param>
    private sealed record FileBinding(TelegramEndpointType Endpoint, long Generation, long Serial);
}

/// <summary>Captures one routed request's epoch for foreground telemetry after the inner request context restores.</summary>
/// <remarks>Created only for enabled foreground telemetry; concurrent calls have isolated AsyncLocal observations.</remarks>
internal sealed class TelegramEndpointRequestObservation : IDisposable
{
    /// <summary>Current foreground-owned observation slot; never installed around a receiver callback.</summary>
    private static readonly AsyncLocal<TelegramEndpointRequestObservation> Ambient = new();
    /// <summary>Whether a foreground request currently needs a routed metadata capture.</summary>
    internal static bool IsActive => Ambient.Value != null;
    /// <summary>Enclosing observation restored after the complete foreground request.</summary>
    private readonly TelegramEndpointRequestObservation _previous;
    /// <summary>Actual admitted route, or null if the request was rejected before endpoint selection.</summary>
    internal TelegramEndpointRoute? Route { get; private set; }
    /// <summary>Closed state label captured inside the actual request context, or null before admission.</summary>
    internal string MigrationState { get; private set; }
    /// <summary>Creates a request-specific observation slot without I/O.</summary>
    internal TelegramEndpointRequestObservation() { _previous = Ambient.Value; Ambient.Value = this; }
    /// <summary>Attaches immutable admitted route metadata to an enclosing foreground observation.</summary>
    /// <param name="route">Actual route selected under the request admission lock.</param>
    /// <param name="migrationState">Closed state label from the actual request context.</param>
    /// <remarks>No global last-request state is used.</remarks>
    internal static void Capture(TelegramEndpointRoute route, string migrationState)
    {
        if (Ambient.Value is not { } observation) return;
        observation.Route = route;
        observation.MigrationState = migrationState;
    }
    /// <summary>Restores the preceding observation; completed slots are not retained.</summary>
    public void Dispose() => Ambient.Value = _previous;
}

/// <summary>Maps official Local --local file paths into an existing host-mounted read-only data directory.</summary>
/// <remarks>Official Local API does not serve Cloud's /file endpoint. Container/server paths are never opened directly. No downloader configuration or filesystem is modified.</remarks>
internal static class TelegramLocalFileMapper
{
    /// <summary>Validates an existing nonlinked host root before an irreversible Cloud logout.</summary>
    /// <param name="options">Validated trusted server/host mapping from application configuration.</param>
    /// <returns>True when the mapped host directory exists and no path component is a link/reparse point.</returns>
    /// <remarks>Missing permission or inaccessible paths return false; actual per-file permissions are checked when transferring.</remarks>
    internal static bool IsReady(TelegramEndpointRoutingOptions options)
    {
        if (!options.HasLocalFileMapping) return false;
        try { CheckLinks(Path.GetFullPath(options.LocalFileHostRoot)); return Directory.Exists(options.LocalFileHostRoot); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// <summary>Resolves one observed server file beneath the trusted existing host mapping without traversal or links.</summary>
    /// <param name="options">Validated explicit roots; neither root may be supplied through an admin callback.</param>
    /// <param name="serverPath">Private actual GetFile path returned by the trusted Local API.</param>
    /// <returns>A full local host path suitable only for read-only copying; never expose it to users or logs.</returns>
    /// <exception cref="BotTransportUnavailableException">Mapping is absent, invalid, outside its root, or linked.</exception>
    /// <remarks>URI decoding is deliberately absent: this is a filesystem path, not a URL. Every component rejects parent traversal and symbolic links.</remarks>
    internal static string Resolve(TelegramEndpointRoutingOptions options, string serverPath)
    {
        if (!options.HasLocalFileMapping || string.IsNullOrEmpty(serverPath) || serverPath.Length > 4096 || serverPath.Any(char.IsControl))
            throw new BotTransportUnavailableException("local_file_mapping_required");
        var serverRoot = options.LocalFileServerRoot.Replace('\\', '/').TrimEnd('/') + "/";
        var normalized = serverPath.Replace('\\', '/');
        if (!normalized.StartsWith(serverRoot, StringComparison.Ordinal)) throw new BotTransportUnavailableException("local_file_outside_mapping");
        var relative = normalized[serverRoot.Length..];
        if (relative.Length == 0 || relative.Split('/').Any(part => part is ".." or "." or ""))
            throw new BotTransportUnavailableException("invalid_local_file_path");
        var hostRoot = Path.GetFullPath(options.LocalFileHostRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var local = Path.GetFullPath(Path.Combine(hostRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!local.StartsWith(hostRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new BotTransportUnavailableException("local_file_outside_mapping");
        try { CheckLinks(local); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new BotTransportUnavailableException("local_file_mapping_unavailable"); }
        return local;
    }

    /// <summary>Checks each existing host path component without following symbolic links or Windows reparse points.</summary>
    /// <param name="fullPath">Required fully qualified root or file path.</param>
    /// <exception cref="IOException">A linked component exists.</exception>
    /// <remarks>The shared data volume is treated as trusted operator-owned storage; customer input never selects its root.</remarks>
    private static void CheckLinks(string fullPath)
    {
        var current = Path.GetPathRoot(fullPath);
        foreach (var part in fullPath[current.Length..].Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget != null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new IOException("A Local file mapping component is linked.");
        }
    }
}
