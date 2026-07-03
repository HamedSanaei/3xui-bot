using Adminbot.Domain;
using Adminbot.Domain.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System.Net;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

/// <summary>
/// Central in-memory registry of all Telegram bots known to the process.
/// It starts with configured owned bots and can be hydrated or updated with tenant bots from users.db.
/// </summary>
public class BotRegistry
{
    private readonly Dictionary<string, BotInstanceConfig> _bots = new(StringComparer.OrdinalIgnoreCase);
    private BotInstanceConfig _defaultBot;
    private readonly object _syncRoot = new();

    /// <summary>
    /// Creates the registry from AppConfig.Bots and determines the default bot.
    /// </summary>
    /// <param name="configuration">Application configuration loaded from configuration.json.</param>
    public BotRegistry(IConfiguration configuration)
    {
        var appConfig = configuration.Get<AppConfig>() ?? new AppConfig();
        foreach (var bot in BuildBots(appConfig))
            _bots[bot.Id] = bot;

        _defaultBot = _bots.Values.FirstOrDefault(b => b.IsDefault) ?? _bots.Values.FirstOrDefault();
    }

    /// <summary>
    /// Snapshot of all known bot configurations.
    /// </summary>
    public IReadOnlyList<BotInstanceConfig> Bots
    {
        get
        {
            lock (_syncRoot)
                return _bots.Values.ToList();
        }
    }

    public BotInstanceConfig DefaultBot => _defaultBot;

    /// <summary>
    /// Looks up a bot by internal BotId.
    /// </summary>
    /// <param name="botId">Internal BotId, for example vpnetiranbot or tenant-123.</param>
    /// <returns>The matching bot configuration, or the default bot if the id is empty or unknown.</returns>
    public BotInstanceConfig GetById(string botId)
    {
        if (string.IsNullOrWhiteSpace(botId))
            return DefaultBot;

        lock (_syncRoot)
            return _bots.TryGetValue(botId, out var bot) ? bot : DefaultBot;
    }

    /// <summary>
    /// Loads tenant bots persisted in users.db into the runtime registry.
    /// </summary>
    /// <param name="userDbContext">User database context containing BotInstances.</param>
    /// <param name="cancellationToken">Cancellation token for the database query.</param>
    /// <returns>A task that completes after all tenant bots are registered in memory.</returns>
    public async Task LoadTenantBotsFromDatabaseAsync(UserDbContext userDbContext, CancellationToken cancellationToken = default)
    {
        // Config owns the main brand bots; users.db owns colleague tenant bots created at runtime.
        var tenants = await userDbContext.BotInstances
            .Where(x => x.Type == BotInstanceTypes.Tenant)
            .ToListAsync(cancellationToken);

        lock (_syncRoot)
        {
            foreach (var tenant in tenants)
                _bots[tenant.Id] = ToConfig(tenant);
        }
    }

    /// <summary>
    /// Adds or updates a bot instance in the runtime registry after an owner edits tenant settings.
    /// </summary>
    /// <param name="instance">Persisted bot instance to convert into runtime configuration.</param>
    public void Upsert(BotInstance instance)
    {
        if (instance == null || string.IsNullOrWhiteSpace(instance.Id))
            return;

        lock (_syncRoot)
            _bots[instance.Id] = ToConfig(instance);
    }

    /// <summary>
    /// Converts the persisted BotInstance row to the runtime BotInstanceConfig shape.
    /// </summary>
    /// <param name="bot">Persisted bot row from users.db.</param>
    /// <returns>Runtime configuration used by bot client and dispatch code.</returns>
    private static BotInstanceConfig ToConfig(BotInstance bot)
    {
        return new BotInstanceConfig
        {
            Id = bot.Id,
            Username = bot.Username,
            Token = bot.Token,
            BrandName = string.IsNullOrWhiteSpace(bot.BrandName) ? bot.Username : bot.BrandName,
            ChannelIds = DeserializeStringList(bot.ChannelIdsJson),
            SupportAccount = bot.SupportAccount,
            LoggerChannel = bot.LoggerChannel,
            BackupChannel = bot.BackupChannel,
            IosTutorial = DeserializeStringList(bot.IosTutorialJson).ToArray(),
            AndroidTutorial = DeserializeStringList(bot.AndroidTutorialJson).ToArray(),
            WindowsTutorial = DeserializeStringList(bot.WindowsTutorialJson).ToArray(),
            Type = string.IsNullOrWhiteSpace(bot.Type) ? BotInstanceTypes.Owned : bot.Type,
            IsDefault = bot.IsDefault,
            Enabled = bot.Enabled,
            OwnerTelegramUserId = bot.OwnerTelegramUserId,
            TenantPriceMarkupPercent = bot.TenantPriceMarkupPercent,
            TenantWelcomeText = bot.TenantWelcomeText,
            TenantMandatoryJoinEnabled = bot.TenantMandatoryJoinEnabled,
            TenantChannelIds = DeserializeStringList(bot.TenantChannelIdsJson),
            TenantCardPaymentEnabled = bot.TenantCardPaymentEnabled,
            TenantCardNumber = bot.TenantCardNumber,
            TenantCardHolderName = bot.TenantCardHolderName,
            TenantHooshPayEnabled = bot.TenantHooshPayEnabled,
            TenantNowPaymentsEnabled = bot.TenantNowPaymentsEnabled,
            TenantTutorialsJson = bot.TenantTutorialsJson
        };
    }

    /// <summary>
    /// Safely deserializes JSON string arrays stored on BotInstance.
    /// </summary>
    /// <param name="json">JSON array string or null.</param>
    /// <returns>Deserialized list; empty list on null or invalid JSON.</returns>
    private static List<string> DeserializeStringList(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<string>();

        try
        {
            return JsonConvert.DeserializeObject<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Builds the owned bot list from configuration, falling back to the legacy single-bot fields if needed.
    /// </summary>
    /// <param name="appConfig">Application configuration.</param>
    /// <returns>Normalized owned bot configurations.</returns>
    private static IReadOnlyList<BotInstanceConfig> BuildBots(AppConfig appConfig)
    {
        var configured = appConfig.Bots?
            .Where(b => b != null)
            .Select(b => NormalizeBot(b, appConfig))
            .ToList() ?? new List<BotInstanceConfig>();

        if (configured.Count == 0)
        {
            configured.Add(NormalizeBot(new BotInstanceConfig
            {
                Id = BotContextAccessor.DefaultBotId,
                Username = BotContextAccessor.DefaultBotId,
                Token = appConfig.BotToken,
                BrandName = "VpnetIran",
                IsDefault = true
            }, appConfig));
        }

        if (!configured.Any(b => b.IsDefault))
            configured[0].IsDefault = true;

        if (appConfig.SalesAssistantBot != null &&
            appConfig.SalesAssistantBot.Enabled &&
            !string.IsNullOrWhiteSpace(appConfig.SalesAssistantBot.Token))
        {
            var assistant = NormalizeBot(appConfig.SalesAssistantBot, appConfig);
            assistant.Type = BotInstanceTypes.SalesAssistant;
            assistant.IsSalesAssistant = true;
            assistant.IsDefault = false;
            if (string.IsNullOrWhiteSpace(assistant.Id))
                assistant.Id = "sales-assistant";
            configured.Add(assistant);
        }

        return configured;
    }

    /// <summary>
    /// Applies fallback values and normalizes BotId and username for one configured bot.
    /// </summary>
    /// <param name="bot">Raw bot config item from configuration.json.</param>
    /// <param name="fallback">App-level fallback config.</param>
    /// <returns>A complete runtime bot configuration.</returns>
    private static BotInstanceConfig NormalizeBot(BotInstanceConfig bot, AppConfig fallback)
    {
        var username = string.IsNullOrWhiteSpace(bot.Username)
            ? bot.Id
            : bot.Username.Trim().TrimStart('@');

        if (string.IsNullOrWhiteSpace(username))
            username = BotContextAccessor.DefaultBotId;

        var id = string.IsNullOrWhiteSpace(bot.Id)
            ? username
            : bot.Id.Trim();

        return new BotInstanceConfig
        {
            Id = id,
            Username = username,
            Token = string.IsNullOrWhiteSpace(bot.Token) ? fallback.BotToken : bot.Token,
            BrandName = string.IsNullOrWhiteSpace(bot.BrandName) ? username : bot.BrandName,
            ChannelIds = bot.ChannelIds?.Where(c => !string.IsNullOrWhiteSpace(c)).ToList()
                         ?? fallback.ChannelIds
                         ?? new List<string>(),
            SupportAccount = string.IsNullOrWhiteSpace(bot.SupportAccount) ? fallback.SupportAccount : bot.SupportAccount,
            LoggerChannel = string.IsNullOrWhiteSpace(bot.LoggerChannel) ? fallback.LoggerChannel : bot.LoggerChannel,
            BackupChannel = string.IsNullOrWhiteSpace(bot.BackupChannel) ? fallback.BackupChannel.ToString() : bot.BackupChannel,
            IosTutorial = bot.IosTutorial ?? fallback.IosTutorial,
            AndroidTutorial = bot.AndroidTutorial ?? fallback.AndroidTutorial,
            WindowsTutorial = bot.WindowsTutorial ?? fallback.WindowsTutorial,
            Type = string.IsNullOrWhiteSpace(bot.Type) ? BotInstanceTypes.Owned : bot.Type,
            Enabled = bot.Enabled,
            IsDefault = bot.IsDefault,
            OwnerTelegramUserId = bot.OwnerTelegramUserId,
            TenantPriceMarkupPercent = bot.TenantPriceMarkupPercent,
            TenantWelcomeText = bot.TenantWelcomeText,
            TenantMandatoryJoinEnabled = bot.TenantMandatoryJoinEnabled,
            TenantChannelIds = bot.TenantChannelIds?.Where(c => !string.IsNullOrWhiteSpace(c)).ToList() ?? new List<string>(),
            TenantCardPaymentEnabled = bot.TenantCardPaymentEnabled,
            TenantCardNumber = bot.TenantCardNumber,
            TenantCardHolderName = bot.TenantCardHolderName,
            TenantHooshPayEnabled = bot.TenantHooshPayEnabled,
            TenantNowPaymentsEnabled = bot.TenantNowPaymentsEnabled,
            TenantTutorialsJson = bot.TenantTutorialsJson,
            IsSalesAssistant = bot.IsSalesAssistant
        };
    }
}

/// <summary>
/// Lazily creates and caches TelegramBotClient instances per BotId.
/// </summary>
public class BotClientProvider
{
    private readonly BotRegistry _registry;
    private readonly Dictionary<string, ITelegramBotClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _syncRoot = new();

    /// <summary>
    /// Creates a provider bound to the shared BotRegistry.
    /// </summary>
    /// <param name="registry">Runtime bot registry.</param>
    public BotClientProvider(BotRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>
    /// Gets the Telegram client for the default owned bot.
    /// </summary>
    /// <returns>Telegram client using the default bot token.</returns>
    public ITelegramBotClient GetDefaultClient()
    {
        return GetClient(_registry.DefaultBot?.Id);
    }

    /// <summary>
    /// Gets the Telegram client for the bot currently stored in BotContextAccessor.
    /// </summary>
    /// <returns>Telegram client for the active update context.</returns>
    public ITelegramBotClient GetCurrentClient()
    {
        return GetClient(BotContextAccessor.CurrentBotId);
    }

    /// <summary>
    /// Gets or creates a Telegram client for a BotId.
    /// </summary>
    /// <param name="botId">Internal bot id.</param>
    /// <returns>Cached or newly created TelegramBotClient.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the bot has no configured token.</exception>
    public ITelegramBotClient GetClient(string botId)
    {
        var bot = _registry.GetById(botId);
        if (bot == null || string.IsNullOrWhiteSpace(bot.Token))
            throw new InvalidOperationException("No Telegram bot token is configured.");

        lock (_syncRoot)
        {
            if (_clients.TryGetValue(bot.Id, out var existing))
                return existing;

            var created = new TelegramBotClient(bot.Token);
            _clients[bot.Id] = created;
            return created;
        }
    }

    /// <summary>
    /// Removes a cached client so a changed token will be used on the next request.
    /// </summary>
    /// <param name="botId">BotId whose client should be recreated.</param>
    public void Invalidate(string botId)
    {
        if (string.IsNullOrWhiteSpace(botId))
            return;

        lock (_syncRoot)
            _clients.Remove(botId);
    }
}

/// <summary>
/// Hosted service that starts one Telegram receiver per enabled bot.
/// All receivers dispatch updates into the shared TelegramBotService with a bot-specific runtime context.
/// </summary>
public class MultiBotHostedService : IHostedService
{
    private readonly BotRegistry _registry;
    private readonly BotClientProvider _clientProvider;
    private readonly TelegramBotService _dispatcher;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BotContextAccessor _botContextAccessor;
    private readonly ILogger<MultiBotHostedService> _logger;
    private CancellationTokenSource _receivingCts;
    private readonly Dictionary<string, CancellationTokenSource> _botReceivers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _syncRoot = new();

    /// <summary>
    /// Creates the hosted receiver manager.
    /// </summary>
    /// <param name="registry">Registry of owned and tenant bots.</param>
    /// <param name="clientProvider">Telegram client provider.</param>
    /// <param name="dispatcher">Shared update dispatcher.</param>
    /// <param name="scopeFactory">
    /// Factory used to create short-lived scopes for users.db cleanup when a tenant token is revoked or duplicated.
    /// </param>
    /// <param name="botContextAccessor">
    /// Async-local bot context accessor used to attribute polling errors to the bot whose receiver failed.
    /// </param>
    /// <param name="logger">Logger for receiver lifecycle events.</param>
    public MultiBotHostedService(
        BotRegistry registry,
        BotClientProvider clientProvider,
        TelegramBotService dispatcher,
        IServiceScopeFactory scopeFactory,
        BotContextAccessor botContextAccessor,
        ILogger<MultiBotHostedService> logger)
    {
        _registry = registry;
        _clientProvider = clientProvider;
        _dispatcher = dispatcher;
        _scopeFactory = scopeFactory;
        _botContextAccessor = botContextAccessor;
        _logger = logger;
    }

    /// <summary>
    /// Starts receivers for all enabled bots known at application startup.
    /// </summary>
    /// <param name="cancellationToken">Host shutdown token.</param>
    /// <returns>A task that completes after receiver startup has been requested.</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _receivingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        foreach (var bot in _registry.Bots)
        {
            await StartBotAsync(bot.Id, cancellationToken);
        }
    }

    /// <summary>
    /// Starts receiving updates for one bot if it is enabled and has a usable token.
    /// </summary>
    /// <param name="botId">
    /// Internal runtime bot id from the registry. For tenant storefronts this is the local <c>tenant-{ownerId}</c>
    /// value, not the numeric Telegram bot id.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for Telegram startup checks and command setup.</param>
    /// <returns>
    /// <c>true</c> when the receiver is already running or successfully started; <c>false</c> when the bot is
    /// disabled, missing a token, duplicated, or rejected by Telegram.
    /// </returns>
    /// <remarks>
    /// This method is intentionally fail-soft. A revoked tenant token disables only that tenant row in users.db
    /// and never stops other owned or tenant bots from starting. Owned bot tokens come from configuration and are
    /// never modified automatically.
    /// </remarks>
    public async Task<bool> StartBotAsync(string botId, CancellationToken cancellationToken = default)
    {
        var bot = _registry.GetById(botId);
        if (bot == null || !string.Equals(bot.Id, botId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!bot.Enabled || string.IsNullOrWhiteSpace(bot.Token))
        {
            _logger.LogInformation("Telegram bot receiver skipped. botId={BotId}, enabled={Enabled}, hasToken={HasToken}", bot.Id, bot.Enabled, !string.IsNullOrWhiteSpace(bot.Token));
            return false;
        }

        lock (_syncRoot)
        {
            if (_botReceivers.ContainsKey(bot.Id))
                return true;
        }

        var duplicate = FindRuntimeTokenConflict(bot);
        if (duplicate.HasConflict)
        {
            if (IsTenant(bot))
            {
                await DisableTenantTokenAsync(
                    bot,
                    $"duplicate token conflict with {duplicate.ConflictBotId}",
                    notifyOwner: true,
                    cancellationToken);
            }
            else
            {
                _logger.LogCritical(
                    "Configured non-tenant Telegram bot token conflict. botId={BotId}, conflictBotId={ConflictBotId}, token={MaskedToken}",
                    bot.Id,
                    duplicate.ConflictBotId,
                    TelegramBotTokenIdentity.MaskToken(bot.Token));
            }

            return false;
        }

        await DisableTenantConflictsWonByAsync(bot, cancellationToken);

        CancellationTokenSource botCts = null;
        try
        {
            // Each bot receives with its own token but dispatches through the shared TelegramBotService.
            var parentToken = _receivingCts?.Token ?? cancellationToken;
            botCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            var client = _clientProvider.GetClient(bot.Id);
            var me = await client.GetMeAsync(cancellationToken);
            await ConfigureBotCommandsAsync(client, bot, cancellationToken);
            var context = new BotRuntimeContext
            {
                Config = bot,
                Client = client
            };

            client.StartReceiving(
                updateHandler: (_, update, token) => _dispatcher.DispatchUpdateAsync(client, update, context, token),
                pollingErrorHandler: (_, exception, token) => HandleBotPollingErrorAsync(bot.Id, exception, token),
                receiverOptions: new ReceiverOptions
                {
                    AllowedUpdates = Array.Empty<UpdateType>()
                },
                cancellationToken: botCts.Token);

            lock (_syncRoot)
                _botReceivers[bot.Id] = botCts;

            _logger.LogInformation("Started Telegram bot receiver. botId={BotId}, username=@{Username}", bot.Id, me.Username);
            if (IsTenant(bot))
                LogTenantRuntimeEvent(bot, me.Username, "روشن شد", null);

            return true;
        }
        catch (Exception ex)
        {
            botCts?.Cancel();
            botCts?.Dispose();
            _clientProvider.Invalidate(bot.Id);

            if (IsTenant(bot) && IsTelegramTokenInvalidError(ex))
            {
                await DisableTenantTokenAsync(bot, ex.Message, notifyOwner: true, cancellationToken);
                return false;
            }

            if (IsTenant(bot))
            {
                _logger.LogError(ex, "Tenant Telegram bot receiver failed to start. botId={BotId}", bot.Id);
                LogTenantRuntimeEvent(bot, bot.Username, "خطا در روشن شدن", ex.Message);
            }
            else
            {
                _logger.LogCritical(ex, "Configured Telegram bot receiver failed to start. botId={BotId}", bot.Id);
            }

            return false;
        }
    }

    /// <summary>
    /// Disables enabled tenant bots that lose a duplicate-token decision to the bot currently being started.
    /// </summary>
    /// <param name="winner">Runtime bot that is allowed to keep the Telegram receiver.</param>
    /// <param name="cancellationToken">Cancellation token for users.db cleanup and owner notification.</param>
    /// <returns>A task that completes after all losing tenant rows have been disabled best-effort.</returns>
    /// <remarks>
    /// This closes the edge case where a tenant receiver was already running before an owned bot with the same
    /// token is evaluated. Owned bots are never mutated; tenant rows are disabled because they are user-managed.
    /// </remarks>
    private async Task DisableTenantConflictsWonByAsync(BotInstanceConfig winner, CancellationToken cancellationToken)
    {
        foreach (var other in _registry.Bots.ToList())
        {
            if (other == null ||
                !other.Enabled ||
                !IsTenant(other) ||
                string.Equals(other.Id, winner.Id, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(other.Token))
            {
                continue;
            }

            var sameToken = TelegramBotTokenIdentity.IsSameBotToken(winner.Token, other.Token);
            var sameUsername = !string.IsNullOrWhiteSpace(winner.Username) &&
                               string.Equals(
                                   TelegramBotTokenIdentity.NormalizeUsername(winner.Username),
                                   TelegramBotTokenIdentity.NormalizeUsername(other.Username),
                                   StringComparison.OrdinalIgnoreCase);

            if ((sameToken || sameUsername) && IsDuplicateWinner(winner, other))
            {
                await DisableTenantTokenAsync(
                    other,
                    $"duplicate token conflict with {winner.Id}",
                    notifyOwner: true,
                    cancellationToken);
            }
        }
    }

    /// <summary>
    /// Handles polling errors for a specific bot receiver and self-heals revoked tenant tokens.
    /// </summary>
    /// <param name="botId">
    /// Internal runtime bot id whose receiver produced the polling error. Tenant ids use the local
    /// <c>tenant-{ownerId}</c> format.
    /// </param>
    /// <param name="exception">Exception raised by the Telegram polling loop.</param>
    /// <param name="cancellationToken">Receiver cancellation token.</param>
    /// <returns>A task that completes after logging and any tenant cleanup attempt.</returns>
    /// <remarks>
    /// Telegram can report revoked tokens after a receiver has already been started. This handler disables only
    /// the affected tenant bot and then delegates normal error logging to the shared dispatcher.
    /// User-block, chat-not-found, and Telegram request-timeout errors are treated as delivery failures for a
    /// single update and are swallowed here so one blocked customer or slow Telegram reply cannot stop an owned or
    /// tenant receiver. A Telegram 409 getUpdates conflict means another process or receiver is already polling
    /// the same token; this receiver is stopped to prevent noisy conflict loops.
    /// </remarks>
    private async Task HandleBotPollingErrorAsync(string botId, Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || exception is OperationCanceledException)
            return;

        if (IsTelegramUserDeliveryError(exception))
        {
            _logger.LogWarning(
                "Telegram polling delivery error ignored. botId={BotId}, telegramError={Message}",
                botId,
                exception.Message);
            return;
        }

        var bot = _registry.GetById(botId);
        if (bot != null &&
            string.Equals(bot.Id, botId, StringComparison.OrdinalIgnoreCase) &&
            IsTelegramGetUpdatesConflict(exception))
        {
            await StopBotAsync(botId);
            _logger.LogCritical(
                "Telegram receiver stopped because another getUpdates poller is using the same token. botId={BotId}, username=@{Username}, telegramError={TelegramError}",
                bot.Id,
                bot.Username,
                exception.Message);
            return;
        }

        if (bot != null &&
            string.Equals(bot.Id, botId, StringComparison.OrdinalIgnoreCase) &&
            IsTenant(bot) &&
            IsTelegramTokenInvalidError(exception))
        {
            await DisableTenantTokenAsync(bot, exception.Message, notifyOwner: true, cancellationToken);
            return;
        }

        try
        {
            var client = bot != null && string.Equals(bot.Id, botId, StringComparison.OrdinalIgnoreCase)
                ? _clientProvider.GetClient(bot.Id)
                : _clientProvider.GetDefaultClient();
            var context = new BotRuntimeContext
            {
                Config = bot,
                Client = client
            };
            using (_botContextAccessor.Push(context))
            {
                await _dispatcher.HandlePollingErrorAsync(client, exception, cancellationToken);
            }
        }
        catch (Exception logException)
        {
            _logger.LogError(
                logException,
                "Telegram polling error logger failed. botId={BotId}, originalError={OriginalError}",
                botId,
                exception.Message);
        }
    }

    /// <summary>
    /// Detects Telegram's long-polling conflict response for duplicate getUpdates receivers.
    /// </summary>
    /// <param name="exception">Exception raised by the Telegram polling loop.</param>
    /// <returns>
    /// <c>true</c> when Telegram reports that another receiver is already polling the same bot token;
    /// otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// A 409 conflict is different from a user delivery failure. Continuing to poll will create an error loop, so
    /// the caller stops only the affected bot receiver and leaves the rest of the process alive.
    /// </remarks>
    private static bool IsTelegramGetUpdatesConflict(Exception exception)
    {
        if (exception is not ApiRequestException apiException)
            return false;

        var message = apiException.Message ?? string.Empty;
        return apiException.ErrorCode == 409 ||
               message.Contains("terminated by other getUpdates request", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("only one bot instance is running", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Detects Telegram delivery errors that mean one user blocked the bot, the chat is unreachable, or Telegram timed out.
    /// </summary>
    /// <param name="exception">Exception raised by the Telegram polling loop or update handler.</param>
    /// <returns>
    /// <c>true</c> when the error is a non-fatal per-user or transient Telegram delivery failure; otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// The polling library forwards unhandled update-handler exceptions into the polling error callback. A
    /// customer blocking any owned or tenant bot, or a single Telegram send timeout, must not disable that receiver
    /// or terminate the process.
    /// </remarks>
    private static bool IsTelegramUserDeliveryError(Exception exception)
    {
        if (exception is RequestException requestException)
        {
            var requestMessage = requestException.Message ?? string.Empty;
            return requestMessage.Contains("request timed out", StringComparison.OrdinalIgnoreCase) ||
                   requestMessage.Contains("timed out", StringComparison.OrdinalIgnoreCase);
        }

        if (exception is not ApiRequestException apiException)
            return false;

        var message = apiException.Message ?? string.Empty;
        return apiException.ErrorCode == 403 ||
               message.Contains("bot was blocked", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("user is deactivated", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("chat not found", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("forbidden", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds a token or username collision between the current bot and other enabled runtime bots.
    /// </summary>
    /// <param name="bot">Runtime bot being started.</param>
    /// <returns>
    /// A conflict result. <see cref="RuntimeBotTokenConflict.HasConflict" /> is <c>true</c> only when
    /// <paramref name="bot" /> must not be started.
    /// </returns>
    /// <remarks>
    /// Owned bots win over tenant bots because owned tokens are controlled by configuration. When two bots have
    /// the same ownership type, the lexicographically smaller internal bot id is treated as the winner so startup
    /// is deterministic and exactly one receiver survives.
    /// </remarks>
    private RuntimeBotTokenConflict FindRuntimeTokenConflict(BotInstanceConfig bot)
    {
        foreach (var other in _registry.Bots)
        {
            if (other == null ||
                !other.Enabled ||
                string.Equals(other.Id, bot.Id, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(other.Token))
            {
                continue;
            }

            var sameToken = TelegramBotTokenIdentity.IsSameBotToken(bot.Token, other.Token);
            var sameUsername = !string.IsNullOrWhiteSpace(bot.Username) &&
                               string.Equals(
                                   TelegramBotTokenIdentity.NormalizeUsername(bot.Username),
                                   TelegramBotTokenIdentity.NormalizeUsername(other.Username),
                                   StringComparison.OrdinalIgnoreCase);

            if ((sameToken || sameUsername) && !IsDuplicateWinner(bot, other))
                return RuntimeBotTokenConflict.Conflict(other.Id);
        }

        return RuntimeBotTokenConflict.None;
    }

    /// <summary>
    /// Determines which bot should keep running when two runtime entries identify the same Telegram bot.
    /// </summary>
    /// <param name="current">Bot currently being evaluated for startup.</param>
    /// <param name="other">Existing conflicting bot entry from the registry.</param>
    /// <returns>
    /// <c>true</c> when <paramref name="current" /> should be allowed to start; otherwise <c>false</c>.
    /// </returns>
    private static bool IsDuplicateWinner(BotInstanceConfig current, BotInstanceConfig other)
    {
        if (IsTenant(current) && !IsTenant(other))
            return false;
        if (!IsTenant(current) && IsTenant(other))
            return true;

        return string.Compare(current.Id, other.Id, StringComparison.OrdinalIgnoreCase) < 0;
    }

    /// <summary>
    /// Disables a tenant bot whose token can no longer be trusted.
    /// </summary>
    /// <param name="bot">Runtime tenant bot configuration that failed validation or duplicated another bot.</param>
    /// <param name="reason">
    /// Non-secret technical reason recorded in structured logs. This value may contain a Telegram error message
    /// but must never contain the full bot token.
    /// </param>
    /// <param name="notifyOwner">Whether to notify the tenant owner through the default owned bot.</param>
    /// <param name="cancellationToken">Cancellation token for database and Telegram operations.</param>
    /// <returns>A task that completes after best-effort cleanup and owner notification.</returns>
    /// <remarks>
    /// The tenant username and settings are preserved for the owner panel, but <c>Token</c> is cleared and
    /// <c>Enabled</c> is set to <c>false</c>. This prevents one revoked tenant token from breaking the whole process.
    /// </remarks>
    private async Task DisableTenantTokenAsync(
        BotInstanceConfig bot,
        string reason,
        bool notifyOwner,
        CancellationToken cancellationToken)
    {
        await StopBotAsync(bot.Id);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var tenant = await db.BotInstances.FirstOrDefaultAsync(x => x.Id == bot.Id, cancellationToken);
        if (tenant == null)
            return;

        tenant.Enabled = false;
        tenant.Token = null;
        tenant.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        _registry.Upsert(tenant);
        _clientProvider.Invalidate(tenant.Id);

        _logger.LogWarning(
            "Tenant bot disabled and token cleared. botId={BotId}, username=@{Username}, owner={OwnerTelegramUserId}, reason={Reason}",
            tenant.Id,
            tenant.Username,
            tenant.OwnerTelegramUserId,
            reason);

        LogTenantRuntimeEvent(
            tenant.Id,
            tenant.Username,
            tenant.OwnerTelegramUserId,
            DeserializeRuntimeStringList(tenant.TenantChannelIdsJson),
            tenant.SupportAccount,
            "خاموش شد",
            reason);

        if (notifyOwner && tenant.OwnerTelegramUserId.HasValue)
            await NotifyTenantOwnerTokenClearedAsync(tenant.OwnerTelegramUserId.Value, tenant.Username, cancellationToken);
    }

    /// <summary>
    /// Sends a best-effort owner notification when a tenant token is revoked or duplicated.
    /// </summary>
    /// <param name="ownerTelegramUserId">Numeric Telegram user id of the colleague who owns the tenant bot.</param>
    /// <param name="tenantUsername">Last known tenant bot username, used only for display.</param>
    /// <param name="cancellationToken">Cancellation token for the Telegram send operation.</param>
    /// <returns>A task that completes after the notification attempt.</returns>
    private async Task NotifyTenantOwnerTokenClearedAsync(
        long ownerTelegramUserId,
        string tenantUsername,
        CancellationToken cancellationToken)
    {
        try
        {
            var username = string.IsNullOrWhiteSpace(tenantUsername)
                ? "ربات فروشگاهی شما"
                : "@" + TelegramBotTokenIdentity.NormalizeUsername(tenantUsername);

            await _clientProvider.GetDefaultClient().SendTextMessageAsync(
                ownerTelegramUserId,
                $"⚠️ توکن {username} باطل یا تکراری تشخیص داده شد.\nفروشگاه خاموش شد و توکن حذف شد. لطفاً از BotFather توکن جدید بگیرید و دوباره ثبت کنید.",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to notify tenant owner about cleared token. owner={OwnerTelegramUserId}", ownerTelegramUserId);
        }
    }

    /// <summary>
    /// Checks whether a Telegram startup or polling exception means the bot token is invalid.
    /// </summary>
    /// <param name="exception">Exception returned by Telegram client calls or polling.</param>
    /// <returns>
    /// <c>true</c> when Telegram clearly rejected the token with an authorization-style error; otherwise <c>false</c>.
    /// </returns>
    private static bool IsTelegramTokenInvalidError(Exception exception)
    {
        if (exception is ApiRequestException apiException)
            return apiException.ErrorCode == 401 ||
                   ContainsTokenInvalidText(apiException.Message);

        return ContainsTokenInvalidText(exception?.Message);
    }

    /// <summary>
    /// Performs a conservative text check for Telegram invalid-token errors.
    /// </summary>
    /// <param name="message">Exception message returned by Telegram or the client library.</param>
    /// <returns><c>true</c> when the message indicates unauthorized, invalid token, or revoked token.</returns>
    private static bool ContainsTokenInvalidText(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("invalid token", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("bot token", StringComparison.OrdinalIgnoreCase) && message.Contains("invalid", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks whether the runtime configuration represents a colleague tenant storefront bot.
    /// </summary>
    /// <param name="bot">Runtime bot configuration to inspect.</param>
    /// <returns><c>true</c> for tenant storefront bots; otherwise <c>false</c>.</returns>
    private static bool IsTenant(BotInstanceConfig bot)
    {
        return string.Equals(bot?.Type, BotInstanceTypes.Tenant, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Writes the tenant runtime lifecycle event to the private operational Telegram log.
    /// </summary>
    /// <param name="bot">Runtime tenant bot configuration.</param>
    /// <param name="telegramUsername">Username returned by Telegram <c>GetMe</c>, when available.</param>
    /// <param name="status">Human-readable lifecycle status such as started, stopped, or failed.</param>
    /// <param name="error">Optional non-secret error text.</param>
    private void LogTenantRuntimeEvent(BotInstanceConfig bot, string telegramUsername, string status, string error)
    {
        LogTenantRuntimeEvent(
            bot.Id,
            string.IsNullOrWhiteSpace(telegramUsername) ? bot.Username : telegramUsername,
            bot.OwnerTelegramUserId,
            bot.TenantChannelIds ?? new List<string>(),
            bot.SupportAccount,
            status,
            error);
    }

    /// <summary>
    /// Writes a tenant runtime lifecycle event using already extracted tenant settings.
    /// </summary>
    /// <param name="tenantId">Internal tenant bot id.</param>
    /// <param name="tenantUsername">Last known public tenant bot username.</param>
    /// <param name="ownerTelegramUserId">Telegram user id of the tenant owner, when known.</param>
    /// <param name="channels">Tenant forced-join channels configured by the owner.</param>
    /// <param name="supportAccount">Tenant support username or contact text.</param>
    /// <param name="status">Lifecycle status shown in the private log channel.</param>
    /// <param name="error">Optional non-secret error text shown in the private log channel.</param>
    private void LogTenantRuntimeEvent(
        string tenantId,
        string tenantUsername,
        long? ownerTelegramUserId,
        IEnumerable<string> channels,
        string supportAccount,
        string status,
        string error)
    {
        var owner = ownerTelegramUserId.HasValue
            ? $"<a href=\"tg://user?id={ownerTelegramUserId.Value}\">{ownerTelegramUserId.Value}</a>"
            : "<code>نامشخص</code>";

        var username = string.IsNullOrWhiteSpace(tenantUsername)
            ? "ثبت نشده"
            : "@" + TelegramBotTokenIdentity.NormalizeUsername(tenantUsername);

        var message =
            "🤖 <b>وضعیت ربات فروشگاهی tenant</b>\n\n" +
            $"وضعیت: <b>{Html(status)}</b>\n" +
            $"ربات: <code>{Html(username)}</code>\n" +
            $"شناسه داخلی: <code>{Html(tenantId)}</code>\n" +
            $"مالک: {owner}\n" +
            $"کانال جوین اجباری: {FormatTelegramReferences(channels)}\n" +
            $"پشتیبانی: {FormatTelegramReference(supportAccount)}" +
            (string.IsNullOrWhiteSpace(error) ? string.Empty : $"\nخطا: <code>{Html(error)}</code>");

        _logger.LogPayment(message);
    }

    /// <summary>
    /// Formats a collection of Telegram usernames, links, or private ids for an HTML log message.
    /// </summary>
    /// <param name="references">Channel identifiers configured by the tenant owner.</param>
    /// <returns>Comma-separated HTML-safe references suitable for Telegram <c>ParseMode.Html</c>.</returns>
    private static string FormatTelegramReferences(IEnumerable<string> references)
    {
        var formatted = (references ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(FormatTelegramReference)
            .ToArray();

        return formatted.Length == 0 ? "<code>ثبت نشده</code>" : string.Join(", ", formatted);
    }

    /// <summary>
    /// Formats one Telegram username, t.me link, numeric private id, or free-form support value.
    /// </summary>
    /// <param name="reference">Raw tenant channel/support value from users.db or runtime configuration.</param>
    /// <returns>HTML-safe clickable link when Telegram can open it publicly; otherwise code/plain safe text.</returns>
    private static string FormatTelegramReference(string reference)
    {
        var value = reference?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return "<code>ثبت نشده</code>";

        if (value.StartsWith("@", StringComparison.Ordinal) && value.Length > 1)
        {
            var username = value.TrimStart('@');
            return $"<a href=\"https://t.me/{HtmlAttribute(username)}\">@{Html(username)}</a>";
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (string.Equals(uri.Host, "t.me", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Host, "telegram.me", StringComparison.OrdinalIgnoreCase)))
        {
            return $"<a href=\"{HtmlAttribute(uri.ToString())}\">{Html(value)}</a>";
        }

        return long.TryParse(value, out _)
            ? $"<code>{Html(value)}</code>"
            : Html(value);
    }

    /// <summary>
    /// Decodes a persisted JSON array of string settings used by tenant bot rows.
    /// </summary>
    /// <param name="json">JSON array stored in users.db, or null.</param>
    /// <returns>A non-null list of trimmed values; invalid JSON returns an empty list.</returns>
    private static IReadOnlyList<string> DeserializeRuntimeStringList(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<string>();

        try
        {
            return JsonConvert.DeserializeObject<List<string>>(json)?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToArray() ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// HTML-encodes a value for Telegram HTML message text.
    /// </summary>
    /// <param name="value">Raw value that may contain HTML-sensitive characters.</param>
    /// <returns>Encoded text, or an empty string for null.</returns>
    private static string Html(string value)
    {
        return WebUtility.HtmlEncode(value ?? string.Empty);
    }

    /// <summary>
    /// HTML-encodes a value for use inside an HTML attribute.
    /// </summary>
    /// <param name="value">Raw attribute value.</param>
    /// <returns>Encoded attribute value.</returns>
    private static string HtmlAttribute(string value)
    {
        return WebUtility.HtmlEncode(value ?? string.Empty);
    }

    /// <summary>
    /// Publishes the Telegram command menu for the bot currently being started.
    /// </summary>
    /// <param name="client">
    /// Telegram client created with the token of the bot currently being started. Do not pass the default owned bot
    /// client when configuring another owned brand bot or a tenant storefront.
    /// </param>
    /// <param name="bot">
    /// Runtime bot configuration from the registry. Tenant storefronts receive only <c>/start</c>, while owned bots
    /// receive the public support commands used by customers and colleagues.
    /// </param>
    /// <param name="cancellationToken">
    /// Startup cancellation token used to abort the Telegram API call when the host is stopping.
    /// </param>
    /// <returns>
    /// A task that completes after Telegram accepts the command menu or immediately when the bot type does not need
    /// commands.
    /// </returns>
    /// <remarks>
    /// Tenant customers already use <c>/start</c> to return to the storefront home menu. Owned bot users can also use
    /// the command menu to return to the main menu or start account operations without relying on a persistent reply
    /// keyboard while a purchase flow is active.
    /// </remarks>
    /// <example>
    /// <code>
    /// await ConfigureBotCommandsAsync(client, bot, cancellationToken);
    /// </code>
    /// </example>
    private static async Task ConfigureBotCommandsAsync(
        ITelegramBotClient client,
        BotInstanceConfig bot,
        CancellationToken cancellationToken)
    {
        BotCommand[] commands;
        if (string.Equals(bot.Type, BotInstanceTypes.Tenant, StringComparison.OrdinalIgnoreCase))
        {
            commands = new[]
            {
                new BotCommand
                {
                    Command = "start",
                    Description = "بازگشت به منوی اصلی"
                }
            };
        }
        else if (string.Equals(bot.Type, BotInstanceTypes.Owned, StringComparison.OrdinalIgnoreCase) ||
                 string.IsNullOrWhiteSpace(bot.Type))
        {
            commands = new[]
            {
                new BotCommand { Command = "start", Description = "شروع مجدد ربات و برگشت به منوی اصلی" },
                new BotCommand { Command = "renew_email", Description = "شروع تمدید اکانت با نام اکانت یا ایمیل" },
                new BotCommand { Command = "enable_email", Description = "فعال کردن اکانت با نام اکانت یا ایمیل" },
                new BotCommand { Command = "disable_email", Description = "غیرفعال کردن اکانت با نام اکانت یا ایمیل" },
                new BotCommand { Command = "account_number", Description = "جستجوی اکانت همکار بر اساس شماره اکانت" }
            };
        }
        else
        {
            return;
        }

        await client.SetMyCommandsAsync(commands, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Stops one bot receiver without stopping the whole application.
    /// </summary>
    /// <param name="botId">Internal BotId to stop.</param>
    /// <returns>A completed task after the receiver cancellation token has been cancelled.</returns>
    public Task StopBotAsync(string botId)
    {
        if (string.IsNullOrWhiteSpace(botId))
            return Task.CompletedTask;

        CancellationTokenSource cts = null;
        lock (_syncRoot)
        {
            if (_botReceivers.TryGetValue(botId, out cts))
                _botReceivers.Remove(botId);
        }

        cts?.Cancel();
        cts?.Dispose();
        _logger.LogInformation("Stopped Telegram bot receiver. botId={BotId}", botId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops all active Telegram receivers during host shutdown.
    /// </summary>
    /// <param name="cancellationToken">Host shutdown token.</param>
    /// <returns>A completed task after cancellation tokens are disposed.</returns>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            foreach (var receiver in _botReceivers.Values)
            {
                receiver.Cancel();
                receiver.Dispose();
            }
            _botReceivers.Clear();
        }

        _receivingCts?.Cancel();
        _receivingCts?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Non-secret duplicate-token result used during bot receiver startup.
    /// </summary>
    /// <remarks>
    /// The result stores only the conflicting runtime bot id. It never stores the token or token secret, so it can
    /// be used safely in structured logs and owner-facing cleanup decisions.
    /// </remarks>
    private sealed class RuntimeBotTokenConflict
    {
        /// <summary>
        /// Shared instance representing a startup candidate with no token conflict.
        /// </summary>
        public static readonly RuntimeBotTokenConflict None = new();

        /// <summary>
        /// Indicates whether the evaluated bot must not be started.
        /// </summary>
        public bool HasConflict { get; private init; }

        /// <summary>
        /// Internal bot id of the runtime entry that won the duplicate-token decision.
        /// </summary>
        public string ConflictBotId { get; private init; }

        /// <summary>
        /// Creates a conflict result for a specific winning runtime bot id.
        /// </summary>
        /// <param name="conflictBotId">Internal bot id of the bot that should keep the receiver.</param>
        /// <returns>A conflict result with <see cref="HasConflict" /> set to <c>true</c>.</returns>
        public static RuntimeBotTokenConflict Conflict(string conflictBotId)
        {
            return new RuntimeBotTokenConflict
            {
                HasConflict = true,
                ConflictBotId = conflictBotId
            };
        }
    }
}
