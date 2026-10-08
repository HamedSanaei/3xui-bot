using Adminbot.Domain;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Exceptions;

namespace Adminbot.Services.TelegramEndpoints;

/// <summary>Delivers durable endpoint incidents through only an explicitly reserved independent Cloud-only owned bot.</summary>
/// <remarks>Read-only pre-send checks may retry. Once the durable send boundary is written, interruptions or ambiguous failures are never automatically replayed.</remarks>
public sealed class TelegramEndpointNotificationWorker : BackgroundService
{
    /// <summary>Endpoint-only outbox store.</summary>
    private readonly TelegramEndpointStore _store;
    /// <summary>Exact current runtime bot definitions; default lookup fallback is deliberately never used.</summary>
    private readonly BotRegistry _registry;
    /// <summary>Shared trusted control-client provider, never a migrating bot's ordinary routing facade.</summary>
    private readonly BotClientProvider _clients;
    /// <summary>Validated explicit notifier settings and retry bounds.</summary>
    private readonly TelegramEndpointRoutingOptions _options;
    /// <summary>Operational logger receives only fixed failure categories, never exceptions or tokens.</summary>
    private readonly ILogger<TelegramEndpointNotificationWorker> _logger;
    /// <summary>Clock for deadlines and deterministic recovery tests.</summary>
    private readonly TimeProvider _clock;

    /// <summary>Creates the durable global-superadmin notification worker.</summary>
    /// <param name="store">Required endpoint-only transactional store.</param>
    /// <param name="options">Required validated trusted notifier settings.</param>
    /// <param name="registry">Required runtime registry of exact enabled owned bots.</param>
    /// <param name="clients">Required shared Cloud control transport provider.</param>
    /// <param name="logger">Required operational logger; raw provider errors are never passed to it.</param>
    /// <param name="clock">Optional UTC clock; defaults to the system clock.</param>
    /// <remarks>Construction does not send, log out, migrate, or modify any bot or container.</remarks>
    public TelegramEndpointNotificationWorker(TelegramEndpointStore store, TelegramEndpointRoutingOptions options,
        BotRegistry registry, BotClientProvider clients, ILogger<TelegramEndpointNotificationWorker> logger, TimeProvider clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).ValidateAndSnapshot();
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Processes bounded pages outside update lanes while preserving unresolved incidents across restart.</summary>
    /// <param name="stoppingToken">Host shutdown cancellation; in-flight sends remain durably uncertain rather than replayable.</param>
    /// <returns>A task completing when the host stops the worker.</returns>
    /// <remarks>Only delivered receipts are pruned. A database outage is logged with a fixed category and never drops an intent.</remarks>
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

    /// <summary>Claims and processes one independent alert without network awaits inside SQLite work.</summary>
    /// <param name="token">Cancellation of pre-send work and the bounded Telegram request.</param>
    /// <returns>True when a due claim was processed; false when there was no due intent.</returns>
    /// <remarks>Authorization and notifier identity are rechecked immediately before send. Every post-boundary exception is quarantined as DeliveryUncertain, including shutdown.</remarks>
    /// <example><code>await worker.ProcessOneAsync(cancellationToken);</code></example>
    public async Task<bool> ProcessOneAsync(CancellationToken token)
    {
        var alert = await _store.ClaimAlertAsync(UtcNow, token);
        if (alert == null) return false;
        if (!_store.IsAuthorizedRecipient(alert.RecipientTelegramUserId))
        {
            await FinishAsync(alert, TelegramEndpointAlertStatus.ManualReview, "recipient_unauthorized");
            return true;
        }
        var bot = ResolveNotifier();
        if (bot == null)
        {
            await FinishAsync(alert, TelegramEndpointAlertStatus.Pending,
                string.IsNullOrEmpty(_options.NotificationBotId) ? "transport_unconfigured" : "transport_unavailable");
            return true;
        }
        ITelegramBotClient client;
        try
        {
            if (!await _store.IsNotifierCloudOnlyAsync(bot.Id, _options.NotificationTelegramBotId.Value, token))
            {
                await FinishAsync(alert, TelegramEndpointAlertStatus.Pending, "notifier_not_cloud");
                return true;
            }
            var state = await _store.GetOrCreateAsync(bot.Id, _options.NotificationTelegramBotId.Value, token);
            client = _clients.CreateEndpointControlClient(bot.Id, TelegramEndpointType.Cloud, state.Generation, _options.NotificationTelegramBotId.Value);
            using var preSend = CancellationTokenSource.CreateLinkedTokenSource(token);
            preSend.CancelAfter(TimeSpan.FromSeconds(_options.MigrationTimeoutSeconds));
            var me = await client.GetMe(preSend.Token);
            if (me.Id != _options.NotificationTelegramBotId.Value || ResolveNotifier() == null)
            {
                await FinishAsync(alert, TelegramEndpointAlertStatus.Pending, "notifier_identity_mismatch");
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(alert, TelegramEndpointAlertStatus.Pending, "pre_send_timeout");
            if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();
            return true;
        }
        catch
        {
            await FinishAsync(alert, TelegramEndpointAlertStatus.Pending, "pre_send_failure");
            return true;
        }
        if (!_store.IsAuthorizedRecipient(alert.RecipientTelegramUserId))
        {
            await FinishAsync(alert, TelegramEndpointAlertStatus.ManualReview, "recipient_unauthorized");
            return true;
        }
        if (ResolveNotifier() == null)
        {
            await FinishAsync(alert, TelegramEndpointAlertStatus.Pending, "transport_unavailable");
            return true;
        }
        if (!await _store.MarkAlertSendStartedAsync(alert, UtcNow, token))
        {
            if (!_store.IsAuthorizedRecipient(alert.RecipientTelegramUserId))
                await FinishAsync(alert, TelegramEndpointAlertStatus.ManualReview, "recipient_unauthorized");
            return true;
        }
        // From this durable boundary onward, even shutdown or a lost acknowledgment must never recreate the send.
        try
        {
            using var sending = CancellationTokenSource.CreateLinkedTokenSource(token);
            sending.CancelAfter(TimeSpan.FromSeconds(_options.MigrationTimeoutSeconds));
            await client.SendMessage(new ChatId(alert.RecipientTelegramUserId), Render(alert), cancellationToken: sending.Token);
        }
        catch (ApiRequestException exception) when (exception.ErrorCode == 429)
        {
            // A typed rejection is proof of no delivery; never apply this retry path to lost replies or gateway errors.
            using var finishing = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _store.RetryRateLimitedAlertAsync(alert, UtcNow, exception.Parameters?.RetryAfter, finishing.Token);
            return true;
        }
        catch (ApiRequestException exception) when (exception.ErrorCode is 400 or 401 or 403)
        {
            await FinishAsync(alert, TelegramEndpointAlertStatus.ManualReview, "send_rejected");
            return true;
        }
        catch
        {
            await FinishAsync(alert, TelegramEndpointAlertStatus.DeliveryUncertain, "send_uncertain");
            return true;
        }
        await FinishAsync(alert, TelegramEndpointAlertStatus.Delivered, null);
        return true;
    }

    /// <summary>Current UTC wall-clock observation used for persisted deadlines.</summary>
    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    /// <summary>Resolves only the explicit enabled owned notifier with its exact BotFather identity.</summary>
    /// <returns>The exact current owned bot, or null; no default bot, assistant, tenant, or id normalization is accepted.</returns>
    private BotInstanceConfig ResolveNotifier()
    {
        if (string.IsNullOrEmpty(_options.NotificationBotId) || _options.NotificationTelegramBotId is not > 0) return null;
        return _registry.Bots.FirstOrDefault(x => string.Equals(x.Id, _options.NotificationBotId, StringComparison.Ordinal) &&
            x.Enabled && x.Type == BotInstanceTypes.Owned && !x.IsSalesAssistant &&
            TelegramBotTokenIdentity.ExtractBotId(x.Token) == _options.NotificationTelegramBotId);
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
