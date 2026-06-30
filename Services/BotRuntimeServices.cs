using Adminbot.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Telegram.Bot;
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
    /// <param name="logger">Logger for receiver lifecycle events.</param>
    public MultiBotHostedService(
        BotRegistry registry,
        BotClientProvider clientProvider,
        TelegramBotService dispatcher,
        ILogger<MultiBotHostedService> logger)
    {
        _registry = registry;
        _clientProvider = clientProvider;
        _dispatcher = dispatcher;
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
    /// Starts receiving updates for one bot if it is enabled and has a token.
    /// This is also used when a colleague toggles a tenant bot on at runtime.
    /// </summary>
    /// <param name="botId">Internal BotId to start.</param>
    /// <param name="cancellationToken">Cancellation token for GetMe/startup checks.</param>
    /// <returns>True when the receiver is already running or successfully started; otherwise false.</returns>
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

        // Each bot receives with its own token but dispatches through the shared TelegramBotService.
        var parentToken = _receivingCts?.Token ?? cancellationToken;
        var botCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        var client = _clientProvider.GetClient(bot.Id);
        var me = await client.GetMeAsync(cancellationToken);
        await ConfigureTenantCommandsAsync(client, bot, cancellationToken);
        var context = new BotRuntimeContext
        {
            Config = bot,
            Client = client
        };

        client.StartReceiving(
            updateHandler: (_, update, token) => _dispatcher.DispatchUpdateAsync(client, update, context, token),
            pollingErrorHandler: (botClient, exception, token) => _dispatcher.HandlePollingErrorAsync(botClient, exception, token),
            receiverOptions: new ReceiverOptions
            {
                AllowedUpdates = Array.Empty<UpdateType>()
            },
            cancellationToken: botCts.Token);

        lock (_syncRoot)
            _botReceivers[bot.Id] = botCts;

        _logger.LogInformation("Started Telegram bot receiver. botId={BotId}, username=@{Username}", bot.Id, me.Username);
        return true;
    }

    /// <summary>
    /// Publishes the Telegram command menu for a tenant storefront bot.
    /// </summary>
    /// <param name="client">
    /// Telegram client created with the token of the bot currently being started.
    /// The method must receive the tenant bot client, not the default owned bot client.
    /// </param>
    /// <param name="bot">
    /// Runtime bot configuration from the registry. Commands are published only when this config represents
    /// a tenant storefront; owned and sales-assistant bots keep their existing command configuration.
    /// </param>
    /// <param name="cancellationToken">
    /// Startup cancellation token used to abort the Telegram API call when the host is stopping.
    /// </param>
    /// <returns>
    /// A task that completes after Telegram accepts the command menu or immediately when the bot is not a tenant.
    /// </returns>
    /// <remarks>
    /// Tenant customers already use <c>/start</c> to return to the storefront home menu. This method only exposes
    /// that command in Telegram's command menu so users can discover it. It does not change conversation state,
    /// payments, orders, or tenant settings.
    /// </remarks>
    /// <example>
    /// <code>
    /// await ConfigureTenantCommandsAsync(client, bot, cancellationToken);
    /// </code>
    /// </example>
    private static async Task ConfigureTenantCommandsAsync(
        ITelegramBotClient client,
        BotInstanceConfig bot,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(bot.Type, BotInstanceTypes.Tenant, StringComparison.OrdinalIgnoreCase))
            return;

        await client.SetMyCommandsAsync(
            new[]
            {
                new BotCommand
                {
                    Command = "start",
                    Description = "بازگشت به منوی اصلی"
                }
            },
            cancellationToken: cancellationToken);
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
}
