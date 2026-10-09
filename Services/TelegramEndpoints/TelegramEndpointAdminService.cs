using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Adminbot.Domain;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Adminbot.Services.TelegramEndpoints;

/// <summary>Exposes the identity-bound endpoint coordinator to the administration panel without coupling UI tests to hosted workers.</summary>
/// <remarks>Implementations enqueue migration intents rather than running logout or draining the hosting receiver inside a callback.</remarks>
public interface ITelegramEndpointAdministration
{
    /// <summary>Gets current configured bot identities, including disabled and incomplete configurations.</summary>
    /// <param name="cancellationToken">Cancels the inventory read.</param>
    /// <returns>Detached metadata without credentials; the list may be empty.</returns>
    Task<IReadOnlyList<TelegramEndpointState>> GetInventoryAsync(CancellationToken cancellationToken);
    /// <summary>Reads the current identity and endpoint state of an exact configured internal bot id.</summary>
    /// <param name="botId">Required registry bot id, never a token or Telegram user id.</param>
    /// <param name="cancellationToken">Cancels the metadata read.</param>
    /// <returns>Detached state for the current identity.</returns>
    Task<TelegramEndpointState> GetStatusAsync(string botId, CancellationToken cancellationToken);
    /// <summary>Queues an authorized explicit endpoint migration with optimistic control revision checking.</summary>
    /// <param name="botId">Exact registry bot id selected by the administrator.</param>
    /// <param name="target">Cloud or Local; never a caller-provided URL.</param>
    /// <param name="actor">Positive Telegram id from the authenticated callback sender.</param>
    /// <param name="expectedControlRevision">Revision rendered in the confirmed control message.</param>
    /// <param name="expectedTelegramBotId">Exact positive BotFather identity bound into the confirmed session; checked atomically under the bot's mutation lock.</param>
    /// <param name="cancellationToken">Cancels intent persistence, not a subsequently queued migration.</param>
    /// <returns>A closed result code such as accepted, stale, busy, denied or control_path_missing.</returns>
    /// <remarks>Starting Cloud-to-Local migration requires a separate enabled owned Cloud control identity. The background worker rechecks this before logout; no callback waits for its own receiver drain.</remarks>
    Task<string> RequestMigrationAsync(string botId, TelegramEndpointType target, long actor, long expectedControlRevision, long expectedTelegramBotId, CancellationToken cancellationToken);
    /// <summary>Persists a super-admin's explicit automatic-fallback preference.</summary>
    /// <param name="botId">Exact selected internal bot id.</param>
    /// <param name="enabled">Requested target preference, never a blind toggle.</param>
    /// <param name="actor">Authorized callback sender's Telegram user id.</param>
    /// <param name="expectedControlRevision">Current rendered control revision.</param>
    /// <param name="expectedTelegramBotId">Exact positive BotFather identity rendered in the session; identity replacement must return stale even when revisions match.</param>
    /// <param name="cancellationToken">Cancels the preference write.</param>
    /// <returns>A closed safe result code.</returns>
    Task<string> SetAutoFailoverAsync(string botId, bool enabled, long actor, long expectedControlRevision, long expectedTelegramBotId, CancellationToken cancellationToken);
    /// <summary>Refreshes safe health observations without activating an endpoint or replaying logout.</summary>
    /// <param name="botId">Exact internal bot id to observe.</param>
    /// <param name="cancellationToken">Cancels the bounded observation.</param>
    /// <returns>A task completing after safe observations are recorded.</returns>
    Task RefreshHealthAsync(string botId, CancellationToken cancellationToken);
    /// <summary>Reads bounded sanitized migration history for the current configured identity.</summary>
    /// <param name="botId">Exact internal registry id.</param>
    /// <param name="cancellationToken">Cancels the history read.</param>
    /// <returns>Detached safe audit metadata, possibly empty.</returns>
    Task<IReadOnlyList<TelegramEndpointHistory>> GetHistoryAsync(string botId, CancellationToken cancellationToken);
    /// <summary>Gets tokenless shared Local server reachability, not bot authentication.</summary>
    TelegramEndpointSharedHealth SharedLocalHealth { get; }
    /// <summary>Gets the last observed number of undelivered or uncertain persisted alerts.</summary>
    int PendingAlertCount { get; }
    /// <summary>Reads the current durable number of undelivered or uncertain operator alerts.</summary>
    /// <param name="cancellationToken">Cancels the outbox count read.</param>
    /// <returns>A nonnegative count; zero does not guarantee future Telegram delivery.</returns>
    Task<int> GetPendingAlertCountAsync(CancellationToken cancellationToken);
}

/// <summary>Provides the private owned-bot Persian endpoint panel with message-bound, single-use random controls.</summary>
/// <remarks>Global AdminsUserIds is rechecked on every update. Tenant owners, customers, assistant hosts and group chats cannot enter.
/// Sessions contain no credentials and are bounded to 512 entries with ten-minute expiry. The coordinator alone changes routes.</remarks>
public sealed class TelegramEndpointAdminService
{
    /// <summary>The exact owned administration-menu entry; /telegram_api provides an alternate healthy owned-host path.</summary>
    public const string Action = "🌐 مدیریت Telegram API";
    private const string Prefix = "tep:";
    private const int PageSize = 6;
    private const int MaxSessions = 512;
    private readonly ITelegramEndpointAdministration _coordinator;
    private readonly AppConfig _configuration;
    /// <summary>Optional live global configuration; production revocations take effect without a restart.</summary>
    private readonly Microsoft.Extensions.Configuration.IConfiguration _liveConfiguration;
    private readonly BotRegistry _registry;
    private readonly TelegramEndpointRoutingOptions _options;
    private readonly ILogger<TelegramEndpointAdminService> _logger;
    private readonly TelegramInteractionTimeouts _timeouts;
    private readonly TimeProvider _time;
    /// <summary>Serializes session consumption only; no network or database work occurs under this lock.</summary>
    private readonly object _sync = new();
    private readonly Dictionary<string, PanelSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>Creates the singleton panel with its global authorization, safe coordinator and fixed interaction budgets.</summary>
    /// <param name="coordinator">Required identity-bound metadata and queued-command implementation.</param>
    /// <param name="configuration">Required global super-admin allow-list, not tenant authorization.</param>
    /// <param name="registry">Required exact owned/tenant/assistant registry; fallback lookup is deliberately not used.</param>
    /// <param name="options">Validated endpoint resource settings, never user-supplied callback URLs.</param>
    /// <param name="logger">Required safe diagnostics logger; raw exceptions and callback contents are never logged.</param>
    /// <param name="timeouts">Optional immutable callback budget; defaults to the existing production deadline.</param>
    /// <param name="timeProvider">Optional deterministic UTC clock for session expiry and cooldown presentation.</param>
    /// <param name="liveConfiguration">Optional live application configuration; production supplies it so a removed global admin cannot use stale startup authority.</param>
    /// <example><code>await panel.TryHandleAsync(hostingBotId, client, update, cancellationToken);</code></example>
    public TelegramEndpointAdminService(ITelegramEndpointAdministration coordinator, AppConfig configuration,
        BotRegistry registry, TelegramEndpointRoutingOptions options, ILogger<TelegramEndpointAdminService> logger,
        TelegramInteractionTimeouts timeouts = null, TimeProvider timeProvider = null,
        Microsoft.Extensions.Configuration.IConfiguration liveConfiguration = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeouts = timeouts ?? TelegramInteractionTimeouts.Production;
        _time = timeProvider ?? TimeProvider.System;
        _liveConfiguration = liveConfiguration;
    }

    /// <summary>Consumes endpoint-panel entries or callbacks before stale business flows on a healthy owned host.</summary>
    /// <param name="hostingBotId">Exact internal registry id of the bot receiving this update; never a default fallback id.</param>
    /// <param name="client">Required client belonging to that receiving bot.</param>
    /// <param name="update">Required authenticated Telegram update; the callback's actor and message supply all security bindings.</param>
    /// <param name="token">Receiver/update cancellation token; not retained by queued migrations.</param>
    /// <returns>True when this is a panel update, including rejected attempts; false for unrelated business updates.</returns>
    /// <remarks>Each valid callback consumes the entire displayed session before awaiting work, rejecting replay and concurrent taps.
    /// Migration progress is rendered before an intent is queued, so migrating this host never needs a post-fence ordinary send.</remarks>
    /// <exception cref="OperationCanceledException">The update caller cancels its work.</exception>
    /// <example><code>if (await panel.TryHandleAsync(bot.Id, botClient, update, token)) return;</code></example>
    public async Task<bool> TryHandleAsync(string hostingBotId, ITelegramBotClient client, Update update, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(update);
        var callback = update.CallbackQuery;
        var entry = update.Message;
        var isCallback = callback?.Data?.StartsWith(Prefix, StringComparison.Ordinal) == true;
        var isEntry = entry?.Text == Action || IsCommand(entry?.Text, hostingBotId);
        if (!isCallback && !isEntry) return false;
        var message = isCallback ? callback.Message : entry;
        var actor = isCallback ? callback.From?.Id ?? 0 : entry.From?.Id ?? 0;
        if (!Authorized(hostingBotId, actor, message))
        {
            if (isCallback) await AckAsync(client, callback, hostingBotId, "این بخش فقط در گفت‌وگوی خصوصی ربات اصلی برای مدیر کل فعال است.", token);
            return true;
        }
        try
        {
            if (!isCallback)
            {
                await RenderAsync(hostingBotId, client, actor, message.Chat.Id, 0, new PanelCommand(CommandKind.Inventory), token);
                return true;
            }
            PanelSession session;
            PanelCommand command;
            lock (_sync)
            {
                PruneSessions();
                var parts = callback.Data.Split(':');
                if (Encoding.UTF8.GetByteCount(callback.Data) > 64 || parts.Length != 3 ||
                    !_sessions.TryGetValue(parts[1], out session) || session.Host != hostingBotId ||
                    session.Actor != actor || session.Chat != message.Chat.Id || session.MessageId != message.Id ||
                    !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
                    index < 0 || index >= session.Commands.Count)
                {
                    session = null;
                    command = null;
                }
                else
                {
                    command = session.Commands[index];
                    _sessions.Remove(parts[1]);
                }
            }
            if (session == null)
            {
                await AckAsync(client, callback, hostingBotId, "این دکمه نامعتبر، مصرف‌شده یا منقضی است؛ پنل را دوباره باز کنید.", token);
                return true;
            }
            var host = await _coordinator.GetStatusAsync(hostingBotId, token);
            var current = command.BotId == null ? host : await _coordinator.GetStatusAsync(command.BotId, token);
            if (host.TelegramBotId != session.HostIdentity || current.TelegramBotId != command.Identity ||
                current.ControlRevision != command.Revision)
            {
                await AckAsync(client, callback, hostingBotId, "وضعیت یا هویت ربات تغییر کرده است؛ پنل تازه نمایش داده می‌شود.", token);
                var freshView = command.BotId != null && host.TelegramBotId == session.HostIdentity &&
                    current.TelegramBotId == command.Identity
                    ? command with { Kind = CommandKind.Detail, Revision = current.ControlRevision }
                    : new PanelCommand(CommandKind.Inventory);
                await RenderAsync(hostingBotId, client, actor, message.Chat.Id, message.Id, freshView, token);
                return true;
            }
            await AckAsync(client, callback, hostingBotId, null, token);
            if (command.Kind == CommandKind.Migrate)
            {
                // Publish refresh controls before the queued operation can fence this very receiver.
                await RenderAsync(hostingBotId, client, actor, message.Chat.Id, message.Id,
                    command with { Kind = CommandKind.Detail }, token,
                    "در حال ثبت درخواست انتقال؛ نتیجه نهایی را با «تازه‌سازی» یا /telegram_api در ربات اصلی سالم دیگری ببینید.");
                var result = await _coordinator.RequestMigrationAsync(command.BotId, command.Target, actor, command.Revision, command.Identity, token);
                if (result != "accepted")
                    await RenderAsync(hostingBotId, client, actor, message.Chat.Id, message.Id,
                        command with { Kind = CommandKind.Detail }, token, ResultLabel(result));
                return true;
            }
            string notice = null;
            if (command.Kind == CommandKind.Failover)
                notice = ResultLabel(await _coordinator.SetAutoFailoverAsync(command.BotId, command.Enabled, actor, command.Revision, command.Identity, token));
            if (command.Kind == CommandKind.Refresh) await _coordinator.RefreshHealthAsync(command.BotId, token);
            await RenderAsync(hostingBotId, client, actor, message.Chat.Id, message.Id,
                command.Kind is CommandKind.Failover or CommandKind.Refresh ? command with { Kind = CommandKind.Detail } : command, token, notice);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Neither exception text nor callback/token-derived values belong in Telegram or diagnostics.
            _logger.LogWarning("Telegram endpoint administration could not complete a metadata or control operation.");
            if (isCallback) await AckAsync(client, callback, hostingBotId,
                "پنل فعلاً در دسترس نیست؛ هیچ انتقالی را تکرار نکنید. با /telegram_api وضعیت ثبت‌شده را بررسی کنید.", token);
            else await SendTextAsync(client, message.Chat.Id,
                "خواندن وضعیت مدیریت API انجام نشد؛ با /telegram_api دوباره وضعیت را بررسی کنید.", token);
        }
        return true;
    }

    /// <summary>Checks exact owned-host authorization without the registry's unknown-id default fallback.</summary>
    /// <param name="host">Required incoming internal bot id.</param>
    /// <param name="actor">Incoming sender's numeric Telegram id.</param>
    /// <param name="message">Callback-bound or entry message; null and group messages are denied.</param>
    /// <returns>True only for a currently authorized global admin's private self-chat on an enabled non-assistant owned host.</returns>
    private bool Authorized(string host, long actor, Message message)
    {
        var bot = _registry.Bots.FirstOrDefault(x => string.Equals(x.Id, host, StringComparison.Ordinal));
        return bot != null && bot.Enabled && bot.Type == BotInstanceTypes.Owned && !bot.IsSalesAssistant &&
            TelegramEndpointAdministratorPolicy.IsAuthorized(_liveConfiguration, _configuration, actor) &&
            message?.Chat?.Type == ChatType.Private && message.Chat.Id == actor;
    }

    /// <summary>Recognizes only the alternate administration command addressed to this exact receiving bot.</summary>
    /// <param name="text">Incoming text, possibly null.</param>
    /// <param name="host">Exact receiving registry id.</param>
    /// <returns>True for /telegram_api or its exact username-addressed variant.</returns>
    private bool IsCommand(string text, string host)
    {
        if (text == "/telegram_api") return true;
        var username = _registry.Bots.FirstOrDefault(x => x.Id == host)?.Username?.TrimStart('@');
        return !string.IsNullOrWhiteSpace(username) && string.Equals(text, "/telegram_api@" + username, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Renders bounded metadata and a fresh message-bound session; no endpoint mutation occurs here.</summary>
    /// <param name="host">Exact authorized receiving bot id.</param>
    /// <param name="client">Receiving bot's ordinary client.</param>
    /// <param name="actor">Authorized Telegram sender id.</param>
    /// <param name="chat">Private Telegram chat id equal to actor.</param>
    /// <param name="messageId">Existing control message id, or zero to send a new message before binding buttons.</param>
    /// <param name="view">Server-held view command, not user callback fields.</param>
    /// <param name="token">Cancels UI metadata and bounded Telegram work.</param>
    /// <param name="notice">Optional fixed Persian outcome/progress label without raw error data.</param>
    /// <returns>A task completing after the new control keyboard is published.</returns>
    private async Task RenderAsync(string host, ITelegramBotClient client, long actor, long chat, int messageId,
        PanelCommand view, CancellationToken token, string notice = null)
    {
        var hostState = await _coordinator.GetStatusAsync(host, token);
        var commands = new List<PanelCommand>();
        var rows = new List<InlineKeyboardButton[]>();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var text = new StringBuilder(Action).AppendLine().AppendLine(notice);
        var pending = await _coordinator.GetPendingAlertCountAsync(token);
        text.AppendLine($"اعلان‌های تحویل‌نشده/نامطمئن: {pending}");
        text.AppendLine(await NotificationLabelAsync(token));
        if (view.Kind == CommandKind.Inventory)
        {
            var inventory = await _coordinator.GetInventoryAsync(token);
            var page = Math.Clamp(view.Page, 0, Math.Max(0, (inventory.Count - 1) / PageSize));
            text.AppendLine($"فهرست ربات‌ها — صفحه {page + 1}/{Math.Max(1, (inventory.Count + PageSize - 1) / PageSize)}");
            foreach (var state in inventory.Skip(page * PageSize).Take(PageSize))
            {
                text.AppendLine($"{BotLabel(state.BotId)} | انتخاب: {(state.TelegramBotId > 0 ? EndpointLabel(state.DesiredEndpoint) : "نامشخص؛ هویت موجود نیست")} | مؤثر: {(state.TelegramBotId > 0 ? EndpointLabel(state.EffectiveEndpoint) : "نامشخص؛ مسیر منتشر نشده")} | {StateLabel(state.MigrationState)}");
                AddButton(rows, commands, nonce, "جزئیات «" + BotLabel(state.BotId) + "»", new PanelCommand(CommandKind.Detail, state.BotId, state.TelegramBotId, state.ControlRevision, page));
            }
            if (page > 0) AddButton(rows, commands, nonce, "◀️ صفحه قبل", new PanelCommand(CommandKind.Inventory, Identity: hostState.TelegramBotId, Revision: hostState.ControlRevision, Page: page - 1));
            if ((page + 1) * PageSize < inventory.Count) AddButton(rows, commands, nonce, "صفحه بعد ▶️", new PanelCommand(CommandKind.Inventory, Identity: hostState.TelegramBotId, Revision: hostState.ControlRevision, Page: page + 1));
            AddButton(rows, commands, nonce, "🔄 تازه‌سازی فهرست", new PanelCommand(CommandKind.Inventory, Identity: hostState.TelegramBotId, Revision: hostState.ControlRevision, Page: page));
        }
        else
        {
            var state = await _coordinator.GetStatusAsync(view.BotId, token);
            var detail = view with { Kind = CommandKind.Detail, Identity = state.TelegramBotId, Revision = state.ControlRevision };
            text.AppendLine($"ربات: {BotLabel(state.BotId)} — شناسه Telegram: {state.TelegramBotId}");
            text.AppendLine($"انتخاب مدیر: {(state.TelegramBotId > 0 ? EndpointLabel(state.DesiredEndpoint) : "نامشخص؛ هویت موجود نیست")} | مسیر مؤثر: {(state.TelegramBotId > 0 ? EndpointLabel(state.EffectiveEndpoint) : "نامشخص؛ مسیر منتشر نشده")}");
            text.AppendLine($"وضعیت: {StateLabel(state.MigrationState)} | نسل مسیر: {(state.TelegramBotId > 0 ? state.Generation.ToString(CultureInfo.InvariantCulture) : "نامشخص")}");
            var configured = _registry.Bots.FirstOrDefault(x => x.Id == state.BotId);
            text.AppendLine($"پیکربندی ربات: {(configured?.Enabled == true ? "فعال" : "غیرفعال")} | هویت معتبر: {(state.TelegramBotId > 0 ? "بله" : "خیر؛ توکن/هویت پیکربندی نشده")}");
            var health = _coordinator.SharedLocalHealth;
            text.AppendLine($"سرور محلی: {(health.Reachable ? "در دسترس؛ احراز هویت ربات نیست" : "در دسترس نیست/هنوز بررسی نشده")}");
            text.AppendLine($"بررسی سرور UTC: {Utc(health.LastCheckedAtUtc)} | آخرین موفقیت سرور UTC: {Utc(health.LastSuccessAtUtc)}");
            text.AppendLine($"آخرین بررسی ربات UTC: {Utc(state.LastHealthCheckAtUtc)}");
            text.AppendLine($"آخرین موفقیت ربات UTC: {Utc(state.LastSuccessfulHealthAtUtc)}");
            text.AppendLine($"آخرین انتقال UTC: {Utc(state.LastMigrationAtUtc)} | تلاش ایمن بعدی UTC: {Utc(state.NextAttemptAtUtc)}");
            text.AppendLine($"آخرین خطا: {FailureLabel(state.LastFailureCategory)} | زمان UTC: {Utc(state.LastFailureAtUtc)}");
            var remaining = state.CloudReuseEligibleAtUtc.HasValue ? Math.Max(0, (state.CloudReuseEligibleAtUtc.Value - _time.GetUtcNow().UtcDateTime).TotalSeconds) : 0;
            text.AppendLine(state.TelegramBotId > 0
                ? $"انتظار مجاز Cloud: {Math.Ceiling(remaining)} ثانیه | موعد UTC: {Utc(state.CloudReuseEligibleAtUtc)}"
                : "انتظار مجاز Cloud: نامشخص؛ هویت معتبر موجود نیست");
            text.AppendLine($"بازگشت اضطراری خودکار: {(state.TelegramBotId <= 0 ? "نامشخص؛ هویت موجود نیست" : state.AutoFailoverEnabled ? "روشن" : "خاموش")} | بازگشت خودکار به Local: {(_options.AutomaticFailback ? "روشن" : "خاموش")}");
            text.AppendLine($"مدیریت مسیر: {(_options.Enabled ? "فعال" : "غیرفعال در پیکربندی")}");
            text.AppendLine($"پیش‌نیاز فایل Local: {(!string.IsNullOrWhiteSpace(_options.LocalFileServerRoot) && !string.IsNullOrWhiteSpace(_options.LocalFileHostRoot) ? "نگاشت مسیر تنظیم شده" : "نگاشت مطمئن مسیر سرور/میزبان تنظیم نشده؛ انتقال Local مجاز نیست")}");
            if (state.LogoutAttemptedAtUtc.HasValue && !state.LogoutAcknowledgedAtUtc.HasValue)
                text.AppendLine("⚠️ خروج نامطمئن/در انتظار: تکرار logOut یا فعال‌سازی اجباری Cloud مجاز نیست؛ بررسی دستی لازم است.");
            if (state.TelegramBotId <= 0)
            {
                text.AppendLine("⚠️ بدون هویت معتبر، مسیر Cloud فرض نمی‌شود و انتقال/بازگشت اضطراری مجاز نیست. ابتدا پیکربندی ربات را اصلاح کنید.");
                AddButton(rows, commands, nonce, "🔄 تازه‌سازی وضعیت", detail);
                text.AppendLine("تاریخچه هویت جاری در دسترس نیست؛ هیچ هویتی از سوابق قدیمی حدس زده نمی‌شود.");
            }
            else if (view.Kind == CommandKind.Confirm)
            {
                text.AppendLine($"⚠️ انتقال به {EndpointLabel(view.Target)} را تأیید می‌کنید؟ دریافت پیام این ربات موقتاً متوقف می‌شود. خروج bot-specific و انتظار رسمی Cloud لازم است؛ سرویس مشترک دست‌کاری نمی‌شود.");
                AddButton(rows, commands, nonce, "✅ تأیید انتقال به " + EndpointLabel(view.Target), detail with { Kind = CommandKind.Migrate, Target = view.Target });
                AddButton(rows, commands, nonce, "❌ انصراف", detail);
            }
            else
            {
                AddButton(rows, commands, nonce, "🔄 تازه‌سازی وضعیت و سلامت", detail with { Kind = CommandKind.Refresh });
                AddButton(rows, commands, nonce, "انتقال به Cloud…", detail with { Kind = CommandKind.Confirm, Target = TelegramEndpointType.Cloud });
                AddButton(rows, commands, nonce, "انتقال به Local…", detail with { Kind = CommandKind.Confirm, Target = TelegramEndpointType.Local });
                AddButton(rows, commands, nonce, state.AutoFailoverEnabled ? "خاموش‌کردن بازگشت اضطراری" : "روشن‌کردن بازگشت اضطراری", detail with { Kind = CommandKind.Failover, Enabled = !state.AutoFailoverEnabled });
                var history = await _coordinator.GetHistoryAsync(state.BotId, token);
                text.AppendLine("تاریخچه اخیر:");
                foreach (var item in history.Take(6)) text.AppendLine(HistoryLabel(item));
                if (history.Count == 0) text.AppendLine("رویدادی ثبت نشده است.");
            }
            AddButton(rows, commands, nonce, "📋 بازگشت به فهرست", new PanelCommand(CommandKind.Inventory, Identity: hostState.TelegramBotId, Revision: hostState.ControlRevision, Page: view.Page));
        }
        var body = text.ToString();
        if (body.Length > 3900)
        {
            var length = char.IsHighSurrogate(body[3899]) ? 3899 : 3900;
            body = body[..length] + "\n…";
        }
        if (messageId == 0) messageId = (await SendTextAsync(client, chat, body, token)).Id;
        lock (_sync)
        {
            PruneSessions();
            foreach (var key in _sessions.Where(x => x.Value.Host == host && x.Value.Actor == actor && x.Value.Chat == chat && x.Value.MessageId == messageId).Select(x => x.Key).ToArray()) _sessions.Remove(key);
            if (_sessions.Count >= MaxSessions) _sessions.Remove(_sessions.MinBy(x => x.Value.Expires).Key);
            _sessions[nonce] = new PanelSession(host, hostState.TelegramBotId, actor, chat, messageId, _time.GetUtcNow().AddMinutes(10), commands);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await client.EditMessageText(chat, messageId, body, replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: deadline.Token);
    }

    /// <summary>Adds one short opaque callback and its server-held operation without a captured rendering closure.</summary>
    /// <param name="rows">Required output keyboard rows for this rendering.</param>
    /// <param name="commands">Required server-held commands bound to the new session.</param>
    /// <param name="nonce">Cryptographically random 24-hex session nonce, not a bot token.</param>
    /// <param name="label">Fixed Persian button label or sanitized configured bot name.</param>
    /// <param name="command">Immutable identity/revision-bound server-held control.</param>
    /// <remarks>Payloads contain no URLs or operation parameters and stay below Telegram's 64-byte bound.</remarks>
    private static void AddButton(List<InlineKeyboardButton[]> rows, List<PanelCommand> commands,
        string nonce, string label, PanelCommand command)
    {
        var data = Prefix + nonce + ":" + commands.Count.ToString(CultureInfo.InvariantCulture);
        commands.Add(command);
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData(label, data) });
    }

    /// <summary>Describes logger-channel prerequisites without claiming posting permission or notification delivery.</summary>
    /// <param name="token">Cancellation checked before reading trusted local configuration.</param>
    /// <returns>A fixed Persian logger prerequisite label; the worker verifies Cloud admission and channel posting rights.</returns>
    /// <remarks>Private Super Admin chats and dedicated notification identities are never selected. No Telegram request or migration occurs while rendering this label.</remarks>
    private Task<string> NotificationLabelAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var channel = Adminbot.Domain.Logging.TelegramDestination.SelectValid(
            _liveConfiguration != null ? _liveConfiguration["loggerChannel"] : _configuration.LoggerChannel,
            _registry.DefaultBot?.LoggerChannel);
        return Task.FromResult(string.IsNullOrEmpty(channel)
            ? "⚠️ کانال loggerChannel تنظیم نشده است؛ هشدارها در صف پایدار منتظر می‌مانند و به گفت‌وگوی خصوصی ارسال نمی‌شوند."
            : "اعلان‌ها مستقیماً به کانال لاگر ارسال می‌شوند؛ ربات Cloud فعال و مجوز ارسال بررسی می‌شود. بدون فرستنده واجدشرایط، هشدار Pending می‌ماند؛ ارسال نامطمئن تکرار نمی‌شود.");
    }

    /// <summary>Removes expired controls while holding the session lock.</summary>
    /// <remarks>Expiry is strict at ten minutes, and expired controls can never be consumed.</remarks>
    private void PruneSessions()
    {
        var now = _time.GetUtcNow();
        foreach (var key in _sessions.Where(x => x.Value.Expires <= now).Select(x => x.Key).ToArray()) _sessions.Remove(key);
    }

    /// <summary>Acknowledges the incoming callback through the existing best-effort deadline policy.</summary>
    /// <param name="client">Exact receiving client.</param>
    /// <param name="callback">Incoming callback supplying id and authenticated sender.</param>
    /// <param name="host">Exact hosting registry id for safe diagnostics.</param>
    /// <param name="text">Fixed Persian error/toast, or null for a silent acknowledgement.</param>
    /// <param name="token">Update cancellation token.</param>
    /// <returns>A task completing after a bounded acknowledgement attempt.</returns>
    private Task<bool> AckAsync(ITelegramBotClient client, CallbackQuery callback, string host, string text, CancellationToken token)
        => TelegramCallbackAnswerPolicy.TryAnswerAsync(client, callback.Id, text, showAlert: text != null,
            cancellationToken: token, logger: _logger, botId: host, telegramUserId: callback.From?.Id, timeout: _timeouts.CallbackAnswer);

    /// <summary>Sends one bounded plain-text panel message without markup, credentials or unsafe retries.</summary>
    /// <param name="client">Receiving owned-bot client.</param>
    /// <param name="chat">Authorized private chat id.</param>
    /// <param name="body">Bounded Persian body without provider error strings.</param>
    /// <param name="token">Update cancellation token.</param>
    /// <returns>The sent message whose numeric id binds the subsequently published controls.</returns>
    private static async Task<Message> SendTextAsync(ITelegramBotClient client, long chat, string body, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        return await client.SendMessage(chat, body, cancellationToken: deadline.Token);
    }

    /// <summary>Returns a strictly sanitized current Telegram username or internal label without arbitrary configured text.</summary>
    /// <param name="id">Current internal registry bot id.</param>
    /// <returns>A bounded credential-free ASCII bot label or a fixed Persian fallback.</returns>
    private string BotLabel(string id)
    {
        var username = _registry.Bots.FirstOrDefault(x => x.Id == id)?.Username?.TrimStart('@');
        var value = string.IsNullOrWhiteSpace(username) ? id : username;
        return !string.IsNullOrWhiteSpace(value) && value.Length <= 64 && value.All(x => char.IsAsciiLetterOrDigit(x) || x is '_' or '-') ? value : "ربات پیکربندی‌شده";
    }

    /// <summary>Formats an optional timestamp explicitly in UTC for operator cooldown/history interpretation.</summary>
    /// <param name="value">Persisted UTC timestamp, or null for never observed.</param>
    /// <returns>Invariant UTC timestamp or a fixed Persian absence label.</returns>
    private static string Utc(DateTime? value) => value?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "ثبت نشده";

    /// <summary>Labels the closed endpoint enum without displaying arbitrary callback input.</summary>
    /// <param name="value">Persisted endpoint enum.</param>
    /// <returns>Fixed Cloud/Local label, or a fixed invalid-state label.</returns>
    private static string EndpointLabel(TelegramEndpointType value) => value switch { TelegramEndpointType.Cloud => "Cloud (ابری)", TelegramEndpointType.Local => "Local (محلی)", _ => "نامعتبر" };

    /// <summary>Labels every protocol state in readable Persian, including unsafe uncertain states.</summary>
    /// <param name="value">Persisted closed migration-state enum.</param>
    /// <returns>A fixed safe Persian description.</returns>
    private static string StateLabel(TelegramEndpointMigrationState value) => value switch
    {
        TelegramEndpointMigrationState.Cloud => "Cloud فعال",
        TelegramEndpointMigrationState.CheckingLocal => "بررسی پیش‌نیاز Local",
        TelegramEndpointMigrationState.CloudLogoutPending => "خروج Cloud در انتظار",
        TelegramEndpointMigrationState.CloudLogoutUncertain => "خروج Cloud نامطمئن؛ بررسی دستی",
        TelegramEndpointMigrationState.SwitchingToLocal => "در حال فعال‌سازی Local",
        TelegramEndpointMigrationState.Local => "Local فعال",
        TelegramEndpointMigrationState.LocalDegraded => "Local ناپایدار",
        TelegramEndpointMigrationState.LocalUnavailable => "Local در دسترس نیست",
        TelegramEndpointMigrationState.FallbackPending => "بازگشت اضطراری در انتظار پاک‌سازی ایمن",
        TelegramEndpointMigrationState.LocalLogoutPending => "خروج Local در انتظار",
        TelegramEndpointMigrationState.LocalLogoutUncertain => "خروج Local نامطمئن؛ بررسی دستی",
        TelegramEndpointMigrationState.CloudWait => "انتظار رسمی استفاده مجدد Cloud",
        TelegramEndpointMigrationState.SwitchingToCloud => "در حال فعال‌سازی Cloud",
        TelegramEndpointMigrationState.CloudRecovered => "Cloud بازیابی‌شده",
        TelegramEndpointMigrationState.MigrationFailed => "انتقال ناموفق؛ وضعیت ثبت‌شده را بررسی کنید",
        TelegramEndpointMigrationState.ManualInterventionRequired => "نیازمند بررسی دستی؛ اجبار مسیر مجاز نیست",
        _ => "وضعیت نامعتبر؛ بررسی دستی"
    };

    /// <summary>Maps closed command outcomes to safe Persian without echoing unknown error strings.</summary>
    /// <param name="result">Coordinator's closed outcome code.</param>
    /// <returns>A fixed safe operational explanation.</returns>
    private static string ResultLabel(string result) => result switch
    {
        "accepted" => "درخواست ثبت شد؛ وضعیت تازه‌سازی و هشدارهای پایدار را بررسی کنید.",
        "stale" => "پنل قدیمی است؛ وضعیت جدید را بررسی کنید.",
        "busy" => "عملیات دیگری برای این ربات در جریان است؛ انتقال جدید ثبت نشد.",
        "denied" => "مجوز مدیر کل یا پیش‌نیاز ایمنی وجود ندارد؛ انتقال ثبت نشد.",
        "aliased" => "شناسه BotFather این ربات در چند تنظیم ربات مشترک است؛ انتقال ثبت نشد. ابتدا پیکربندی تکراری را اصلاح کنید؛ غیرفعال کردن ربات تکراری کافی نیست.",
        "disabled" => "مدیریت مسیر یا ربات در پیکربندی غیرفعال است.",
        "control_path_missing" => "انتقال به Local ثبت نشد: یک ربات Owned فعال و مستقل باید روی Cloud باقی بماند تا هنگام قطع Local، پنل مدیریت از مسیر دیگری در دسترس باشد.",
        "unchanged" => "وضعیت درخواستی از قبل برقرار است؛ انتقال تازه‌ای ثبت نشد.",
        "unsafe" => "خروج یا پاک‌سازی نامطمئن است؛ انتقال خودکار یا اجبار مسیر مجاز نیست و بررسی دستی لازم است.",
        "unavailable" => "ربات، هویت یا نگاشت فایل Local آماده نیست؛ انتقال ثبت نشد.",
        _ => "درخواست پذیرفته نشد؛ پیش‌نیازها و وضعیت ثبت‌شده را بررسی کنید. خروج نامطمئن را تکرار نکنید."
    };

    /// <summary>Renders a safe bounded history row using numeric actors and closed reason/outcome labels only.</summary>
    /// <param name="item">Persisted credential-free audit row.</param>
    /// <returns>A plain-text UTC actor/reason/outcome history line.</returns>
    private static string HistoryLabel(TelegramEndpointHistory item)
        => $"{Utc(item.CreatedAtUtc)} | عامل: {(item.ActorTelegramUserId.HasValue ? item.ActorTelegramUserId.Value.ToString(CultureInfo.InvariantCulture) : "سامانه")} | دلیل: {ReasonLabel(item.Reason)} | نتیجه: {HistoryOutcomeLabel(item.Outcome)} | از {EndpointLabel(item.FromEffectiveEndpoint)} به {EndpointLabel(item.ToEffectiveEndpoint)}";

    /// <summary>Labels audit reasons without echoing untrusted persisted error/provider strings.</summary>
    /// <param name="reason">Persisted closed history reason.</param>
    /// <returns>A fixed Persian reason label.</returns>
    private static string ReasonLabel(string reason) => reason switch
    {
        "migration_requested" => "درخواست انتقال مدیر کل",
        "auto_failover_changed" => "تغییر بازگشت اضطراری",
        "startup_reconciled" => "بازیابی پس از راه‌اندازی",
        "cloud_logout_intent" => "ثبت قصد خروج Cloud",
        "local_logout_intent" => "ثبت قصد خروج Local",
        "logout_acknowledged" => "خروج تأییدشده",
        "logout_uncertain" => "خروج نامطمئن؛ بدون تکرار",
        "logout_refused" => "خروج ردشده",
        "destination_starting" => "بررسی و شروع دریافت مقصد",
        "migration_succeeded" => "انتقال موفق",
        "migration_failed" => "انتقال ناموفق",
        "local_outage" => "قطعی Local",
        "local_recovered" => "بازیابی Local",
        "fallback_pending" => "انتظار بازگشت اضطراری ایمن",
        "cloud_wait" => "انتظار رسمی Cloud",
        "cloud_recovered" => "بازیابی Cloud",
        "manual_intervention" => "نیاز به بررسی دستی",
        "safe_retry_scheduled" => "تلاش ایمن زمان‌بندی‌شده",
        "automatic_failback" => "بازگشت خودکار Local",
        _ => "رویداد ایمنی/انتقال ثبت‌شده"
    };

    /// <summary>Labels closed persisted migration outcomes without echoing arbitrary history content.</summary>
    /// <param name="outcome">Persisted enum-name outcome, not raw provider text.</param>
    /// <returns>The Persian protocol-state label, or a fixed unknown-outcome label.</returns>
    private static string HistoryOutcomeLabel(string outcome)
        => Enum.TryParse<TelegramEndpointMigrationState>(outcome, out var state) && Enum.IsDefined(state)
            ? StateLabel(state) : "نتیجه ثبت‌شده نامشخص؛ وضعیت فعلی را بررسی کنید";

    /// <summary>Labels safe health failure categories without echoing unrecognized provider/error values.</summary>
    /// <param name="category">Persisted closed health error category, possibly null.</param>
    /// <returns>A fixed Persian failure label.</returns>
    private static string FailureLabel(string category) => category switch
    {
        null or "" or "none" => "ثبت نشده",
        "timeout" => "مهلت بررسی پایان یافت",
        "connection_refused" or "network" => "سرور در دسترس نیست",
        "invalid_response" => "پاسخ سرور محلی معتبر نیست",
        "identity_mismatch" => "هویت ربات مطابقت ندارد",
        "identity_alias_conflict" => "شناسه BotFather بین چند تنظیم ربات مشترک است؛ انتقال تا اصلاح پیکربندی تکراری مجاز نیست",
        "operator_control_missing" => "مسیر مستقل مدیریت از یک ربات Owned فعال روی Cloud وجود ندارد",
        "token_rejected" => "احراز هویت رد شد",
        "rate_limited" => "محدودیت نرخ Telegram؛ اثبات قطعی سرور نیست",
        "telegram_upstream" => "خطای Telegram؛ اثبات قطعی سرور نیست",
        "logout_refused" => "خروج رد شد",
        "logout_uncertain" => "خروج نامطمئن؛ بدون تکرار",
        "receiver_not_ready" => "دریافت پیام مقصد آماده نیست",
        "configuration_missing" => "پیش‌نیاز پیکربندی موجود نیست",
        _ => "خطای بررسی ثبت‌شده؛ اطلاعات حساس نمایش داده نمی‌شود"
    };

    /// <summary>Server-side control kinds; users submit only a nonce and an index, never operation parameters.</summary>
    private enum CommandKind { Inventory, Detail, Confirm, Migrate, Failover, Refresh }
    /// <summary>Immutable server-held operation bound to the exact identity and optimistic control revision.</summary>
    /// <param name="Kind">Navigation, confirmation or mutation kind.</param>
    /// <param name="BotId">Selected internal registry id, or null for hosting-bot inventory controls.</param>
    /// <param name="Identity">Exact current numeric Telegram bot identity.</param>
    /// <param name="Revision">Expected control revision unaffected by background health-only writes.</param>
    /// <param name="Page">Zero-based inventory page to restore after detail navigation.</param>
    /// <param name="Target">Explicit endpoint enum for confirmed migration only.</param>
    /// <param name="Enabled">Explicit automatic-fallback target preference.</param>
    private sealed record PanelCommand(CommandKind Kind, string BotId = null, long Identity = 0, long Revision = 0,
        int Page = 0, TelegramEndpointType Target = TelegramEndpointType.Cloud, bool Enabled = false);
    /// <summary>One message-bound one-use control session with a strict ten-minute absolute expiry.</summary>
    /// <param name="Host">Exact internal owned hosting bot id.</param>
    /// <param name="HostIdentity">Current numeric hosting bot identity; token replacement invalidates controls.</param>
    /// <param name="Actor">Authorized numeric Telegram user id.</param>
    /// <param name="Chat">Private self-chat id equal to actor.</param>
    /// <param name="MessageId">Actual sent Telegram control message id.</param>
    /// <param name="Expires">Absolute UTC expiry for all commands in this rendering.</param>
    /// <param name="Commands">Immutable-by-convention rendered server-held command list.</param>
    private sealed record PanelSession(string Host, long HostIdentity, long Actor, long Chat, int MessageId,
        DateTimeOffset Expires, IReadOnlyList<PanelCommand> Commands);
}
