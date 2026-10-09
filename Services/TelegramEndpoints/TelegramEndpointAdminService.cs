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
public partial interface ITelegramEndpointAdministration
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
/// Callback sessions contain no credentials and are bounded to 512 entries with ten-minute expiry; read-only batch reports last one hour to outlive Cloud cooldown. The coordinator alone changes routes.</remarks>
public sealed class TelegramEndpointAdminService
{
    /// <summary>The exact owned administration-menu entry; /telegram_api provides an alternate healthy owned-host path.</summary>
    public const string Action = "🌐 مدیریت Telegram API";
    private const string Prefix = "tep:";
    /// <summary>Three complete operational summaries per page keep fixed Persian diagnostics below Telegram's text limit without truncation.</summary>
    private const int PageSize = 3;
    private const int MaxSessions = 512;
    /// <summary>Absolute bounded report lifetime, longer than the official ten-minute Cloud cooldown; callback confirmations still expire after ten minutes.</summary>
    private static readonly TimeSpan BulkReportLifetime = TimeSpan.FromHours(1);
    private readonly ITelegramEndpointAdministration _coordinator;
    private readonly AppConfig _configuration;
    /// <summary>Optional live global configuration; production revocations take effect without a restart.</summary>
    private readonly Microsoft.Extensions.Configuration.IConfiguration _liveConfiguration;
    private readonly BotRegistry _registry;
    private readonly TelegramEndpointRoutingOptions _options;
    /// <summary>Authoritative startup configuration location for read-only operator diagnostics; never reads the private JSON file.</summary>
    private readonly ApplicationConfigurationSource _configurationSource;
    private readonly ILogger<TelegramEndpointAdminService> _logger;
    private readonly TelegramInteractionTimeouts _timeouts;
    private readonly TimeProvider _time;
    /// <summary>Serializes session consumption only; no network or database work occurs under this lock.</summary>
    private readonly object _sync = new();
    private readonly Dictionary<string, PanelSession> _sessions = new(StringComparer.Ordinal);
    /// <summary>Bounded process-local registration reports; individual operation state and history remain durable in the coordinator.</summary>
    private readonly Dictionary<string, BulkReport> _reports = new(StringComparer.Ordinal);

    /// <summary>Creates the singleton panel with its global authorization, safe coordinator and fixed interaction budgets.</summary>
    /// <param name="coordinator">Required identity-bound metadata and queued-command implementation.</param>
    /// <param name="configuration">Required global super-admin allow-list, not tenant authorization.</param>
    /// <param name="registry">Required exact owned/tenant/assistant registry; fallback lookup is deliberately not used.</param>
    /// <param name="options">Validated endpoint resource settings, never user-supplied callback URLs.</param>
    /// <param name="logger">Required safe diagnostics logger; raw exceptions and callback contents are never logged.</param>
    /// <param name="timeouts">Optional immutable callback budget; defaults to the existing production deadline.</param>
    /// <param name="timeProvider">Optional deterministic UTC clock for session expiry and cooldown presentation.</param>
    /// <param name="liveConfiguration">Optional live application configuration; production supplies it so a removed global admin cannot use stale startup authority.</param>
    /// <param name="configurationSource">Optional authoritative content-root configuration source; absent test constructions report an unavailable source rather than guessing a path.</param>
    /// <remarks>Construction retains trusted configuration metadata only; technical views never load JSON or hot-change startup routing/file mappings.</remarks>
    /// <example><code>await panel.TryHandleAsync(hostingBotId, client, update, cancellationToken);</code></example>
    public TelegramEndpointAdminService(ITelegramEndpointAdministration coordinator, AppConfig configuration,
        BotRegistry registry, TelegramEndpointRoutingOptions options, ILogger<TelegramEndpointAdminService> logger,
        TelegramInteractionTimeouts timeouts = null, TimeProvider timeProvider = null,
        Microsoft.Extensions.Configuration.IConfiguration liveConfiguration = null, ApplicationConfigurationSource configurationSource = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeouts = timeouts ?? TelegramInteractionTimeouts.Production;
        _time = timeProvider ?? TimeProvider.System;
        _liveConfiguration = liveConfiguration;
        _configurationSource = configurationSource;
    }

    /// <summary>Consumes endpoint-panel entries or callbacks before stale business flows on a healthy owned host.</summary>
    /// <param name="hostingBotId">Exact internal registry id of the bot receiving this update; never a default fallback id.</param>
    /// <param name="client">Required client belonging to that receiving bot.</param>
    /// <param name="update">Required authenticated Telegram update; the callback's actor and message supply all security bindings.</param>
    /// <param name="token">Receiver/update cancellation token; not retained by queued migrations.</param>
    /// <returns>True when this is a panel update, including rejected attempts; false for unrelated business updates.</returns>
    /// <remarks>Each valid callback consumes the entire displayed session before awaiting work, rejecting replay and concurrent taps.
    /// Migration and bulk progress are rendered before registration, so migrating this host never needs a post-fence ordinary send.
    /// Bulk confirmations freeze the entire configured inventory; reports are actor-scoped, process-local, absolutely one-hour bounded and read-only on refresh.</remarks>
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
            var reportNavigation = command.Kind == CommandKind.BulkReport;
            if (host.TelegramBotId != session.HostIdentity || (!reportNavigation &&
                (current.TelegramBotId != command.Identity || current.ControlRevision != command.Revision)))
            {
                await AckAsync(client, callback, hostingBotId, "وضعیت یا هویت ربات تغییر کرده است؛ پنل تازه نمایش داده می‌شود.", token);
                var freshView = command.BotId != null && host.TelegramBotId == session.HostIdentity &&
                    current.TelegramBotId == command.Identity
                    ? command with { Kind = command.Kind == CommandKind.Technical ? CommandKind.Technical : CommandKind.Detail, Revision = current.ControlRevision, Receipt = null }
                    : new PanelCommand(CommandKind.Inventory);
                await RenderAsync(hostingBotId, client, actor, message.Chat.Id, message.Id, freshView, token);
                return true;
            }
            await AckAsync(client, callback, hostingBotId, null, token);
            if (command.Kind == CommandKind.BulkMigrate)
            {
                var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
                var report = new BulkReport(actor, _time.GetUtcNow().Add(BulkReportLifetime), command.Target, command.Targets);
                lock (_sync)
                {
                    PruneSessions();
                    if (_reports.Count >= MaxSessions) _reports.Remove(_reports.MinBy(x => x.Value.Expires).Key);
                    _reports[id] = report;
                }
                // Publish progress and refresh before this host can be fenced. No post-registration send is required.
                await RenderAsync(hostingBotId, client, actor, message.Chat.Id, message.Id,
                    new PanelCommand(CommandKind.BulkReport, Identity: host.TelegramBotId, Revision: host.ControlRevision, BatchId: id), token);
                try
                {
                    var results = await _coordinator.RequestBulkMigrationAsync(hostingBotId, session.HostIdentity,
                        command.Target, actor, command.Targets, token);
                    lock (_sync) { report.Results = results.ToArray(); report.Registering = false; }
                }
                catch (Exception)
                {
                    // An exception can follow a durable commit. Never retry a possibly submitted identity.
                    lock (_sync)
                    {
                        report.Results = command.Targets.Select(x => new TelegramEndpointBulkResult(x.BotId, x.Identity, "registration_uncertain", null)).ToArray();
                        report.Registering = false;
                    }
                    if (token.IsCancellationRequested) throw;
                    _logger.LogWarning("Telegram endpoint bulk registration outcome could not be read; requests will not be replayed.");
                }
                return true;
            }
            if (command.Kind == CommandKind.Migrate)
            {
                // The mutable bounded receipt is already bound to refresh/technical controls before possible host fencing.
                var receipt = new ControlReceipt(command.Identity, command.Revision, _time.GetUtcNow().AddMinutes(10));
                var progress = command with { Kind = CommandKind.Detail, Receipt = receipt };
                await RenderAsync(hostingBotId, client, actor, message.Chat.Id, message.Id, progress, token,
                    "⏳ در حال ثبت درخواست انتقال؛ هنوز پذیرش یا تکمیل اثبات نشده است. نتیجه را با تازه‌سازی یا در ربات اصلی سالم دیگری ببینید.");
                string result;
                try
                {
                    result = await _coordinator.RequestMigrationAsync(command.BotId, command.Target, actor, command.Revision, command.Identity, token);
                    lock (_sync) receipt.Category = result;
                }
                catch
                {
                    lock (_sync) receipt.Category = "registration_uncertain";
                    throw;
                }
                if (result is not ("accepted" or "registration_uncertain"))
                    await RenderAsync(hostingBotId, client, actor, message.Chat.Id, message.Id, progress, token,
                        result == "stale" ? ResultLabel(result) : null);
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
    /// <remarks>Main screens contain only connection, migration outcome and fixed Persian action. Technical screens are read-only fresh snapshots, paginated without dropping diagnostic content. Registration receipts expire independently and remain identity/control-revision bound.</remarks>
    private async Task RenderAsync(string host, ITelegramBotClient client, long actor, long chat, int messageId,
        PanelCommand view, CancellationToken token, string notice = null)
    {
        var hostState = await _coordinator.GetStatusAsync(host, token);
        var commands = new List<PanelCommand>();
        var rows = new List<InlineKeyboardButton[]>();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var text = new StringBuilder();
        if (!string.IsNullOrEmpty(notice)) text.AppendLine(notice);
        if (view.Kind == CommandKind.BulkConfirm)
        {
            var inventory = await _coordinator.GetInventoryAsync(token);
            var targets = inventory.Select(x => new TelegramEndpointBulkTarget(x.BotId, x.TelegramBotId, x.ControlRevision)).ToArray();
            text.AppendLine($"⚠️ انتقال دسته‌ای {targets.Length} ربات به {EndpointLabel(view.Target)} را تأیید می‌کنید؟");
            text.AppendLine("تمام فهرست (همه صفحه‌ها)، هویت و نسخه کنترل اکنون ثابت شده‌اند؛ ربات جدید/جایگزین وارد این درخواست نمی‌شود.");
            text.AppendLine(view.Target == TelegramEndpointType.Local
                ? "🏠 یک ربات Owned فعال و مستقل روی Cloud برای مسیر مدیریت باقی می‌ماند (ترجیحاً همین میزبان). قانون آخرین مسیر مستقل Cloud دور زده نمی‌شود؛ بعضی درخواست‌ها ممکن است رد شوند."
                : "☁️ همه ربات‌های این فهرست، از جمله میزبان، بررسی می‌شوند. پاک‌سازی و مهلت رسمی Cloud می‌تواند تکمیل را به تأخیر بیندازد.");
            text.AppendLine("ثبت درخواست ≠ تکمیل انتقال. دریافت پیام میزبان ممکن است متوقف شود؛ گزارش را با تازه‌سازی یا /telegram_api در ربات Owned سالم دیگری ببینید.");
            AddButton(rows, commands, nonce, "✅ تأیید انتقال دسته‌ای به " + EndpointLabel(view.Target),
                view with { Kind = CommandKind.BulkMigrate, Identity = hostState.TelegramBotId, Revision = hostState.ControlRevision, Targets = targets });
            AddButton(rows, commands, nonce, "❌ انصراف", new PanelCommand(CommandKind.Inventory, Identity: hostState.TelegramBotId, Revision: hostState.ControlRevision));
        }
        else if (view.Kind == CommandKind.BulkReport)
            await AppendBulkReportAsync(text, rows, commands, nonce, actor, hostState, view, token);
        else if (view.Kind == CommandKind.Inventory)
        {
            var inventory = await _coordinator.GetInventoryAsync(token);
            var page = Math.Clamp(view.Page, 0, Math.Max(0, (inventory.Count - 1) / PageSize));
            text.AppendLine($"فهرست ربات‌ها — صفحه {page + 1}/{Math.Max(1, (inventory.Count + PageSize - 1) / PageSize)}");
            text.Insert(0, ActiveBadge(hostState) + "\n" + Action + "\n");
            foreach (var state in inventory.Skip(page * PageSize).Take(PageSize))
            {
                text.AppendLine($"{ActiveBadge(state)} | {BotLabel(state.BotId)}");
                var outcomeHistory = state.TelegramBotId > 0 ? await _coordinator.GetHistoryAsync(state.BotId, token) : Array.Empty<TelegramEndpointHistory>();
                AppendConnection(text, state, outcomeHistory);
                text.AppendLine(TelegramEndpointPresentation.MainOutcomeLabel(state, outcomeHistory));
                AppendDiagnostic(text, state.LastFailureCategory, state, technical: false);
                AddButton(rows, commands, nonce, "جزئیات «" + BotLabel(state.BotId) + "»", new PanelCommand(CommandKind.Detail, state.BotId, state.TelegramBotId, state.ControlRevision, page));
            }
            if (page > 0) AddButton(rows, commands, nonce, "◀️ صفحه قبل", new PanelCommand(CommandKind.Inventory, Identity: hostState.TelegramBotId, Revision: hostState.ControlRevision, Page: page - 1));
            if ((page + 1) * PageSize < inventory.Count) AddButton(rows, commands, nonce, "صفحه بعد ▶️", new PanelCommand(CommandKind.Inventory, Identity: hostState.TelegramBotId, Revision: hostState.ControlRevision, Page: page + 1));
            AddButton(rows, commands, nonce, "🔄 تازه‌سازی فهرست", new PanelCommand(CommandKind.Inventory, Identity: hostState.TelegramBotId, Revision: hostState.ControlRevision, Page: page));
            AddButton(rows, commands, nonce, "🏠 انتقال همه ربات‌ها به Local…", new PanelCommand(CommandKind.BulkConfirm, Identity: hostState.TelegramBotId, Revision: hostState.ControlRevision, Target: TelegramEndpointType.Local));
            AddButton(rows, commands, nonce, "☁️ انتقال همه ربات‌ها به Cloud…", new PanelCommand(CommandKind.BulkConfirm, Identity: hostState.TelegramBotId, Revision: hostState.ControlRevision));
            KeyValuePair<string, BulkReport>[] reports;
            lock (_sync) { PruneSessions(); reports = _reports.Where(x => x.Value.Actor == actor).OrderByDescending(x => x.Value.Expires).Take(3).ToArray(); }
            foreach (var report in reports)
                AddButton(rows, commands, nonce, "📊 گزارش دسته‌ای " + EndpointLabel(report.Value.Target) + " — " + report.Value.Expires.Subtract(BulkReportLifetime).ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    new PanelCommand(CommandKind.BulkReport, Identity: hostState.TelegramBotId, BatchId: report.Key));
        }
        else
        {
            var state = await _coordinator.GetStatusAsync(view.BotId, token);
            var detail = view with { Kind = CommandKind.Detail, Identity = state.TelegramBotId, Revision = state.ControlRevision };
            var history = state.TelegramBotId > 0 ? await _coordinator.GetHistoryAsync(state.BotId, token) : Array.Empty<TelegramEndpointHistory>();
            var receiptCategory = ReceiptCategory(view.Receipt, state);
            if (view.Kind == CommandKind.Technical)
            {
                text.AppendLine("🔍 جزئیات فنی — فقط خواندنی");
                text.AppendLine($"ربات: {BotLabel(state.BotId)} — شناسه Telegram: {state.TelegramBotId}");
                if (view.BatchId != null)
                {
                    lock (_sync)
                    {
                        if (_reports.TryGetValue(view.BatchId, out var report) && report.Actor == actor)
                        {
                            var frozen = report.Targets.FirstOrDefault(x => x.BotId == state.BotId);
                            text.AppendLine($"گزارش دسته‌ای — هویت ثابت: {frozen?.Identity} | مقصد: {RouteName(report.Target)} | انقضا UTC: {report.Expires:yyyy-MM-dd HH:mm:ss 'UTC'}");
                        }
                    }
                }
                text.AppendLine(ActiveBadge(state));
                text.AppendLine(TelegramEndpointPresentation.OutcomeLabel(state, history));
                if (receiptCategory != null) text.AppendLine("نتیجه ثبت همین درخواست: " + ResultLabel(receiptCategory));
                AppendDiagnostic(text, receiptCategory, state, technical: true);
                if (!string.IsNullOrEmpty(state.LastFailureCategory)) text.AppendLine("خطای وضعیت فعلی:");
                AppendDiagnostic(text, state.LastFailureCategory, state, technical: true);
                if (state.MigrationState == TelegramEndpointMigrationState.CloudWait)
                    AppendDiagnostic(text, "cloud_cooldown", state, technical: true);
                await AppendTechnicalAsync(text, state, history, token);
                var pages = TechnicalPages(text.ToString());
                var technicalPage = Math.Clamp(view.TechnicalPage, 0, pages.Count - 1);
                text.Clear().AppendLine($"🔍 جزئیات فنی — صفحه {technicalPage + 1}/{pages.Count}").Append(pages[technicalPage]);
                var technical = detail with { Kind = CommandKind.Technical, TechnicalPage = technicalPage };
                AddButton(rows, commands, nonce, "🔄 تازه‌سازی جزئیات فنی", technical);
                if (technicalPage > 0) AddButton(rows, commands, nonce, "◀️ بخش فنی قبل", technical with { TechnicalPage = technicalPage - 1 });
                if (technicalPage + 1 < pages.Count) AddButton(rows, commands, nonce, "بخش فنی بعد ▶️", technical with { TechnicalPage = technicalPage + 1 });
                AddButton(rows, commands, nonce, "↩️ بازگشت به وضعیت ربات", detail);
                if (view.BatchId != null) AddButton(rows, commands, nonce, "📊 بازگشت به گزارش", new PanelCommand(CommandKind.BulkReport, Identity: hostState.TelegramBotId, BatchId: view.BatchId, Page: view.Page));
            }
            else
            {
                text.Insert(0, ActiveBadge(state) + "\n" + Action + "\n");
                text.AppendLine($"ربات: {BotLabel(state.BotId)}");
                AppendConnection(text, state, history);
                // A pre-registration screen must not reuse a previous operation's success as the new request's result.
                text.AppendLine(view.Receipt != null && notice != null
                    ? "⏳ نتیجه انتقال: درخواست جدید هنوز پذیرفته نشده؛ نتیجه را تازه‌سازی کنید."
                    : TelegramEndpointPresentation.MainOutcomeLabel(state, history));
                if (receiptCategory != null) text.AppendLine("ثبت درخواست: " + ResultLabel(receiptCategory));
                AppendDiagnostic(text, receiptCategory, state, technical: false);
                AppendDiagnostic(text, state.LastFailureCategory, state, technical: false);
                if (view.Kind == CommandKind.Confirm)
                    text.AppendLine($"⚠️ انتقال به {EndpointLabel(view.Target)} را تأیید می‌کنید؟ دریافت پیام این ربات موقتاً متوقف می‌شود؛ خروج اختصاصی ربات و مهلت رسمی CLOUD لازم است.");
                if (state.TelegramBotId <= 0)
                    AddButton(rows, commands, nonce, "🔄 تازه‌سازی وضعیت", detail);
                else if (view.Kind == CommandKind.Confirm)
                {
                    AddButton(rows, commands, nonce, "✅ تأیید انتقال به " + EndpointLabel(view.Target), detail with { Kind = CommandKind.Migrate, Target = view.Target });
                    AddButton(rows, commands, nonce, "❌ انصراف", detail);
                }
                else
                {
                    AddButton(rows, commands, nonce, "🔄 تازه‌سازی وضعیت و سلامت", detail with { Kind = CommandKind.Refresh });
                    AddButton(rows, commands, nonce, "انتقال به Cloud…", detail with { Kind = CommandKind.Confirm, Target = TelegramEndpointType.Cloud, Receipt = null });
                    AddButton(rows, commands, nonce, "انتقال به Local…", detail with { Kind = CommandKind.Confirm, Target = TelegramEndpointType.Local, Receipt = null });
                    AddButton(rows, commands, nonce, state.AutoFailoverEnabled ? "خاموش‌کردن بازگشت اضطراری" : "روشن‌کردن بازگشت اضطراری", detail with { Kind = CommandKind.Failover, Enabled = !state.AutoFailoverEnabled });
                }
                AddButton(rows, commands, nonce, "🔍 جزئیات فنی", detail with { Kind = CommandKind.Technical, TechnicalPage = 0 });
            }
            AddButton(rows, commands, nonce, "📋 بازگشت به فهرست", new PanelCommand(CommandKind.Inventory, Identity: hostState.TelegramBotId, Revision: hostState.ControlRevision, Page: view.Page));
        }
        // Logger/configuration/history belong exclusively to the authenticated technical view.
        var body = text.ToString();
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

    /// <summary>Adds progress with historical source separated from current admission.</summary>
    /// <param name="text">Operational output body.</param>
    /// <param name="state">Fresh current identity and route.</param>
    /// <param name="history">Current identity operation receipts.</param>
    /// <remarks>A historical source is never described as active when admission is paused.</remarks>
    private void AppendConnection(StringBuilder text, TelegramEndpointState state, IReadOnlyList<TelegramEndpointHistory> history)
    {
        if (TelegramEndpointPresentation.Outcome(state, history) != "pending") return;
        text.AppendLine("⏳ در حال انتقال");
        var source = history.FirstOrDefault(x => x.OperationId == state.OperationId && x.Reason == "migration_requested")?.FromEffectiveEndpoint ?? state.LogoutEndpoint;
        text.AppendLine($"مبدأ انتقال: {(source.HasValue ? RouteName(source.Value) : "نامشخص")} → مقصد: {RouteName(TelegramEndpointPresentation.Target(state))}");
        text.AppendLine("اتصال فعلی: " + ActiveBadge(state));
    }

    /// <summary>Reads a receipt only while its original identity, revision and expiry match.</summary>
    /// <param name="receipt">Optional session-held closed result receipt.</param>
    /// <param name="state">Fresh configured identity and revision.</param>
    /// <returns>Retained category, or null after expiry/replacement.</returns>
    /// <remarks>Locking publishes the result written after pre-fence UI delivery; no request is registered by this read.</remarks>
    private string ReceiptCategory(ControlReceipt receipt, TelegramEndpointState state)
    {
        lock (_sync)
            return receipt != null && receipt.Identity == state.TelegramBotId && receipt.Revision == state.ControlRevision &&
                receipt.Expires > _time.GetUtcNow() ? receipt.Category : null;
    }

    /// <summary>Renders fixed catalog messages/actions; codes and stages appear only on technical screens.</summary>
    /// <param name="text">Output body.</param>
    /// <param name="category">Closed backend category, never exception text.</param>
    /// <param name="state">Fresh identity snapshot for stage-aware interpretation.</param>
    /// <param name="technical">True includes code, stage and checked prerequisite.</param>
    /// <remarks>Unrecognized values use the catalog fallback and are never echoed.</remarks>
    private static void AppendDiagnostic(StringBuilder text, string category, TelegramEndpointState state, bool technical)
    {
        if (category is null or "" or "none") return;
        var diagnostic = TelegramEndpointDiagnosticCatalog.Describe(category, state);
        if (diagnostic == null) return;
        text.AppendLine("⚠️ " + diagnostic.Message);
        text.AppendLine("اقدام: " + diagnostic.Action);
        if (!technical) return;
        text.AppendLine("کد: " + diagnostic.Code + " | مرحله: " + diagnostic.Stage);
        text.AppendLine("بررسی‌شده: " + diagnostic.Checked);
    }

    /// <summary>Collects secret-free technical state, mapping provenance and history without probes or mutations.</summary>
    /// <param name="text">Technical body, paginated without truncation.</param>
    /// <param name="state">Current bot's detached snapshot.</param>
    /// <param name="history">Current identity's bounded audit history.</param>
    /// <param name="token">Cancels local metadata reads.</param>
    /// <returns>A task completing after read-only diagnostics are collected.</returns>
    /// <remarks>Mapping remains startup-bound to preserve file epochs; live changes require restart. Closed history failures retain their own diagnostics after later health clears the current failure.</remarks>
    private async Task AppendTechnicalAsync(StringBuilder text, TelegramEndpointState state, IReadOnlyList<TelegramEndpointHistory> history, CancellationToken token)
    {
        text.AppendLine($"Generation: {state.RuntimeGeneration?.ToString(CultureInfo.InvariantCulture) ?? "نامشخص"} | Gate: {state.RuntimeAvailable?.ToString() ?? "نامشخص"} | Runtime endpoint: {(state.RuntimeEndpoint.HasValue ? RouteName(state.RuntimeEndpoint.Value) : "نامشخص")}");
        text.AppendLine($"Migration State: {StateLabel(state.MigrationState)} | ControlRevision: {state.ControlRevision}");
        text.AppendLine($"انتخاب ذخیره‌شده: {RouteName(state.DesiredEndpoint)} | آخرین فعال‌سازی: {RouteName(state.EffectiveEndpoint)}");
        text.AppendLine($"شروع UTC: {Utc(state.MigrationStartedAtUtc)} | فعال‌سازی همین عملیات UTC: {(TelegramEndpointPresentation.Outcome(state, history) == "succeeded" ? Utc(state.LastMigrationAtUtc) : "اثبات نشده")}");
        text.AppendLine($"بررسی ربات UTC: {Utc(state.LastHealthCheckAtUtc)} | سلامت موفق UTC: {Utc(state.LastSuccessfulHealthAtUtc)}");
        text.AppendLine($"آخرین خطا UTC: {Utc(state.LastFailureAtUtc)} | تلاش ایمن بعدی UTC: {Utc(state.NextAttemptAtUtc)}");
        text.AppendLine($"خروج آغازشده UTC: {Utc(state.LogoutAttemptedAtUtc)} | خروج تأییدشده UTC: {Utc(state.LogoutAcknowledgedAtUtc)}");
        var remaining = state.CloudReuseEligibleAtUtc.HasValue ? Math.Max(0, (state.CloudReuseEligibleAtUtc.Value - _time.GetUtcNow().UtcDateTime).TotalSeconds) : 0;
        text.AppendLine($"انتظار مجاز CLOUD: {Math.Ceiling(remaining)} ثانیه | موعد UTC: {Utc(state.CloudReuseEligibleAtUtc)}");
        text.AppendLine($"بازگشت اضطراری: {(state.AutoFailoverEnabled ? "روشن" : "خاموش")} | بازگشت خودکار LOCAL: {(_options.AutomaticFailback ? "روشن" : "خاموش")}");
        text.AppendLine($"مدیریت مسیر: {(_options.Enabled ? "فعال" : "غیرفعال")}");
        text.AppendLine("منبع پیکربندی: " + (_configurationSource?.FilePath ?? "منبع در دسترس نیست"));
        text.AppendLine("نگاشت آغاز اجرا — localFileServerRoot: " + (string.IsNullOrWhiteSpace(_options.LocalFileServerRoot) ? "تنظیم نشده" : _options.LocalFileServerRoot));
        text.AppendLine("نگاشت آغاز اجرا — localFileHostRoot: " + (string.IsNullOrWhiteSpace(_options.LocalFileHostRoot) ? "تنظیم نشده" : _options.LocalFileHostRoot));
        if (_liveConfiguration != null)
        {
            var liveServer = _liveConfiguration["telegramEndpointRouting:localFileServerRoot"];
            var liveHost = _liveConfiguration["telegramEndpointRouting:localFileHostRoot"];
            text.AppendLine("نگاشت پیکربندی فعلی — localFileServerRoot: " + (string.IsNullOrWhiteSpace(liveServer) ? "تنظیم نشده" : liveServer));
            text.AppendLine("نگاشت پیکربندی فعلی — localFileHostRoot: " + (string.IsNullOrWhiteSpace(liveHost) ? "تنظیم نشده" : liveHost));
            if (!string.Equals(liveServer ?? "", _options.LocalFileServerRoot ?? "", StringComparison.Ordinal) ||
                !string.Equals(liveHost ?? "", _options.LocalFileHostRoot ?? "", StringComparison.Ordinal))
                text.AppendLine("⚠️ نگاشت فعلی با آغاز اجرا متفاوت است؛ پس از اصلاح، راه‌اندازی مجدد لازم است. مسیرهای فایل زنده تغییر نمی‌کنند.");
        }
        text.AppendLine(_options.HasLocalFileMapping ? "پیش‌نیاز فایل LOCAL: ریشه‌ها تنظیم شده‌اند؛ خوانایی و مجوز از این نمایش اثبات نمی‌شود." : "پیش‌نیاز فایل LOCAL: نگاشت مطمئن تنظیم نشده است.");
        var health = _coordinator.SharedLocalHealth;
        text.AppendLine($"سرور محلی: {(health.Reachable ? "در دسترس؛ احراز هویت ربات نیست" : "در دسترس نیست/بررسی نشده")} | بررسی UTC: {Utc(health.LastCheckedAtUtc)} | موفق UTC: {Utc(health.LastSuccessAtUtc)}");
        text.AppendLine($"اعلان‌های تحویل‌نشده/نامطمئن: {await _coordinator.GetPendingAlertCountAsync(token)}");
        text.AppendLine(await NotificationLabelAsync(token));
        text.AppendLine("تاریخچه اخیر:");
        foreach (var item in history.Take(6))
        {
            text.AppendLine(HistoryLabel(item));
            if (item.Reason is ("migration_failed" or "migration_admission_failed" or "logout_refused" or "logout_uncertain" or
                "manual_intervention" or "safe_retry_scheduled" or "startup_reconciled") &&
                (!Enum.TryParse<TelegramEndpointMigrationState>(item.Outcome, out var oldState) || !Enum.IsDefined(oldState)))
                AppendDiagnostic(text, item.Outcome, null, technical: true);
        }
        if (history.Count == 0) text.AppendLine("رویدادی ثبت نشده است.");
        if (state.TelegramBotId <= 0) text.AppendLine("هیچ هویتی از سوابق قدیمی حدس زده نمی‌شود.");
    }

    /// <summary>Splits complete technical output into bounded Unicode-safe pages without dropping any diagnostic or path.</summary>
    /// <param name="body">Secret-free technical text, including trusted filesystem paths.</param>
    /// <returns>Nonempty pages at most 3000 UTF-16 code units each.</returns>
    /// <remarks>Splitting preserves surrogate pairs; navigation exposes every part instead of silently omitting history/actions.</remarks>
    private static IReadOnlyList<string> TechnicalPages(string body)
    {
        var pages = new List<string>();
        for (var offset = 0; offset < body.Length;)
        {
            var count = Math.Min(3000, body.Length - offset);
            if (offset + count < body.Length)
            {
                var newline = body.LastIndexOf('\n', offset + count - 1, count);
                if (newline >= offset) count = newline - offset + 1;
                else if (char.IsHighSurrogate(body[offset + count - 1])) count--;
            }
            pages.Add(body.Substring(offset, count));
            offset += count;
        }
        if (pages.Count == 0) pages.Add("");
        return pages;
    }

    /// <summary>Names endpoint kinds without suggesting that a durable endpoint admits requests.</summary>
    /// <param name="endpoint">Closed durable or observed endpoint enum.</param>
    /// <returns>Uppercase CLOUD/LOCAL or a fixed unknown label.</returns>
    private static string RouteName(TelegramEndpointType endpoint) => endpoint switch
    {
        TelegramEndpointType.Cloud => "CLOUD",
        TelegramEndpointType.Local => "LOCAL",
        _ => "نامشخص"
    };
    /// <summary>Formats a bot's runtime admission badge using its current enabled configuration.</summary>
    /// <param name="state">Detached identity-bound observation from the coordinator.</param>
    /// <returns>A badge that never promotes a disabled, missing or fenced route to active.</returns>
    private string ActiveBadge(TelegramEndpointState state)
        => TelegramEndpointPresentation.ActiveBadge(state, _registry.Bots.Any(x => x.Id == state.BotId && x.Enabled));


    /// <summary>Reads a session-local batch report and current execution state without registering any intent again.</summary>
    /// <param name="text">Required bounded output body.</param>
    /// <param name="rows">Output keyboard rows.</param>
    /// <param name="commands">Server-held controls for this rendering.</param>
    /// <param name="nonce">Random message-session nonce.</param>
    /// <param name="actor">Currently authorized global Telegram operator id.</param>
    /// <param name="host">Current exact receiving host state, including its runtime observation.</param>
    /// <param name="view">Report identity and zero-based page selected by a bound callback.</param>
    /// <param name="token">Cancels metadata reads only.</param>
    /// <returns>A task completing with paginated registration and current-operation outcomes plus refresh controls.</returns>
    /// <remarks>Reports expire absolutely after one hour and are lost on restart; callback controls still expire after ten minutes and must be reopened from inventory. Per-bot durable state/history remain accessible after either expiry. Identity/operation replacements are never credited to this batch.
    /// Technical buttons expose each bot's complete diagnostics behind the same one-use, actor/message/host/identity/revision-bound callback security.</remarks>
    private async Task AppendBulkReportAsync(StringBuilder text, List<InlineKeyboardButton[]> rows,
        List<PanelCommand> commands, string nonce, long actor, TelegramEndpointState host, PanelCommand view, CancellationToken token)
    {
        BulkReport report;
        IReadOnlyList<TelegramEndpointBulkResult> results = null;
        bool registering = false;
        lock (_sync)
        {
            PruneSessions();
            _reports.TryGetValue(view.BatchId, out report);
            if (report?.Actor == actor) { results = report.Results; registering = report.Registering; }
            else report = null;
        }
        var inventory = new PanelCommand(CommandKind.Inventory, Identity: host.TelegramBotId, Revision: host.ControlRevision);
        if (report == null)
        {
            text.AppendLine("گزارش دسته‌ای منقضی/ناموجود است؛ انتقال را تکرار نکنید. وضعیت پایدار هر ربات را از فهرست بخوانید.");
            AddButton(rows, commands, nonce, "📋 بازگشت به فهرست", inventory);
            return;
        }
        var states = await _coordinator.GetInventoryAsync(token);
        var histories = new Dictionary<string, IReadOnlyList<TelegramEndpointHistory>>(StringComparer.Ordinal);
        foreach (var state in states.Where(x => x.TelegramBotId > 0 && !string.IsNullOrEmpty(x.OperationId) &&
            results.Any(r => r.BotId == x.BotId && r.Identity == x.TelegramBotId && r.ResultCode is "accepted" or "unchanged")))
            histories[state.BotId] = await _coordinator.GetHistoryAsync(state.BotId, token);
        var observations = report.Targets.Select(target =>
        {
            var result = results.FirstOrDefault(x => x.BotId == target.BotId && x.Identity == target.Identity);
            var state = states.FirstOrDefault(x => x.BotId == target.BotId);
            var history = state != null && histories.TryGetValue(state.BotId, out var receipts) ? receipts : Array.Empty<TelegramEndpointHistory>();
            var outcome = BatchOutcome(target, result, state, history);
            return (Target: target, Result: result, State: state, Outcome: outcome, History: history);
        }).ToArray();
        var accepted = observations.Count(x => x.Result?.ResultCode == "accepted");
        var pending = observations.Count(x => x.Outcome == "pending");
        var succeeded = observations.Count(x => x.Outcome == "succeeded");
        var refused = observations.Count(x => x.Outcome is "refused" or "failed" || (x.Result != null &&
            x.Result.ResultCode is not ("accepted" or "unchanged" or "retained_cloud_control" or "not_submitted" or "registration_uncertain")));
        text.AppendLine($"📊 گزارش انتقال دسته‌ای به {EndpointLabel(report.Target)}");
        text.AppendLine($"ثبت پذیرفته: {accepted} | در انتظار اجرا: {pending} | انتقال موفق: {succeeded} | رد/ناموفق: {refused}");
        text.AppendLine($"Cloud نگه‌داشته: {observations.Count(x => x.Result?.ResultCode == "retained_cloud_control")} | ثبت نامطمئن: {observations.Count(x => x.Result?.ResultCode == "registration_uncertain")} | نتیجه تغییرکرده/اثبات‌نشده: {observations.Count(x => x.Outcome is "changed" or "unproven")}");
        text.AppendLine($"اجرای نامطمئن/بررسی دستی: {observations.Count(x => x.Outcome == "uncertain")} | ثبت‌نشده: {observations.Count(x => x.Result?.ResultCode == "not_submitted")}");
        text.AppendLine($"از قبل روی مقصد: {observations.Count(x => x.Outcome == "already_active")}؛ برای این ربات‌ها انتقال جدیدی انجام نشد.");
        text.AppendLine(registering ? "⏳ ثبت درخواست‌ها در جریان است؛ نتیجه هر ربات هنوز قطعی نیست." : "ثبت درخواست‌ها پایان یافته؛ پذیرش به معنی تکمیل نیست. فقط تازه‌سازی، بدون ثبت مجدد.");
        text.AppendLine("اگر میزبان بسته شود، ارسال به‌روزرسانی نهایی تضمین نمی‌شود؛ /telegram_api را در ربات Owned سالم دیگری باز کنید.");
        if (report.Target == TelegramEndpointType.Local)
            text.AppendLine(observations.Any(x => x.Result?.ResultCode == "retained_cloud_control")
                ? "🏠 مسیر مستقل مدیریت Cloud حفظ شد؛ ربات مشخص‌شده روی Cloud نگه داشته شده و انتقال Local برای آن ثبت نشد."
                : "🏠 قانون آخرین مسیر مستقل Cloud برقرار است؛ نبود کنترل واجدشرایط می‌تواند درخواست‌های Local را رد کند.");
        const int reportPageSize = 3;
        var page = Math.Clamp(view.Page, 0, Math.Max(0, (observations.Length - 1) / reportPageSize));
        text.AppendLine($"صفحه گزارش {page + 1}/{Math.Max(1, (observations.Length + reportPageSize - 1) / reportPageSize)}");
        foreach (var item in observations.Skip(page * reportPageSize).Take(reportPageSize))
        {
            text.AppendLine($"ربات: {BotLabel(item.Target.BotId)}");
            text.AppendLine("ثبت: " + (registering && item.Result == null ? "در جریان؛ هنوز نتیجه ثبت دریافت نشده" : ResultLabel(item.Result?.ResultCode ?? "not_submitted")));
            text.AppendLine("اجرای همین درخواست: " + (item.Outcome switch
            {
                "changed" => "هویت/عملیات تغییر کرده یا درخواست دیگری جایگزین شده؛ نتیجه فعلی به این دسته نسبت داده نمی‌شود.",
                "not_registered" => "درخواست قابل‌انتساب ثبت نشده؛ نتیجه ثبت بالا را بررسی کنید.",
                "already_active" => "مقصد هنگام ثبت درخواست از قبل برقرار بود؛ انتقال جدیدی برای این دسته انجام نشد. نشان مسیر فعلی را جداگانه بررسی کنید.",
                _ => TelegramEndpointPresentation.OutcomeLabel(item.State, item.History)
            }));
            if (item.State != null)
            {
                text.AppendLine(ActiveBadge(item.State));
                AppendConnection(text, item.State, item.History);
                var receipt = new ControlReceipt(item.Target.Identity, item.State.ControlRevision, report.Expires) { Category = item.Result?.ResultCode };
                AddButton(rows, commands, nonce, "🔍 جزئیات فنی «" + BotLabel(item.Target.BotId) + "»",
                    new PanelCommand(CommandKind.Technical, item.State.BotId, item.State.TelegramBotId, item.State.ControlRevision, page, BatchId: view.BatchId, Receipt: receipt));
            }
        }
        var refresh = view with { Identity = host.TelegramBotId, Revision = host.ControlRevision, Page = page };
        AddButton(rows, commands, nonce, "🔄 تازه‌سازی گزارش (بدون ثبت مجدد)", refresh);
        if (page > 0) AddButton(rows, commands, nonce, "◀️ صفحه قبل گزارش", refresh with { Page = page - 1 });
        if ((page + 1) * reportPageSize < observations.Length) AddButton(rows, commands, nonce, "صفحه بعد گزارش ▶️", refresh with { Page = page + 1 });
        AddButton(rows, commands, nonce, "📋 بازگشت به فهرست", inventory);
    }

    /// <summary>Associates execution results only with the frozen identity and exactly returned committed operation.</summary>
    /// <param name="target">Frozen configured identity and control revision.</param>
    /// <param name="result">Registration result, null while registration is still in progress.</param>
    /// <param name="state">Current detached state, null when configuration was removed.</param>
    /// <param name="history">Current identity's bounded durable activation receipts.</param>
    /// <returns>A closed operation outcome, already_active, changed, or not_registered; acceptance and old unchanged-operation receipts never imply new batch success.</returns>
    private static string BatchOutcome(TelegramEndpointBulkTarget target, TelegramEndpointBulkResult result, TelegramEndpointState state,
        IReadOnlyList<TelegramEndpointHistory> history)
    {
        if (state == null || state.TelegramBotId != target.Identity) return "changed";
        if (result?.ResultCode == "unchanged")
            return state.OperationId == result.OperationId ? "already_active" : "changed";
        if (result?.ResultCode != "accepted" || string.IsNullOrEmpty(result.OperationId)) return "not_registered";
        if (state.OperationId != result.OperationId) return "changed";
        return TelegramEndpointPresentation.Outcome(state, history);
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
    /// <remarks>Callback expiry is strict at ten minutes; read-only reports have a separate absolute one-hour lifetime so official Cloud cooldown cannot erase a report before completion.</remarks>
    private void PruneSessions()
    {
        var now = _time.GetUtcNow();
        foreach (var key in _sessions.Where(x => x.Value.Expires <= now).Select(x => x.Key).ToArray()) _sessions.Remove(key);
        foreach (var key in _reports.Where(x => x.Value.Expires <= now).Select(x => x.Key).ToArray()) _reports.Remove(key);
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
    /// <remarks>These labels describe durable protocol state, never runtime admission; the separate first-line badge alone describes gate availability.</remarks>
    private static string StateLabel(TelegramEndpointMigrationState value) => value switch
    {
        TelegramEndpointMigrationState.Cloud => "پروتکل پایدار Cloud",
        TelegramEndpointMigrationState.CheckingLocal => "بررسی پیش‌نیاز Local",
        TelegramEndpointMigrationState.CloudLogoutPending => "خروج Cloud در انتظار",
        TelegramEndpointMigrationState.CloudLogoutUncertain => "خروج Cloud نامطمئن؛ بررسی دستی",
        TelegramEndpointMigrationState.SwitchingToLocal => "در حال فعال‌سازی Local",
        TelegramEndpointMigrationState.Local => "پروتکل پایدار Local",
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
    /// <remarks>New validation refusal categories use the shared fixed catalog; no raw backend strings appear in either single or bulk summaries.</remarks>
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
        "retained_cloud_control" => "☁️ روی Cloud نگه داشته شد؛ مسیر مستقل مدیریت محفوظ است و انتقال Local ثبت نشد.",
        "not_submitted" => "ثبت نشد؛ ادامه دسته پس از توقف/لغو ثبت نشده است.",
        "registration_uncertain" => "ثبت نامطمئن؛ ممکن است درخواست پایدار شده باشد. تکرار نکنید؛ وضعیت هر ربات را بررسی کنید.",
        "batch_busy" => "دسته دیگری در حال ثبت است؛ این درخواست ثبت نشد.",
        "local_file_mapping_missing" or "local_file_mapping_invalid" or "local_file_host_missing" or "local_file_access_denied" or "local_file_path_linked" or "local_file_probe_failed"
            => "اعتبارسنجی رد شد؛ هیچ درخواست انتقالی ثبت نشد. " + TelegramEndpointDiagnosticCatalog.Describe(result).Message,
        "migration_in_progress" => "انتقال دیگری در جریان است؛ درخواست جدید ثبت نشد. تازه‌سازی کنید.",
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
        "migration_admission_failed" => "اعتبارسنجی رد شد؛ درخواست جدید ثبت نشد",
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
    /// <param name="outcome">Persisted protocol enum name or closed failure category, never raw provider text.</param>
    /// <returns>The fixed protocol or catalog failure explanation; unknown content is not echoed.</returns>
    /// <remarks>Historical failure stage is rendered from that receipt separately, never inferred from the latest bot state.</remarks>
    private static string HistoryOutcomeLabel(string outcome)
        => Enum.TryParse<TelegramEndpointMigrationState>(outcome, out var state) && Enum.IsDefined(state)
            ? StateLabel(state) : TelegramEndpointDiagnosticCatalog.Describe(outcome)?.Message ?? "نتیجه ثبت‌شده نامشخص؛ وضعیت فعلی را بررسی کنید";


    /// <summary>Server-side control kinds; users submit only a nonce and an index, never operation parameters.</summary>
    private enum CommandKind
    {
        /// <summary>Reads the current full inventory through bounded pages.</summary>
        Inventory,
        /// <summary>Reads one current configured identity and its durable history.</summary>
        Detail,
        /// <summary>Reads complete diagnostics without probes, preference changes or migration admission.</summary>
        Technical,
        /// <summary>Displays a single-bot confirmation without mutation.</summary>
        Confirm,
        /// <summary>Consumes one confirmation and queues one durable individual intent.</summary>
        Migrate,
        /// <summary>Sets an explicit automatic-fallback preference.</summary>
        Failover,
        /// <summary>Refreshes safe health observations without rerunning migration.</summary>
        Refresh,
        /// <summary>Freezes all configured identities and displays the bulk safety confirmation.</summary>
        BulkConfirm,
        /// <summary>Consumes one frozen confirmation and registers one batch with pre-rendered progress.</summary>
        BulkMigrate,
        /// <summary>Reads only session-local registration results and current durable execution evidence.</summary>
        BulkReport
    }
    /// <summary>Immutable server-held operation bound to the exact identity and optimistic control revision.</summary>
    /// <param name="Kind">Navigation, confirmation or mutation kind.</param>
    /// <param name="BotId">Selected internal registry id, or null for hosting-bot inventory controls.</param>
    /// <param name="Identity">Exact current numeric Telegram bot identity.</param>
    /// <param name="Revision">Expected control revision unaffected by background health-only writes.</param>
    /// <param name="Page">Zero-based inventory page to restore after detail navigation.</param>
    /// <param name="Target">Explicit endpoint enum for confirmed migration only.</param>
    /// <param name="Enabled">Explicit automatic-fallback target preference.</param>
    /// <param name="Targets">Full frozen inventory snapshot used only by single-use bulk confirmation.</param>
    /// <param name="BatchId">Opaque process-local report identity; never a durable migration operation id.</param>
    /// <param name="Receipt">Optional bounded exact-identity/revision registration result retained across read-only navigation.</param>
    /// <param name="TechnicalPage">Zero-based complete diagnostic page, independent of the inventory/report return page.</param>
    private sealed record PanelCommand(CommandKind Kind, string BotId = null, long Identity = 0, long Revision = 0,
        int Page = 0, TelegramEndpointType Target = TelegramEndpointType.Cloud, bool Enabled = false,
        IReadOnlyList<TelegramEndpointBulkTarget> Targets = null, string BatchId = null, ControlReceipt Receipt = null, int TechnicalPage = 0);

    /// <summary>Contains one secret-free registration result bound to the original identity, control revision and absolute expiry.</summary>
    /// <param name="Identity">Exact numeric BotFather identity of the attempted registration.</param>
    /// <param name="Revision">Control revision checked for that registration.</param>
    /// <param name="Expires">Absolute expiry; refreshing the UI never extends retention.</param>
    /// <remarks>Owned by existing bounded sessions; Category is published/read under the panel session lock.</remarks>
    private sealed record ControlReceipt(long Identity, long Revision, DateTimeOffset Expires)
    {
        /// <summary>Closed backend result only; no credentials, external text or exception payloads are retained.</summary>
        public string Category { get; set; }
    }
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

    /// <summary>One actor-scoped one-hour batch report; registration results are published atomically under the session lock.</summary>
    /// <param name="Actor">Authorized Telegram operator owning this report.</param>
    /// <param name="Expires">Absolute UTC expiry, never extended by refresh.</param>
    /// <param name="Target">Explicit requested destination.</param>
    /// <param name="Targets">Entire frozen configured inventory, including missing and disabled identities.</param>
    private sealed record BulkReport(long Actor, DateTimeOffset Expires, TelegramEndpointType Target,
        IReadOnlyList<TelegramEndpointBulkTarget> Targets)
    {
        /// <summary>True until the one API call returns or becomes uncertain; refresh never invokes that call.</summary>
        public bool Registering { get; set; } = true;
        /// <summary>Exact per-target API registration outcomes, initially empty while registration is in progress.</summary>
        public IReadOnlyList<TelegramEndpointBulkResult> Results { get; set; } = Array.Empty<TelegramEndpointBulkResult>();
    }
}
