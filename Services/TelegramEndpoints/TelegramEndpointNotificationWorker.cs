using Adminbot.Domain;
using Adminbot.Services.Telemetry;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Exceptions;

namespace Adminbot.Services.TelegramEndpoints;

/// <summary>Delivers one durable endpoint incident directly to the existing logger channel through an available owned Cloud bot.</summary>
/// <remarks>The sender holds normal counted admission through all read-only authority checks, the SQLite send boundary and acknowledgment. Local or migrating identities never probe Cloud; ambiguous sends are never replayed and explicit 429 retries keep their frozen negative channel id.</remarks>
public sealed class TelegramEndpointNotificationWorker : BackgroundService
{
    /// <summary>Endpoint-only outbox store.</summary>
    private readonly TelegramEndpointStore _store;
    /// <summary>Exact current runtime bot definitions; default lookup fallback is deliberately never used.</summary>
    private readonly BotRegistry _registry;
    /// <summary>Shared pooled Cloud SDK provider, never a migration control-client bypass.</summary>
    private readonly BotClientProvider _clients;
    /// <summary>Current identity/generation admission shared with the transport and migration drain.</summary>
    private readonly TelegramEndpointRuntimeGate _gate;
    /// <summary>Validated read/send deadlines and durable retry bounds.</summary>
    private readonly TelegramEndpointRoutingOptions _options;
    /// <summary>Operational logger receives only fixed failure categories, never exceptions or tokens.</summary>
    private readonly ILogger<TelegramEndpointNotificationWorker> _logger;
    /// <summary>Clock for deadlines and deterministic recovery tests.</summary>
    private readonly TimeProvider _clock;
    /// <summary>Rotating deterministic nondefault candidate offset, preventing permanently denied early bots from starving later senders.</summary>
    private int _candidateCursor;
    /// <summary>Maximum sender probes per incident claim; the default owned sender remains first.</summary>
    private const int MaximumCandidates = 8;

    /// <summary>Creates the durable logger-channel worker without probing tokens, migrating routes or sending messages.</summary>
    /// <param name="store">Required endpoint-only transactional incident store and live logger destination resolver.</param>
    /// <param name="options">Required trusted endpoint settings and bounded delivery policy.</param>
    /// <param name="registry">Required exact current registry of enabled owned bots; tenants and assistants are ineligible.</param>
    /// <param name="clients">Required shared pooled Cloud SDK provider bound to the same gate.</param>
    /// <param name="gate">Required current normal-request admission gate; must be the provider's exact gate.</param>
    /// <param name="logger">Required operational logger; only fixed local-only categories are logged.</param>
    /// <param name="clock">Optional UTC deadline clock; null selects the system clock.</param>
    /// <exception cref="ArgumentNullException">A required collaborator is absent.</exception>
    /// <exception cref="ArgumentException">The provider and worker do not share the same admission gate.</exception>
    /// <remarks>No dedicated bot id, private recipient or default-token fallback is accepted. The default bot supplies only an existing logger-channel fallback and first eligible sender preference.</remarks>
    public TelegramEndpointNotificationWorker(TelegramEndpointStore store, TelegramEndpointRoutingOptions options,
        BotRegistry registry, BotClientProvider clients, TelegramEndpointRuntimeGate gate,
        ILogger<TelegramEndpointNotificationWorker> logger, TimeProvider clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).ValidateAndSnapshot();
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        if (!ReferenceEquals(_clients.EndpointGate, _gate))
            throw new ArgumentException("The notification transport must share its admission gate.", nameof(gate));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Processes bounded pages outside update lanes while preserving unresolved incidents across restart.</summary>
    /// <param name="stoppingToken">Host shutdown cancellation; in-flight sends remain durably uncertain rather than replayable.</param>
    /// <returns>A task completing when the host stops the worker.</returns>
    /// <remarks>Only old acknowledged outbox detail is pruned; incident deduplication receipts remain permanent. A database outage is logged with a fixed category and never drops an intent.</remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                for (var count = 0; count < 16 && await ProcessOneAsync(stoppingToken); count++) { }
                await _store.PruneDeliveredAlertsAsync(UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { LogFailure("notification_persistence_failure"); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), _clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    /// <summary>Claims one incident and verifies an exact owned Cloud sender's authority to post to the existing logger channel.</summary>
    /// <param name="token">Host cancellation for bounded read-only probes and the one Telegram send.</param>
    /// <returns>True when a due intent was processed, including safe deferral; false when no due intent exists.</returns>
    /// <remarks>Definitive pre-send denial tries another bounded candidate. Missing logger/Cloud prerequisites retain Pending without consuming the retry budget. No network await occurs inside SQLite work, and no alternate sender is tried after dispatch may have begun.</remarks>
    /// <example><code>await worker.ProcessOneAsync(cancellationToken);</code></example>
    public async Task<bool> ProcessOneAsync(CancellationToken token)
    {
        var alert = await _store.ClaimAlertAsync(UtcNow, token);
        if (alert == null) return false;
        var target = ResolveLoggerChannel();
        var frozenDestination = alert.DestinationChatId.HasValue;
        if (alert.DestinationChatId == null && string.IsNullOrEmpty(target))
        {
            await FinishAsync(alert, TelegramEndpointAlertStatus.Pending, "logger_unconfigured");
            return true;
        }
        var category = "transport_unavailable";
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(token);
        preparation.CancelAfter(TimeSpan.FromSeconds(_options.MigrationTimeoutSeconds));
        foreach (var bot in SelectCandidates())
        {
            if (preparation.IsCancellationRequested)
            {
                category = "pre_send_timeout";
                break;
            }
            if (!string.Equals(bot.Id, _registry.DefaultBot?.Id, StringComparison.Ordinal))
                Interlocked.Increment(ref _candidateCursor);
            var identity = TelegramBotTokenIdentity.ExtractBotId(bot.Token);
            if (!identity.HasValue) continue;
            var capturedToken = bot.Token;
            var marked = false;
            try
            {
                var delivery = _clients.GetCloudNotificationClient(bot.Id, identity.Value, capturedToken);
                using var lease = delivery.Lease;
                var route = lease.Route;
                using var telemetry = _clients.IsTelemetryEnabled
                    ? TelegramEndpointTelemetryContext.Push(route.Endpoint, route.Generation, route.MigrationState) : null;
                var client = delivery.Client;
                if (!IsCurrent(bot.Id, identity.Value, capturedToken, route)) continue;
                var me = await ReadAsync(ct => client.GetMe(ct), preparation.Token);
                if (!me.IsBot || me.Id != identity.Value)
                {
                    category = "pre_send_identity_mismatch";
                    continue;
                }
                if (!IsCurrent(bot.Id, identity.Value, capturedToken, route)) continue;
                var address = alert.DestinationChatId.HasValue
                    ? new ChatId(alert.DestinationChatId.Value) : new ChatId(target);
                var chat = await ReadAsync(ct => client.GetChat(address, ct), preparation.Token);
                if (chat.Type != ChatType.Channel || chat.Id >= 0 ||
                    (alert.DestinationChatId.HasValue && alert.DestinationChatId.Value != chat.Id))
                {
                    if (category == "transport_unavailable") category = "logger_unavailable";
                    continue;
                }
                if (!IsCurrent(bot.Id, identity.Value, capturedToken, route)) continue;
                var member = await ReadAsync(ct => client.GetChatMember(chat.Id, me.Id, ct), preparation.Token);
                if (!member.User.IsBot || member.User.Id != me.Id ||
                    member is not (ChatMemberAdministrator { CanPostMessages: true } or ChatMemberOwner))
                {
                    if (category == "transport_unavailable") category = "logger_unavailable";
                    continue;
                }
                preparation.Token.ThrowIfCancellationRequested();
                if (!IsCurrent(bot.Id, identity.Value, capturedToken, route)) continue;
                if (!TargetIsCurrent(frozenDestination, target))
                {
                    await FinishAsync(alert, TelegramEndpointAlertStatus.Pending, "logger_unavailable");
                    return true;
                }
                if (!await _store.MarkAlertSendStartedAsync(alert, chat.Id, UtcNow, preparation.Token)) return true;
                marked = true;
                // Recheck after the asynchronous SQLite boundary. No SDK call has begun, so a lost
                // prerequisite can still safely release this marker without creating send ambiguity.
                if (preparation.IsCancellationRequested || !IsCurrent(bot.Id, identity.Value, capturedToken, route) ||
                    !TargetIsCurrent(frozenDestination, target))
                {
                    using var finishing = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await _store.DeferUnsentAlertAsync(alert, "transport_unavailable", UtcNow, finishing.Token);
                    LogFailure("transport_unavailable");
                    return true;
                }
                await SendAsync(alert, chat.Id, client, token);
                return true;
            }
            catch when (marked) { throw; }
            catch (BotTransportUnavailableException) { }
            catch (ApiRequestException exception) when (exception.ErrorCode is 400 or 401 or 403)
            {
                if (category == "transport_unavailable") category = "logger_unavailable";
            }
            catch (OperationCanceledException)
            {
                category = "pre_send_timeout";
                if (preparation.IsCancellationRequested) break;
            }
            catch { category = "pre_send_failure"; }
        }
        await FinishAsync(alert, TelegramEndpointAlertStatus.Pending, category);
        if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();
        return true;
    }

    /// <summary>Performs exactly one send after the negative channel destination and non-replay boundary have been committed.</summary>
    /// <param name="alert">Owned durable incident claim; carries no token or message body.</param>
    /// <param name="destinationChatId">Verified negative Telegram channel id, not a user or group id.</param>
    /// <param name="client">Pooled SDK whose normal request lease remains held by the caller.</param>
    /// <param name="token">Host cancellation; interruption after invocation is terminal uncertainty.</param>
    /// <returns>A task completing after acknowledgment, typed rejection or uncertainty is durably recorded.</returns>
    /// <remarks>Only explicit Telegram 429 is retryable. Other 4xx rejections require manual review; transport failures, 5xx and shutdown are never automatically replayed.</remarks>
    private async Task SendAsync(TelegramEndpointAlert alert, long destinationChatId, ITelegramBotClient client, CancellationToken token)
    {
        try
        {
            using var sending = CancellationTokenSource.CreateLinkedTokenSource(token);
            sending.CancelAfter(TimeSpan.FromSeconds(_options.MigrationTimeoutSeconds));
            await client.SendMessage(new ChatId(destinationChatId), Render(alert), cancellationToken: sending.Token);
        }
        catch (ApiRequestException exception) when (exception.ErrorCode == 429)
        {
            using var finishing = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _store.RetryRateLimitedAlertAsync(alert, UtcNow, exception.Parameters?.RetryAfter, finishing.Token);
            LogFailure("rate_limited");
            return;
        }
        catch (ApiRequestException exception) when (exception.ErrorCode is 400 or 401 or 403)
        {
            await FinishAsync(alert, TelegramEndpointAlertStatus.ManualReview, "send_rejected");
            return;
        }
        catch
        {
            await FinishAsync(alert, TelegramEndpointAlertStatus.DeliveryUncertain, "send_uncertain");
            return;
        }
        await FinishAsync(alert, TelegramEndpointAlertStatus.Delivered, null);
    }

    /// <summary>Current UTC wall-clock observation used for persisted deadlines.</summary>
    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    /// <summary>Resolves the existing live root logger destination with the current default bot's sanitized channel fallback.</summary>
    /// <returns>A sanitized existing channel address or empty text; never a private recipient or invented target.</returns>
    private string ResolveLoggerChannel()
    {
        var defaultId = _registry.DefaultBot?.Id;
        var current = string.IsNullOrEmpty(defaultId) ? null : _registry.GetById(defaultId);
        return _store.ResolveLoggerChannel(current != null &&
            string.Equals(current.Id, defaultId, StringComparison.Ordinal) ? current.LoggerChannel : null);
    }

    /// <summary>Checks pre-send live target stability unless an earlier durable boundary already froze the verified channel id.</summary>
    /// <param name="frozenDestination">Whether a prior attempt already pinned the verified channel id before this claim.</param>
    /// <param name="target">Sanitized destination captured before getChat resolution.</param>
    /// <returns>True when an existing frozen channel is authoritative or the resolved live destination is unchanged.</returns>
    /// <remarks>Explicit 429 retries and proven undispatched post-marker deferrals retain the original negative channel; configuration changes never redirect an already frozen intent.</remarks>
    private bool TargetIsCurrent(bool frozenDestination, string target) =>
        frozenDestination || string.Equals(target, ResolveLoggerChannel(), StringComparison.Ordinal);

    /// <summary>Selects a bounded default-owned-first deterministic page of current eligible Cloud senders.</summary>
    /// <returns>At most eight enabled owned nonassistant candidates; later pages rotate so denied earlier bots cannot starve later authorized bots.</returns>
    /// <remarks>Only metadata is inspected; no Local, fenced, migrating or unhydrated identity is probed. The provider repeats these checks under counted admission.</remarks>
    private IReadOnlyList<BotInstanceConfig> SelectCandidates()
    {
        var candidates = _registry.Bots.Where(bot => bot.Enabled && bot.Type == BotInstanceTypes.Owned &&
                !bot.IsSalesAssistant && IsCloudAvailable(bot))
            .OrderByDescending(bot => string.Equals(bot.Id, _registry.DefaultBot?.Id, StringComparison.Ordinal))
            .ThenBy(bot => bot.Id, StringComparer.Ordinal).ToArray();
        if (candidates.Length == 0) return candidates;
        var hasDefault = string.Equals(candidates[0].Id, _registry.DefaultBot?.Id, StringComparison.Ordinal);
        var firstOther = hasDefault ? 1 : 0;
        var otherCount = candidates.Length - firstOther;
        if (otherCount == 0) return candidates;
        var selected = new List<BotInstanceConfig>(Math.Min(MaximumCandidates, candidates.Length));
        if (hasDefault) selected.Add(candidates[0]);
        var offset = (uint)Volatile.Read(ref _candidateCursor);
        // Advance the shared cursor only as the consumer actually considers nondefault candidates.
        // Advancing by a whole page would permanently skip its tail when the overall deadline expires.
        for (var scanned = 0; scanned < otherCount && selected.Count < MaximumCandidates; scanned++)
            selected.Add(candidates[firstOther + (int)((offset + (uint)scanned) % (uint)otherCount)]);
        return selected;
    }

    /// <summary>Inspects current Cloud availability without hydrating or probing a token.</summary>
    /// <param name="bot">Current enabled owned candidate configuration.</param>
    /// <returns>True only for active hydrated Cloud or verified CloudRecovered routes.</returns>
    private bool IsCloudAvailable(BotInstanceConfig bot)
    {
        var identity = TelegramBotTokenIdentity.ExtractBotId(bot.Token);
        if (!identity.HasValue) return false;
        try
        {
            var route = _gate.GetRoute(bot.Id, identity.Value);
            return route.Available && route.Endpoint == TelegramEndpointType.Cloud &&
                route.MigrationState is TelegramEndpointMigrationState.Cloud or TelegramEndpointMigrationState.CloudRecovered;
        }
        catch (BotTransportUnavailableException) { return false; }
    }

    /// <summary>Rechecks exact enabled owned registry identity, token and admitted available Cloud epoch between every asynchronous boundary.</summary>
    /// <param name="botId">Exact canonical owned registry id.</param>
    /// <param name="identity">Positive BotFather identity verified by getMe.</param>
    /// <param name="capturedToken">Original unlogged secret; same-identity rotations invalidate the attempt.</param>
    /// <param name="admitted">Counted original Cloud route and generation.</param>
    /// <returns>True only while the captured sender remains current and available without endpoint/generation changes.</returns>
    private bool IsCurrent(string botId, long identity, string capturedToken, TelegramEndpointRoute admitted)
    {
        var current = _registry.GetById(botId);
        if (current == null || !string.Equals(current.Id, botId, StringComparison.Ordinal) || !current.Enabled ||
            current.Type != BotInstanceTypes.Owned || current.IsSalesAssistant ||
            !string.Equals(current.Token, capturedToken, StringComparison.Ordinal) ||
            TelegramBotTokenIdentity.ExtractBotId(current.Token) != identity) return false;
        try
        {
            var route = _gate.GetRoute(botId, identity);
            return route.Available && route.Endpoint == TelegramEndpointType.Cloud && route.Generation == admitted.Generation &&
                route.MigrationState is TelegramEndpointMigrationState.Cloud or TelegramEndpointMigrationState.CloudRecovered;
        }
        catch (BotTransportUnavailableException) { return false; }
    }

    /// <summary>Runs one read-only SDK check within the health timeout and a bounded share of the whole preparation deadline.</summary>
    /// <typeparam name="T">SDK identity, channel or membership result.</typeparam>
    /// <param name="read">Exactly one read-only pooled SDK call; no delivery or logout is permitted.</param>
    /// <param name="token">Whole preparation deadline and host cancellation.</param>
    /// <returns>The actual SDK result; failures remain safe pre-send failures.</returns>
    /// <exception cref="OperationCanceledException">The bounded read or caller cancellation expires.</exception>
    /// <remarks>Each read is capped at one quarter of the preparation budget so even three slow default-bot checks cannot monopolize every claim and starve alternate senders.</remarks>
    private async Task<T> ReadAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken token)
    {
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(token);
        reading.CancelAfter(TimeSpan.FromSeconds(Math.Min(_options.HealthCheckTimeoutSeconds,
            _options.MigrationTimeoutSeconds / 4d)));
        return await read(reading.Token);
    }

    /// <summary>Commits a safe fixed-category result even after caller shutdown.</summary>
    /// <param name="alert">Owned detached outbox claim.</param>
    /// <param name="status">Safe retry or terminal result.</param>
    /// <param name="category">Closed optional worker diagnostic category.</param>
    /// <returns>A task completing when the result is durable; persistence failure leaves restart reconciliation responsible.</returns>
    /// <remarks>The separate bounded token prevents host cancellation from erasing a post-send acknowledgment or uncertainty marker.</remarks>
    private async Task FinishAsync(TelegramEndpointAlert alert, TelegramEndpointAlertStatus status, string category)
    {
        using var finishing = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _store.FinishAlertAsync(alert, status, category, UtcNow, finishing.Token);
        if (status != TelegramEndpointAlertStatus.Delivered) LogFailure(category ?? "notification_manual_review");
    }

    /// <summary>Emits fixed diagnostic metadata without allowing logger failure to mask a committed outbox result.</summary>
    /// <param name="category">Internal fixed category; never exception text or bot credentials.</param>
    private void LogFailure(string category)
    {
        try { _logger.LogWarning("Telegram endpoint operator alert retained. category={Category}", category); }
        catch { /* Durable delivery state remains authoritative if logging is unavailable. */ }
    }

    /// <summary>Renders safe Persian operator text at send time rather than persisting a body.</summary>
    /// <param name="alert">Secret-free frozen incident metadata.</param>
    /// <returns>Plain-text Persian alert with fixed category labels and safe bot identity.</returns>
    private static string Render(TelegramEndpointAlert alert)
    {
        var title = alert.Category switch
        {
            "migration_started" => "انتقال Telegram API آغاز شد",
            "migration_failed" => "انتقال Telegram API ناموفق بود",
            "migration_succeeded" => "انتقال Telegram API تکمیل شد",
            "local_outage" => "سرویس Local Telegram API از دسترس خارج شد",
            "local_recovered" => "سرویس Local Telegram API بازیابی شد",
            "fallback_pending" => "بازیابی Cloud منتظر پاک‌سازی ایمن Local است",
            "cloud_recovered" => "سرویس ربات روی Cloud بازیابی شد",
            _ => "وضعیت Telegram API نیازمند بررسی مدیر است"
        };
        return $"🌐 {title}\nربات: {alert.BotId}\nشناسه ربات: {alert.TelegramBotId}\nوضعیت: {alert.MigrationState}\nمسیر فعال: {alert.EffectiveEndpoint}\nمسیر درخواستی: {alert.DesiredEndpoint}\nنسل مسیر: {alert.Generation}";
    }
}
