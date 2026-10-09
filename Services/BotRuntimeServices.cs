using Adminbot.Domain;
using Adminbot.Domain.Logging;
using Adminbot.Services.Telemetry;
using Adminbot.Services.TelegramEndpoints;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
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
    /// <summary>Raised after runtime upsert so deferred durable work can resume promptly.</summary>
    public event Action AvailabilityChanged;

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
        AvailabilityChanged?.Invoke();
    }

    /// <summary>
    /// Converts a persisted bot row into runtime configuration, retaining storefront pricing and public-channel consent.
    /// </summary>
    /// <param name="bot">Required persisted users.db row; prices and public-channel consent belong to this exact internal store id.</param>
    /// <returns>A detached runtime configuration with prices and explicit participation opt-out unchanged; no database write occurs.</returns>
    /// <remarks>An invalid stored pricing mode stays invalid. Public-channel consent never inherits another store's value.</remarks>
    /// <example><code>registry.Upsert(savedTenant); // Retains the saved public-channel opt-out in runtime.</code></example>
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
            // Rows written by an older build can still carry the historical "0" backup sentinel, because the previous
            // normalizer converted a missing long configuration value into the non-blank string "0". Normalizing on
            // read means every runtime consumer of a tenant row sees the same "not configured" representation as the
            // configuration path, and the next configuration sync rewrites the corrected empty value.
            LoggerChannel = TelegramDestination.NormalizeStoredChannel(bot.LoggerChannel),
            BackupChannel = TelegramDestination.NormalizeStoredChannel(bot.BackupChannel),
            IosTutorial = DeserializeStringList(bot.IosTutorialJson).ToArray(),
            AndroidTutorial = DeserializeStringList(bot.AndroidTutorialJson).ToArray(),
            WindowsTutorial = DeserializeStringList(bot.WindowsTutorialJson).ToArray(),
            Type = string.IsNullOrWhiteSpace(bot.Type) ? BotInstanceTypes.Owned : bot.Type,
            IsDefault = bot.IsDefault,
            Enabled = bot.Enabled,
            OwnerTelegramUserId = bot.OwnerTelegramUserId,
            TenantPriceMarkupPercent = bot.TenantPriceMarkupPercent,
            TenantPricingMode = bot.TenantPricingMode,
            TenantNormalPricePerGbToman = bot.TenantNormalPricePerGbToman,
            TenantNormalPricePerDayToman = bot.TenantNormalPricePerDayToman,
            TenantNationalPricePerGbToman = bot.TenantNationalPricePerGbToman,
            TenantUnlimitedPlanPricesJson = bot.TenantUnlimitedPlanPricesJson,
            TenantWelcomeText = bot.TenantWelcomeText,
            TenantMandatoryJoinEnabled = bot.TenantMandatoryJoinEnabled,
            TenantPublicChannelPostsEnabled = bot.TenantPublicChannelPostsEnabled,
            TenantChannelIds = DeserializeStringList(bot.TenantChannelIdsJson),
            TenantCardPaymentEnabled = bot.TenantCardPaymentEnabled,
            TenantCardNumber = bot.TenantCardNumber,
            TenantCardHolderName = bot.TenantCardHolderName,
            TenantHooshPayEnabled = bot.TenantHooshPayEnabled,
            TenantNowPaymentsEnabled = bot.TenantNowPaymentsEnabled,
            TenantTetraminatorEnabled = bot.TenantTetraminatorEnabled,
            TenantUniquePayEnabled = bot.TenantUniquePayEnabled,
            TenantAtlasPayEnabled = bot.TenantAtlasPayEnabled,
            // The storefront premium-visual preference must survive every BotInstance -> BotInstanceConfig conversion so
            // a restart, a receiver rebuild, or an unrelated settings change can never silently disable it.
            TenantPremiumUiEnabled = bot.TenantPremiumUiEnabled,
            TenantOwnerNotificationBotId = bot.TenantOwnerNotificationBotId,
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
    /// Applies fallback identity and destination values and normalizes configured bot settings.
    /// </summary>
    /// <param name="bot">Configured bot from configuration.json, including any explicitly configured storefront rates.</param>
    /// <param name="fallback">Application-level fallback for bot identity, token, and global destinations, never tenant pricing.</param>
    /// <returns>Runtime configured bot preserving its pricing mode, markup and nullable manual rates without tenant price fallback.</returns>
    /// <remarks>
    /// Channel normalization follows the shared <see cref="TelegramDestination"/> contract: blank or numeric zero
    /// becomes empty. Malformed non-empty destinations are retained for downstream validation. Pricing fields are
    /// copied only from this configured bot; they are not inferred from another tenant or the application fallback.
    /// An unknown pricing mode remains invalid instead of silently becoming percentage pricing. No network call occurs.
    /// Public-channel participation is copied unchanged from this store, never inherited from fallback configuration.
    /// </remarks>
    /// <example><code>var configured = NormalizeBot(bot, applicationConfig);</code></example>
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
            // One stored representation for "not configured": an empty string. AppConfig.BackupChannel is a long whose
            // missing default is 0, and the previous code turned that 0 into the non-blank string "0", which later
            // code treated as a real Telegram destination. The shared normalizer applies the zero sentinel to both the
            // fallback and the explicitly configured value, so "0" can never become a deliverable destination again.
            LoggerChannel = TelegramDestination.NormalizeStoredChannel(
                string.IsNullOrWhiteSpace(bot.LoggerChannel) ? fallback.LoggerChannel : bot.LoggerChannel),
            BackupChannel = TelegramDestination.NormalizeStoredChannel(
                string.IsNullOrWhiteSpace(bot.BackupChannel)
                    ? fallback.BackupChannel.ToString(CultureInfo.InvariantCulture)
                    : bot.BackupChannel),
            IosTutorial = bot.IosTutorial ?? fallback.IosTutorial,
            AndroidTutorial = bot.AndroidTutorial ?? fallback.AndroidTutorial,
            WindowsTutorial = bot.WindowsTutorial ?? fallback.WindowsTutorial,
            Type = string.IsNullOrWhiteSpace(bot.Type) ? BotInstanceTypes.Owned : bot.Type,
            Enabled = bot.Enabled,
            IsDefault = bot.IsDefault,
            OwnerTelegramUserId = bot.OwnerTelegramUserId,
            TenantPriceMarkupPercent = bot.TenantPriceMarkupPercent,
            TenantPricingMode = bot.TenantPricingMode,
            TenantNormalPricePerGbToman = bot.TenantNormalPricePerGbToman,
            TenantNormalPricePerDayToman = bot.TenantNormalPricePerDayToman,
            TenantNationalPricePerGbToman = bot.TenantNationalPricePerGbToman,
            TenantUnlimitedPlanPricesJson = bot.TenantUnlimitedPlanPricesJson,
            TenantWelcomeText = bot.TenantWelcomeText,
            TenantMandatoryJoinEnabled = bot.TenantMandatoryJoinEnabled,
            TenantPublicChannelPostsEnabled = bot.TenantPublicChannelPostsEnabled,
            TenantChannelIds = bot.TenantChannelIds?.Where(c => !string.IsNullOrWhiteSpace(c)).ToList() ?? new List<string>(),
            TenantCardPaymentEnabled = bot.TenantCardPaymentEnabled,
            TenantCardNumber = bot.TenantCardNumber,
            TenantCardHolderName = bot.TenantCardHolderName,
            TenantHooshPayEnabled = bot.TenantHooshPayEnabled,
            TenantNowPaymentsEnabled = bot.TenantNowPaymentsEnabled,
            TenantTetraminatorEnabled = bot.TenantTetraminatorEnabled,
            TenantUniquePayEnabled = bot.TenantUniquePayEnabled,
            TenantAtlasPayEnabled = bot.TenantAtlasPayEnabled,
            TenantOwnerNotificationBotId = bot.TenantOwnerNotificationBotId,
            TenantTutorialsJson = bot.TenantTutorialsJson,
            IsSalesAssistant = bot.IsSalesAssistant
        };
    }
}

/// <summary>Expected domain failure when the exact Telegram transport requested by historical work is unavailable.</summary>
public sealed class BotTransportUnavailableException : InvalidOperationException
{
    public const string FailureCode = "bot_transport_unavailable";
    public string ReasonCode { get; }

    public BotTransportUnavailableException(string reasonCode)
        : base("The requested Telegram bot transport is unavailable.")
    {
        ReasonCode = string.IsNullOrWhiteSpace(reasonCode) ? "unavailable" : reasonCode;
    }
}

/// <summary>
/// Lazily creates and caches TelegramBotClient instances per BotId.
/// </summary>
/// <remarks>
/// Runtime delivery refuses disabled tenants. An explicitly owner-authorized activation capability probe can resolve
/// the exact disabled tenant's transport without enabling its storefront or registering a receiver.
/// </remarks>
public class BotClientProvider : IDisposable
{
    private readonly BotRegistry _registry;
    /// <summary>Creates a fresh client after first use or explicit invalidation without changing cache semantics.</summary>
    private readonly Func<BotInstanceConfig, ITelegramBotClient> _clientFactory;
    /// <summary>Provider-owned three-minute socket pool; redirects cannot forward token-bearing paths to an untrusted origin.</summary>
    private readonly SocketsHttpHandler _transport = new() { PooledConnectionLifetime = TimeSpan.FromMinutes(3), AllowAutoRedirect = false };
    /// <summary>Optional shared writer; transport observations never perform I/O on the caller.</summary>
    private readonly LatencyTelemetryService _telemetry;
    /// <summary>Optional endpoint admission authority; direct legacy test constructors retain their original factory behavior.</summary>
    private readonly TelegramEndpointRuntimeGate _endpointGate;
    /// <summary>Validated trusted Cloud/loopback Local origins and read-only host file mapping.</summary>
    private readonly TelegramEndpointRoutingOptions _endpointOptions;
    /// <summary>At most one cached SDK epoch per configured bot and endpoint; obsolete SDKs retain in-flight send ownership until collection.</summary>
    private readonly Dictionary<(string BotId, TelegramEndpointType Endpoint), EndpointSdk> _endpointSdks = new();
    /// <summary>Bounded reusable read-only token-probe SDKs, keyed by secret fingerprint rather than raw token.</summary>
    private readonly Dictionary<(string BotId, TelegramEndpointType Endpoint, long Generation, string Fingerprint), ITelegramBotClient> _probeSdks = new();
    /// <summary>Optional deterministic SDK factory for production-routing verification without real bot tokens or network calls.</summary>
    private readonly Func<BotInstanceConfig, TelegramEndpointRoute, ITelegramBotClient> _endpointFactory;
    /// <summary>Caller-owned deterministic HTTP handler substituting only the socket layer for real SDK routing verification.</summary>
    private readonly HttpMessageHandler _endpointTestTransport;
    /// <summary>Exact production gate used by this provider; token probes must share this instance instead of bypassing saved state.</summary>
    internal TelegramEndpointRuntimeGate EndpointGate => _endpointGate;
    /// <summary>Gets metadata-only health for this provider's active receivers across every bot family.</summary>
    public TelegramPollingTelemetryTracker PollingTelemetry { get; }
    /// <summary>Gets whether runtime provenance should be allocated for the shared enabled writer.</summary>
    internal bool IsTelemetryEnabled => _telemetry?.Enabled == true && !LatencyTelemetrySuppression.IsActive;
    private readonly Dictionary<string, ITelegramBotClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _syncRoot = new();

    /// <summary>
    /// Creates a provider bound to the shared BotRegistry and a single pooled socket transport.
    /// </summary>
    /// <param name="registry">Shared registry of canonical internal bot ids and current bot tokens.</param>
    /// <param name="telemetry">Optional shared nonblocking telemetry writer; null preserves existing direct-constructor callers.</param>
    /// <param name="endpointGate">Optional shared identity-bound routing gate; production publishes saved states before hosted work starts.</param>
    /// <param name="endpointOptions">Validated configured origins/file mapping; missing configuration preserves Cloud defaults.</param>
    /// <remarks>
    /// Disables v22 automatic rate-limit retries so existing application policy remains authoritative.
    /// HttpClient is created once per cached bot, never per request. The shared socket pool has the SDK's
    /// three-minute connection lifetime; no timeout, TLS, DNS, proxy or retry policy is changed.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The registry is null.</exception>
    /// <example><code>var provider = new BotClientProvider(registry, telemetry);</code></example>
    public BotClientProvider(BotRegistry registry, LatencyTelemetryService telemetry = null,
        TelegramEndpointRuntimeGate endpointGate = null, TelegramEndpointRoutingOptions endpointOptions = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _telemetry = telemetry;
        _endpointGate = endpointGate;
        _endpointOptions = (endpointOptions ?? new TelegramEndpointRoutingOptions()).ValidateAndSnapshot();
        PollingTelemetry = new TelegramPollingTelemetryTracker(telemetry);
        _clientFactory = endpointGate == null ? CreateClient : CreateRoutingClient;
    }

    /// <summary>Creates the real routing facade over deterministic SDK transports for isolated integration verification.</summary>
    /// <param name="registry">Exact current bot identity authority.</param>
    /// <param name="endpointGate">Real shared pre-claim/request fence.</param>
    /// <param name="endpointOptions">Validated trusted endpoint/mapping configuration.</param>
    /// <param name="endpointFactory">Per-generation SDK factory using fake HTTP, never production tokens or sessions.</param>
    /// <param name="telemetry">Optional real JSONL writer used by endpoint correlation tests.</param>
    /// <remarks>The alternate factory preserves real facade, generation cache, admission and file provenance behavior.</remarks>
    internal BotClientProvider(BotRegistry registry, TelegramEndpointRuntimeGate endpointGate,
        TelegramEndpointRoutingOptions endpointOptions, Func<BotInstanceConfig, TelegramEndpointRoute, ITelegramBotClient> endpointFactory,
        LatencyTelemetryService telemetry = null) : this(registry, telemetry, endpointGate, endpointOptions)
    {
        _endpointFactory = endpointFactory ?? throw new ArgumentNullException(nameof(endpointFactory));
    }

    /// <summary>Exercises production SDK construction and trusted baseUrl selection over an isolated fake HTTP transport.</summary>
    /// <param name="registry">Exact current identity authority.</param>
    /// <param name="endpointGate">Real handler/request admission fence.</param>
    /// <param name="endpointOptions">Validated fixed Cloud/loopback Local origins.</param>
    /// <param name="transport">Caller-owned fake transport; no live URL is contacted.</param>
    /// <param name="telemetry">Optional real JSONL writer for request correlation.</param>
    /// <remarks>No SDK or request is mocked: the provider constructs the pinned v22 client normally, retaining RetryCount=0 and its pooled generation cache.</remarks>
    internal BotClientProvider(BotRegistry registry, TelegramEndpointRuntimeGate endpointGate,
        TelegramEndpointRoutingOptions endpointOptions, HttpMessageHandler transport, LatencyTelemetryService telemetry = null)
        : this(registry, telemetry, endpointGate, endpointOptions)
    {
        _endpointTestTransport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <summary>
    /// Creates a provider with an alternate Telegram client factory for transport-level lifecycle verification.
    /// </summary>
    /// <param name="registry">Runtime registry that remains authoritative for bot ids and current tokens.</param>
    /// <param name="clientFactory">
    /// Factory that receives the resolved bot configuration and returns a client for that bot. It must not share a
    /// client across different bot tokens. Production uses the public constructor; tests may invoke this constructor
    /// through reflection so invalidation still recreates the controlled transport.
    /// </param>
    /// <remarks>
    /// This constructor is internal so dependency injection continues selecting the production constructor. It does
    /// not change caching: one client remains cached per internal bot id until <see cref="Invalidate"/> is called.
    /// </remarks>
    internal BotClientProvider(
        BotRegistry registry,
        Func<BotInstanceConfig, ITelegramBotClient> clientFactory)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        PollingTelemetry = new TelegramPollingTelemetryTracker();
    }

    /// <summary>Creates one cached v22 SDK client over the provider-owned shared socket pool.</summary>
    /// <param name="bot">Resolved canonical bot configuration; its token is used only by the SDK.</param>
    /// <returns>A client whose lifetime remains shared with receivers and background delivery.</returns>
    /// <remarks>
    /// The lightweight handler is not disposed on invalidation: doing so could cancel already admitted
    /// background sends. It owns no socket resources; the provider disposes the shared pool at host shutdown.
    /// </remarks>
    /// <example><code>var client = CreateClient(bot);</code></example>
    private ITelegramBotClient CreateClient(BotInstanceConfig bot)
    {
        var httpClient = new HttpClient(new TelegramTelemetryHttpHandler(bot.Id, _telemetry, _transport), disposeHandler: false);
        return new TelegramTelemetryBotClient(new TelegramBotClientOptions(bot.Token) { RetryCount = 0 },
            httpClient, bot.Id, _telemetry, PollingTelemetry);
    }

    /// <summary>Creates an ordinary reusable facade whose next operation resolves the active exact endpoint generation.</summary>
    /// <param name="bot">Exact registry configuration with a valid BotFather token.</param>
    /// <returns>A shared facade safe for existing background workers to retain across endpoint migrations.</returns>
    /// <remarks>Endpoint selection is leased atomically per operation; ambiguous requests are never replayed.</remarks>
    /// <example><code>var client = CreateRoutingClient(bot);</code></example>
    private ITelegramBotClient CreateRoutingClient(BotInstanceConfig bot)
    {
        var identity = TelegramBotTokenIdentity.ExtractBotId(bot.Token)
            ?? throw new BotTransportUnavailableException("invalid_bot_identity");
        return new EndpointRoutedTelegramBotClient(bot.Id, identity, _endpointGate,
            route => GetEndpointSdk(bot.Id, route), _endpointOptions, _telemetry);
    }

    /// <summary>Resolves a pooled SDK epoch after the caller has admitted an ordinary or explicit control operation.</summary>
    /// <param name="botId">Exact configured canonical internal bot id.</param>
    /// <param name="route">Admitted positive BotFather identity and endpoint generation.</param>
    /// <returns>A cached SDK over the shared socket pool; no HttpClient is constructed per request.</returns>
    /// <exception cref="BotTransportUnavailableException">The registry identity changed before transport resolution.</exception>
    /// <remarks>At most two SDK entries exist per bot. Replacing an entry never cancels an admitted send; the old SDK remains owned by its caller until completion.</remarks>
    /// <example><code>var sdk = GetEndpointSdk(bot.Id, admittedRoute);</code></example>
    private ITelegramBotClient GetEndpointSdk(string botId, TelegramEndpointRoute route)
    {
        var bot = _registry.GetById(botId);
        if (bot == null || !string.Equals(bot.Id, botId, StringComparison.OrdinalIgnoreCase) ||
            TelegramBotTokenIdentity.ExtractBotId(bot.Token) != route.TelegramBotId)
            throw new BotTransportUnavailableException("bot_identity_changed");
        lock (_syncRoot)
        {
            var key = (bot.Id, route.Endpoint);
            if (_endpointSdks.TryGetValue(key, out var existing) && existing.Generation == route.Generation &&
                string.Equals(existing.Token, bot.Token, StringComparison.Ordinal)) return existing.Client;
            var client = CreateEndpointSdk(bot, route);
            _endpointSdks[key] = new(route.Generation, bot.Token, client);
            return client;
        }
    }

    /// <summary>Constructs one cached SDK using the existing shared socket pool and unchanged retry/timeout policy.</summary>
    /// <param name="bot">Exact configured or authority-verified probe identity; token remains private and must never be logged.</param>
    /// <param name="route">Trusted endpoint and generation admitted by the caller, never a user URL.</param>
    /// <returns>The real SDK or isolated fake transport factory result; caller retains it while an operation is outstanding.</returns>
    /// <remarks>Cloud passes null baseUrl to preserve LocalBotServer=false. Local alone supplies the validated loopback origin.</remarks>
    private ITelegramBotClient CreateEndpointSdk(BotInstanceConfig bot, TelegramEndpointRoute route)
    {
        if (_endpointFactory != null) return _endpointFactory(bot, route);
        var http = new HttpClient(new TelegramTelemetryHttpHandler(bot.Id, _telemetry, _endpointTestTransport ?? _transport), disposeHandler: false);
        var options = new TelegramBotClientOptions(bot.Token, route.Endpoint == TelegramEndpointType.Local ? _endpointOptions.LocalBaseUrl : null)
        { RetryCount = 0 };
        return new TelegramTelemetryBotClient(options, http, bot.Id, _telemetry, PollingTelemetry);
    }

    /// <summary>Resolves a bounded pooled SDK for a same-identity replacement token or a proven-safe fresh Cloud probe.</summary>
    /// <param name="botId">Canonical registered id, or fixed token_probe label for an unregistered identity.</param>
    /// <param name="token">Required unlogged BotFather secret, at most 512 characters, whose identity was verified by the caller.</param>
    /// <param name="route">Authority-admitted endpoint/generation; registered identities are rechecked before construction.</param>
    /// <param name="registered">True for a current registry identity; false only under counted same-gate identity admission after historical cleanup/cooldown authority was checked.</param>
    /// <returns>A shared read-only probe SDK; eviction never cancels an admitted request.</returns>
    /// <remarks>At most 128 secret-fingerprint entries are retained across bots, tokens and generations, using the same socket transport as ordinary clients.</remarks>
    /// <exception cref="BotTransportUnavailableException">Registered or supplied numeric identity no longer matches.</exception>
    private ITelegramBotClient GetProbeSdk(string botId, string token, TelegramEndpointRoute route, bool registered)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (token.Length > 512 || TelegramBotTokenIdentity.ExtractBotId(token) != route.TelegramBotId)
            throw new BotTransportUnavailableException("invalid_bot_identity");
        var current = registered ? _registry.GetById(botId) : null;
        if (registered && (current == null || !string.Equals(current.Id, botId, StringComparison.OrdinalIgnoreCase) ||
            TelegramBotTokenIdentity.ExtractBotId(current.Token) != route.TelegramBotId))
            throw new BotTransportUnavailableException("bot_identity_changed");
        if (registered && string.Equals(current.Token, token, StringComparison.Ordinal))
            return _endpointGate == null ? GetClientForCapabilityProbe(botId) : GetEndpointSdk(botId, route);
        Span<byte> bytes = stackalloc byte[Encoding.UTF8.GetMaxByteCount(token.Length)];
        var count = Encoding.UTF8.GetBytes(token, bytes);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(bytes[..count], digest);
        var key = (botId, route.Endpoint, route.Generation, Convert.ToHexString(digest));
        lock (_syncRoot)
        {
            if (_probeSdks.TryGetValue(key, out var cached)) return cached;
            var template = new BotInstanceConfig
            {
                Id = botId, Token = token, Type = current?.Type ?? BotInstanceTypes.Tenant,
                Enabled = current?.Enabled ?? false, Username = current?.Username
            };
            var client = _endpointGate == null ? _clientFactory(template) : CreateEndpointSdk(template, route);
            if (_probeSdks.Count >= 128)
            {
                using var oldest = _probeSdks.GetEnumerator();
                oldest.MoveNext();
                _probeSdks.Remove(oldest.Current.Key);
            }
            _probeSdks.Add(key, client);
            return client;
        }
    }

    /// <summary>Reads an authority-approved unregistered token through the shared Cloud pool under counted numeric-identity admission.</summary>
    /// <param name="token">Required BotFather secret, never persisted or logged; caller must prove no Local, uncertain, or cooldown alias exists.</param>
    /// <param name="admission">Active lease from this provider's exact runtime gate, acquired before durable and current-registration authority reads.</param>
    /// <param name="cancellationToken">Existing registration/owner probe budget, forwarded unchanged.</param>
    /// <returns>The SDK-authenticated identity for internal registration; never permission to bypass subsequent saved routing.</returns>
    /// <remarks>Only TelegramTokenProbe invokes this read-only seam after historical-state checks. The caller retains admission through the complete SDK await and final authority check, so concurrent registration cannot log out this identity mid-probe. No polling, logout, unrestricted client, or retry is introduced.</remarks>
    /// <exception cref="BotTransportUnavailableException">The supplied identity or its active shared-gate admission is invalid.</exception>
    /// <example><code>var identity = await clients.ProbeUnregisteredTokenAsync(token, admission, cancellationToken);</code></example>
    internal async Task<Telegram.Bot.Types.User> ProbeUnregisteredTokenAsync(string token,
        TelegramEndpointRuntimeGate.IdentityProbeLease admission, CancellationToken cancellationToken)
    {
        var identity = TelegramBotTokenIdentity.ExtractBotId(token) ?? throw new BotTransportUnavailableException("invalid_bot_identity");
        if (_endpointGate == null || admission == null || !admission.IsActiveFor(_endpointGate, identity))
            throw new BotTransportUnavailableException("endpoint_identity_authority_unavailable");
        var route = new TelegramEndpointRoute("token_probe", identity, TelegramEndpointType.Cloud, 1, true, TelegramEndpointMigrationState.Cloud);
        using var context = IsTelemetryEnabled ? TelegramEndpointTelemetryContext.Push(route.Endpoint, route.Generation, route.MigrationState) : null;
        return await GetProbeSdk(route.BotId, token, route, registered: false).GetMe(cancellationToken);
    }

    /// <summary>Admits an enabled owned bot's current Cloud route and returns its pooled SDK for one logger-channel incident.</summary>
    /// <param name="botId">Exact canonical owned registry id; default-id fallback is prohibited.</param>
    /// <param name="expectedTelegramBotId">Positive BotFather identity captured from the current registry token.</param>
    /// <param name="expectedToken">Exact unlogged registry secret captured before admission; token rotation invalidates this selection.</param>
    /// <returns>The shared Cloud SDK and an owned ordinary request lease that the caller must retain through reads, the durable send boundary and completion.</returns>
    /// <remarks>This is not a migration control bypass. Unknown, unhydrated, Local, fenced or migrating routes cannot probe Cloud. The counted lease prevents logout until the complete incident attempt ends; CloudRecovered is eligible regardless of older Local history.</remarks>
    /// <exception cref="BotTransportUnavailableException">The shared gate or exact enabled owned Cloud identity is unavailable.</exception>
    /// <example><code>var delivery = clients.GetCloudNotificationClient(bot.Id, identity, bot.Token); using var lease = delivery.Lease;</code></example>
    internal (ITelegramBotClient Client, TelegramEndpointRuntimeGate.RequestLease Lease) GetCloudNotificationClient(
        string botId, long expectedTelegramBotId, string expectedToken)
    {
        if (_endpointGate == null) throw new BotTransportUnavailableException("notification_gate_unavailable");
        var bot = _registry.GetById(botId);
        if (bot == null || !string.Equals(bot.Id, botId, StringComparison.Ordinal) || !bot.Enabled ||
            bot.Type != BotInstanceTypes.Owned || bot.IsSalesAssistant ||
            TelegramBotTokenIdentity.ExtractBotId(bot.Token) != expectedTelegramBotId ||
            !string.Equals(bot.Token, expectedToken, StringComparison.Ordinal))
            throw new BotTransportUnavailableException("notification_bot_unavailable");
        var lease = _endpointGate.AcquireRequest(botId, expectedTelegramBotId);
        try
        {
            var route = lease.Route;
            if (!route.Available || route.Endpoint != TelegramEndpointType.Cloud ||
                route.MigrationState is not (TelegramEndpointMigrationState.Cloud or TelegramEndpointMigrationState.CloudRecovered))
                throw new BotTransportUnavailableException("notification_cloud_unavailable");
            var client = GetEndpointSdk(botId, route);
            bot = _registry.GetById(botId);
            if (bot == null || !string.Equals(bot.Id, botId, StringComparison.Ordinal) || !bot.Enabled ||
                bot.Type != BotInstanceTypes.Owned || bot.IsSalesAssistant ||
                !string.Equals(bot.Token, expectedToken, StringComparison.Ordinal))
                throw new BotTransportUnavailableException("notification_bot_changed");
            return (client, lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <summary>Creates a narrowly scoped endpoint transport only for migration probes/logout.</summary>
    /// <param name="botId">Exact configured bot id; default-bot fallback is forbidden.</param>
    /// <param name="endpoint">Trusted explicit Cloud or Local destination enum.</param>
    /// <param name="generation">Positive operation epoch used to correlate the control request.</param>
    /// <param name="expectedTelegramBotId">Required positive exact BotFather identity from durable migration state; a replaced registry token is rejected before dispatch.</param>
    /// <returns>A pooled identity-bound control view; never pass it to customer handlers or general background workers.</returns>
    /// <exception cref="BotTransportUnavailableException">The exact bot is absent, disabled, or token identity is invalid.</exception>
    /// <remarks>This bypass is necessary only while ordinary admissions are fenced. The coordinator persists intent before logOut and never repeats an ambiguous irreversible call. Logger incidents instead use counted normal Cloud admission through GetCloudNotificationClient.</remarks>
    /// <example><code>var control = provider.CreateEndpointControlClient(bot.Id, TelegramEndpointType.Cloud, state.Generation, state.TelegramBotId);</code></example>
    public ITelegramBotClient CreateEndpointControlClient(string botId, TelegramEndpointType endpoint, long generation, long expectedTelegramBotId)
    {
        var bot = _registry.GetById(botId);
        var identity = TelegramBotTokenIdentity.ExtractBotId(bot?.Token);
        if (bot == null || !string.Equals(bot.Id, botId, StringComparison.OrdinalIgnoreCase) || !bot.Enabled ||
            !identity.HasValue || identity.Value != expectedTelegramBotId || generation < 1)
            throw new BotTransportUnavailableException("control_bot_unavailable");
        // Existing deterministic factories remain valid for control protocol tests.
        if (_endpointGate == null) return GetClient(bot.Id, identity.Value);
        var state = _endpointGate.GetRoute(bot.Id, identity.Value).MigrationState;
        var route = new TelegramEndpointRoute(bot.Id, identity.Value, endpoint, generation, false, state);
        return new EndpointRoutedTelegramBotClient(bot.Id, identity.Value, null,
            selected => GetEndpointSdk(bot.Id, selected), _endpointOptions, _telemetry, control: route);
    }

    /// <summary>Creates a pinned current-generation receiver view, rejecting requests from an obsolete polling generation.</summary>
    /// <param name="botId">Exact configured internal bot id.</param>
    /// <returns>A receiver-only view permitted during strict staged startup, or the original client when routing is not installed.</returns>
    /// <remarks>Only the runtime owns this view; ordinary callbacks use the reusable normal facade so receiver privileges cannot escape into business execution.</remarks>
    /// <exception cref="BotTransportUnavailableException">The exact identity or generation is unavailable.</exception>
    public ITelegramBotClient GetEndpointReceiverClient(string botId)
    {
        if (_endpointGate == null) return GetClient(botId);
        var bot = _registry.GetById(botId);
        var identity = TelegramBotTokenIdentity.ExtractBotId(bot?.Token);
        if (bot == null || !string.Equals(bot.Id, botId, StringComparison.OrdinalIgnoreCase) || !identity.HasValue)
            throw new BotTransportUnavailableException("receiver_bot_unavailable");
        var route = _endpointGate.GetRoute(bot.Id, identity.Value);
        return new EndpointRoutedTelegramBotClient(bot.Id, identity.Value, _endpointGate,
            selected => GetEndpointSdk(bot.Id, selected), _endpointOptions, _telemetry,
            generation: route.Generation, receiver: true);
    }

    /// <summary>Fixed-size cached SDK ownership for one bot/endpoint; the token is private and never serialized.</summary>
    private sealed class EndpointSdk
    {
        /// <summary>Positive cached endpoint epoch.</summary>
        internal long Generation { get; }
        /// <summary>Current secret used only for ordinal invalidation; never serialized or formatted by a record ToString.</summary>
        internal string Token { get; }
        /// <summary>SDK owned by callers through completion of already admitted operations.</summary>
        internal ITelegramBotClient Client { get; }
        /// <summary>Retains one SDK epoch without record-generated secret formatting.</summary>
        /// <param name="generation">Positive endpoint generation.</param><param name="token">Required private BotFather secret; never log it.</param>
        /// <param name="client">Real cached SDK or deterministic fixture transport.</param>
        internal EndpointSdk(long generation, string token, ITelegramBotClient client) { Generation = generation; Token = token; Client = client; }
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
        => GetClientCore(botId, expectedTelegramBotId: null);

    /// <summary>Gets the current transport only when it still belongs to the recorded BotFather identity.</summary>
    /// <param name="botId">Persisted internal bot id of the historical work item.</param>
    /// <param name="expectedTelegramBotId">Immutable numeric Telegram bot id captured when that work was created.</param>
    /// <returns>The cached or newly created client for the same BotFather bot, including after secret-token rotation.</returns>
    /// <exception cref="BotTransportUnavailableException">Thrown when the bot is unavailable or now identifies a different Telegram bot.</exception>
    public ITelegramBotClient GetClient(string botId, long expectedTelegramBotId)
        => GetClientCore(botId, expectedTelegramBotId);

    /// <summary>Resolves an exact bot transport for read-only capability checks before a tenant is enabled.</summary>
    /// <param name="botId">
    /// Required persisted internal bot id selected by an authorized owner workflow, not a Telegram bot, user or chat id.
    /// Empty ids are rejected rather than falling back to the default owned bot.
    /// </param>
    /// <returns>A read-only view for that bot's current active endpoint/token; the caller must not dispose it or use it for delivery.</returns>
    /// <remarks>
    /// Use only for bounded identity and channel-capability lookups after ownership has been checked. Disabled tenants
    /// are allowed solely for this pre-activation path; ordinary and historical delivery still requires an enabled
    /// tenant. This method changes neither registry settings nor receiver state, and never substitutes another bot.
    /// </remarks>
    /// <exception cref="ArgumentException">The internal bot id is null, empty or whitespace.</exception>
    /// <exception cref="BotTransportUnavailableException">The exact bot is missing or has no configured token.</exception>
    /// <exception cref="InvalidOperationException">The configured client factory returned no transport.</exception>
    /// <example><code>var client = provider.GetClientForCapabilityProbe(tenant.Id);</code></example>
    internal ITelegramBotClient GetClientForCapabilityProbe(string botId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botId);
        return GetClientCore(botId, expectedTelegramBotId: null, allowDisabledTenant: true);
    }

    /// <summary>Probes a replacement secret for the same current BotFather identity through its admitted endpoint, without enabling delivery.</summary>
    /// <param name="botId">Required exact current registry id, not a customer supplied username.</param>
    /// <param name="token">Required trimmed replacement/current BotFather secret; never logged, and its numeric identity must match the registry.</param>
    /// <param name="expectedTelegramBotId">Required positive currently registered BotFather numeric identity.</param>
    /// <returns>A read-only routed facade permitting getMe/chat/admin capability reads only; normal fences remain authoritative.</returns>
    /// <remarks>The supplied token is held in a bounded pooled SDK probe cache. This is not a migration/control bypass or a delivery capability.</remarks>
    /// <exception cref="BotTransportUnavailableException">Current or supplied identity changed, is unknown or lacks a token.</exception>
    internal ITelegramBotClient GetClientForCapabilityProbe(string botId, string token, long expectedTelegramBotId)
    {
        var bot = _registry.GetById(botId);
        if (bot == null || !string.Equals(bot.Id, botId, StringComparison.OrdinalIgnoreCase) || expectedTelegramBotId <= 0 ||
            TelegramBotTokenIdentity.ExtractBotId(bot.Token) != expectedTelegramBotId ||
            TelegramBotTokenIdentity.ExtractBotId(token) != expectedTelegramBotId)
            throw new BotTransportUnavailableException("bot_identity_changed");
        if (_endpointGate == null)
        {
            var route = new TelegramEndpointRoute(bot.Id, expectedTelegramBotId, TelegramEndpointType.Cloud, 1, true, TelegramEndpointMigrationState.Cloud);
            return GetProbeSdk(bot.Id, token, route, registered: true);
        }
        return new EndpointRoutedTelegramBotClient(bot.Id, expectedTelegramBotId, _endpointGate,
            route => GetProbeSdk(bot.Id, token, route, registered: true), _endpointOptions, _telemetry, probe: true);
    }

    /// <summary>Resolves one exact cached transport while preserving runtime availability and historical identity guards.</summary>
    /// <param name="botId">Internal registry bot id; only ordinary default-bot callers may omit it.</param>
    /// <param name="expectedTelegramBotId">Optional immutable BotFather numeric id captured by historical work.</param>
    /// <param name="allowDisabledTenant">True only for an authorized pre-activation capability probe, never for delivery.</param>
    /// <returns>The cached or newly created client belonging to the resolved registry bot; its lifetime remains shared.</returns>
    /// <remarks>Availability and identity are checked before reading the cache, so a prior probe cannot unlock delivery. Disabled-tenant probes return a separate read-only facade, not the delivery cache entry.</remarks>
    /// <exception cref="BotTransportUnavailableException">The bot, token, enabled state or required identity is unavailable.</exception>
    /// <exception cref="InvalidOperationException">The configured client factory returned no transport.</exception>
    /// <example><code>var client = GetClientCore(botId, expectedTelegramBotId);</code></example>
    private ITelegramBotClient GetClientCore(string botId, long? expectedTelegramBotId, bool allowDisabledTenant = false)
    {
        var requestedBotId = botId?.Trim();
        var bot = _registry.GetById(requestedBotId);
        if (!string.IsNullOrWhiteSpace(requestedBotId) &&
            (bot == null || !string.Equals(bot.Id, requestedBotId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new BotTransportUnavailableException("bot_not_found");
        }

        if (bot == null || string.IsNullOrWhiteSpace(bot.Token) ||
            (string.Equals(bot.Type, BotInstanceTypes.Tenant, StringComparison.OrdinalIgnoreCase) &&
             !bot.Enabled && !allowDisabledTenant))
        {
            throw new BotTransportUnavailableException("bot_disabled_or_token_missing");
        }

        if (expectedTelegramBotId.HasValue &&
            TelegramBotTokenIdentity.ExtractBotId(bot.Token) != expectedTelegramBotId.Value)
        {
            throw new BotTransportUnavailableException("bot_identity_changed");
        }

        if (allowDisabledTenant && _endpointGate != null)
        {
            var identity = TelegramBotTokenIdentity.ExtractBotId(bot.Token)
                ?? throw new BotTransportUnavailableException("invalid_bot_identity");
            return new EndpointRoutedTelegramBotClient(bot.Id, identity, _endpointGate,
                route => GetEndpointSdk(bot.Id, route), _endpointOptions, _telemetry, probe: true);
        }

        lock (_syncRoot)
        {
            if (_clients.TryGetValue(bot.Id, out var existing) &&
                (_endpointGate == null || existing.BotId == TelegramBotTokenIdentity.ExtractBotId(bot.Token)))
                return existing;

            var created = _clientFactory(bot);
            if (created == null)
                throw new InvalidOperationException("The Telegram client factory returned null.");
            _clients[bot.Id] = created;
            return created;
        }
    }

    /// <summary>
    /// Removes a cached client so a changed token will be used on the next request.
    /// </summary>
    /// <param name="botId">BotId whose client should be recreated.</param>
    /// <remarks>Active receiver telemetry is removed. Existing in-flight sends keep their original transport; only the next lookup recreates the cached SDK client.</remarks>
    public void Invalidate(string botId)
    {
        if (string.IsNullOrWhiteSpace(botId))
            return;

        lock (_syncRoot)
            _clients.Remove(botId);
        PollingTelemetry.Stopped(botId);
    }

    /// <summary>Releases the shared socket pool when the application service provider is disposed.</summary>
    /// <remarks>Client invalidation never calls this method, so rotating one bot cannot cancel another bot's admitted sends.</remarks>
    public void Dispose() => _transport.Dispose();
}

/// <summary>
/// Read-only runtime status row for one Telegram bot receiver.
/// </summary>
/// <remarks>
/// Instances are built from in-memory status plus <see cref="BotRegistry" /> configuration. They are safe to show
/// to super-admins because they include only masked operational state and never expose bot tokens.
/// </remarks>
public sealed class BotRuntimeStatusSnapshot
{
    /// <summary>
    /// Internal bot id used by the runtime registry and bot-scoped state tables.
    /// </summary>
    public string BotId { get; init; }

    /// <summary>
    /// Public Telegram username configured or returned by Telegram, without relying on the token secret.
    /// </summary>
    public string Username { get; init; }

    /// <summary>
    /// Brand name shown in configuration or tenant settings.
    /// </summary>
    public string BrandName { get; init; }

    /// <summary>
    /// Bot ownership type such as owned, tenant, or sales assistant.
    /// </summary>
    public string BotType { get; init; }

    /// <summary>
    /// Telegram user id of the tenant owner when this row belongs to a tenant storefront.
    /// </summary>
    public long? OwnerTelegramUserId { get; init; }

    /// <summary>
    /// Indicates whether the bot is enabled in configuration or users.db.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Indicates whether a token is present. The token value itself is never exposed.
    /// </summary>
    public bool HasToken { get; init; }

    /// <summary>
    /// Indicates whether this process currently has a registered getUpdates receiver for the bot.
    /// </summary>
    public bool IsReceiverRunning { get; init; }

    /// <summary>
    /// Short machine-readable status label recorded by startup, retry, stop, or cleanup paths.
    /// </summary>
    public string Status { get; init; }

    /// <summary>
    /// Last non-secret startup or polling error message associated with this bot, if any.
    /// </summary>
    public string LastError { get; init; }

    /// <summary>
    /// UTC time when this status row was last updated by the runtime.
    /// </summary>
    public DateTime? UpdatedAtUtc { get; init; }
}

/// <summary>
/// Stores process-local receiver health for all configured and tenant Telegram bots.
/// </summary>
/// <remarks>
/// This store is intentionally memory-only. It answers super-admin status requests and keeps status updates out of
/// <c>users.db</c>, while <see cref="BotRegistry" /> remains the source of truth for configuration, ownership,
/// enabled flags, and token presence.
/// </remarks>
public sealed class BotRuntimeStatusStore
{
    private readonly Dictionary<string, RuntimeStatusState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _syncRoot = new();

    /// <summary>
    /// Marks a bot as skipped during startup because it is disabled, has no token, or was not found exactly.
    /// </summary>
    /// <param name="bot">Runtime bot configuration from <see cref="BotRegistry" />.</param>
    /// <param name="reason">Non-secret reason shown to super-admins, such as disabled or missing token.</param>
    public void MarkSkipped(BotInstanceConfig bot, string reason)
    {
        Upsert(bot, isReceiverRunning: false, status: "skipped", lastError: reason);
    }

    /// <summary>
    /// Marks a bot as listening after its Telegram receiver has been started.
    /// </summary>
    /// <param name="bot">Runtime bot configuration from <see cref="BotRegistry" />.</param>
    /// <param name="telegramUsername">Username returned by Telegram <c>GetMe</c>, when available.</param>
    public void MarkStarted(BotInstanceConfig bot, string telegramUsername)
    {
        Upsert(bot, isReceiverRunning: true, status: "listening", lastError: null, usernameOverride: telegramUsername);
    }

    /// <summary>
    /// Marks a receiver as registered while Telegram identity and command initialization continues in the background.
    /// </summary>
    /// <param name="bot">Runtime bot configuration whose receiver has been registered by this process.</param>
    /// <param name="reason">Non-secret transient reason that prevented the startup probe from completing immediately.</param>
    /// <remarks>
    /// The receiver is already active in this state. This status must not be interpreted as a failed startup or used
    /// to launch another receiver for the same bot id.
    /// </remarks>
    public void MarkInitializing(BotInstanceConfig bot, string reason)
    {
        Upsert(bot, isReceiverRunning: true, status: "initializing", lastError: reason);
    }

    /// <summary>
    /// Marks a registered receiver as operational but temporarily unable to finish Telegram metadata initialization.
    /// </summary>
    /// <param name="bot">Runtime bot configuration whose receiver remains registered and running.</param>
    /// <param name="reason">Non-secret transient Telegram or network error from the latest background attempt.</param>
    /// <remarks>
    /// A degraded bot is not stopped. Later background retries can promote it to <c>listening</c> without creating a
    /// second receiver.
    /// </remarks>
    public void MarkDegraded(BotInstanceConfig bot, string reason)
    {
        Upsert(bot, isReceiverRunning: true, status: "degraded", lastError: reason);
    }

    /// <summary>
    /// Marks a bot startup as failed without changing its configured enabled flag.
    /// </summary>
    /// <param name="bot">Runtime bot configuration from <see cref="BotRegistry" />.</param>
    /// <param name="status">Short failure status, for example duplicate, invalid_token, or startup_failed.</param>
    /// <param name="error">Non-secret error message. Never pass a raw bot token here.</param>
    public void MarkFailed(BotInstanceConfig bot, string status, string error)
    {
        Upsert(bot, isReceiverRunning: false, status: status, lastError: error);
    }

    /// <summary>
    /// Marks a bot receiver as stopped by runtime cleanup, shutdown, or getUpdates conflict handling.
    /// </summary>
    /// <param name="botId">Internal runtime bot id whose receiver was stopped.</param>
    /// <param name="reason">Non-secret stop reason shown in the super-admin status view.</param>
    public void MarkStopped(string botId, string reason)
    {
        if (string.IsNullOrWhiteSpace(botId))
            return;

        lock (_syncRoot)
        {
            var state = GetOrCreateState(botId);
            state.IsReceiverRunning = false;
            state.Status = "stopped";
            state.LastError = reason;
            state.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Returns status snapshots for all known runtime bots.
    /// </summary>
    /// <param name="bots">Current bot registry snapshot. Each bot contributes configuration and token/owner metadata.</param>
    /// <returns>
    /// A non-null list ordered by bot type and id. Missing runtime state is inferred as disabled, missing-token, or
    /// not-started depending on the current configuration.
    /// </returns>
    public IReadOnlyList<BotRuntimeStatusSnapshot> GetSnapshots(IEnumerable<BotInstanceConfig> bots)
    {
        var now = DateTime.UtcNow;
        lock (_syncRoot)
        {
            return (bots ?? Enumerable.Empty<BotInstanceConfig>())
                .Where(bot => bot != null)
                .OrderBy(bot => bot.Type)
                .ThenBy(bot => bot.Id, StringComparer.OrdinalIgnoreCase)
                .Select(bot =>
                {
                    _states.TryGetValue(bot.Id, out var state);
                    var inferredStatus = !bot.Enabled
                        ? "disabled"
                        : string.IsNullOrWhiteSpace(bot.Token)
                            ? "missing_token"
                            : "not_started";

                    return new BotRuntimeStatusSnapshot
                    {
                        BotId = bot.Id,
                        Username = string.IsNullOrWhiteSpace(state?.Username) ? bot.Username : state.Username,
                        BrandName = bot.BrandName,
                        BotType = bot.Type,
                        OwnerTelegramUserId = bot.OwnerTelegramUserId,
                        Enabled = bot.Enabled,
                        HasToken = !string.IsNullOrWhiteSpace(bot.Token),
                        IsReceiverRunning = state?.IsReceiverRunning == true,
                        Status = state?.Status ?? inferredStatus,
                        LastError = state?.LastError,
                        UpdatedAtUtc = state?.UpdatedAtUtc ?? now
                    };
                })
                .ToList();
        }
    }

    /// <summary>
    /// Creates or updates one status row under the store lock.
    /// </summary>
    /// <param name="bot">Runtime bot configuration whose status is changing.</param>
    /// <param name="isReceiverRunning">Whether the process currently owns a receiver for this bot.</param>
    /// <param name="status">Short machine-readable status label.</param>
    /// <param name="lastError">Optional non-secret error text.</param>
    /// <param name="usernameOverride">Optional username returned by Telegram during startup.</param>
    private void Upsert(
        BotInstanceConfig bot,
        bool isReceiverRunning,
        string status,
        string lastError,
        string usernameOverride = null)
    {
        if (bot == null || string.IsNullOrWhiteSpace(bot.Id))
            return;

        lock (_syncRoot)
        {
            var state = GetOrCreateState(bot.Id);
            state.Username = string.IsNullOrWhiteSpace(usernameOverride) ? bot.Username : usernameOverride.Trim().TrimStart('@');
            state.IsReceiverRunning = isReceiverRunning;
            state.Status = status;
            state.LastError = lastError;
            state.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Gets the mutable status row for a bot id, creating it when no runtime event has been recorded yet.
    /// </summary>
    /// <param name="botId">Internal runtime bot id.</param>
    /// <returns>Mutable in-memory state owned by this store and protected by <c>_syncRoot</c>.</returns>
    private RuntimeStatusState GetOrCreateState(string botId)
    {
        if (!_states.TryGetValue(botId, out var state))
        {
            state = new RuntimeStatusState();
            _states[botId] = state;
        }

        return state;
    }

    /// <summary>
    /// Mutable in-memory status state stored under the lock before projection to snapshots.
    /// </summary>
    private sealed class RuntimeStatusState
    {
        /// <summary>
        /// Last known public username for the bot, without a leading at-sign.
        /// </summary>
        public string Username { get; set; }

        /// <summary>
        /// Whether the receiver is currently registered in the running process.
        /// </summary>
        public bool IsReceiverRunning { get; set; }

        /// <summary>
        /// Short status label recorded by the latest runtime event.
        /// </summary>
        public string Status { get; set; }

        /// <summary>
        /// Latest non-secret error or stop reason.
        /// </summary>
        public string LastError { get; set; }

        /// <summary>
        /// UTC time when the state was last changed.
        /// </summary>
        public DateTime UpdatedAtUtc { get; set; }
    }
}

/// <summary>
/// Hosted service that starts one Telegram receiver per enabled bot.
/// All receivers dispatch updates into the shared TelegramBotService with a bot-specific runtime context.
/// </summary>
public class MultiBotHostedService : IHostedService, ITelegramEndpointReceiverLifecycle
{
    private readonly BotRegistry _registry;
    private readonly BotClientProvider _clientProvider;
    private readonly ITelegramUpdateScheduler _scheduler;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BotContextAccessor _botContextAccessor;
    private readonly BotRuntimeStatusStore _runtimeStatusStore;
    private readonly ILogger<MultiBotHostedService> _logger;
    /// <summary>Optional per-update receiver timeline tracker, shared with scheduler admission.</summary>
    private readonly UpdateTelemetryTracker _updateTelemetry;
    /// <summary>Optional writer used for an unpersisted admin-control handler measurement scope.</summary>
    private readonly LatencyTelemetryService _telemetry;
    /// <summary>Shared endpoint admission gate; null preserves original direct-constructor lifecycle tests.</summary>
    private readonly TelegramEndpointRuntimeGate _endpointGate;
    /// <summary>Authorized operator control path available from every healthy owned bot.</summary>
    private readonly TelegramEndpointAdminService _endpointAdmin;
    /// <summary>Coalesces route availability changes into the existing single tracked receiver-recovery lifetime.</summary>
    private readonly SemaphoreSlim _endpointRecoveryWake = new(0, 1);
    private readonly TimeSpan _startupProbeTimeout;
    private CancellationTokenSource _receivingCts;
    private readonly Dictionary<string, CancellationTokenSource> _botReceivers = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Tracked receiver generations; a replacement waits for its predecessor to exit.</summary>
    private readonly Dictionary<string, Task> _receiverTasks = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Live receiver, initialization, and recovery tasks observed through host shutdown.</summary>
    private readonly HashSet<Task> _backgroundTasks = new();
    private readonly Dictionary<string, SemaphoreSlim> _lifecycleGates = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Process-local deduplication set ensuring repeated callbacks from one webhook-conflicted receiver schedule only
    /// one stop/preflight/restart recovery task for that internal bot id.
    /// </summary>
    private readonly HashSet<string> _webhookConflictRecoveries = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Per-bot transient polling failure state used for bounded exponential backoff with jitter.
    /// </summary>
    /// <remarks>
    /// State is keyed by the internal runtime bot id, so a Telegram outage that hits every owned and tenant receiver at
    /// once produces independent delays per bot instead of one synchronized retry storm. This is process-local memory
    /// only and never touches the database, the Telegram log outbox, or bot configuration.
    /// </remarks>
    private readonly TelegramPollingBackoffTracker _transientPollingBackoff = new();
    private readonly object _syncRoot = new();

    /// <summary>
    /// Initial delay between background recovery passes for transient Telegram startup failures.
    /// </summary>
    private static readonly TimeSpan StartupRecoveryDelay = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Maximum bounded delay between persistent recovery passes for an enabled receiver that remains offline.
    /// </summary>
    private static readonly TimeSpan StartupRecoveryMaximumDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Creates the hosted receiver manager.
    /// </summary>
    /// <param name="registry">Registry of owned and tenant bots.</param>
    /// <param name="clientProvider">Telegram client provider.</param>
    /// <param name="scheduler">Durable bounded scheduler; callbacks await admission rather than full dispatch.</param>
    /// <param name="scopeFactory">
    /// Factory used to create short-lived scopes for users.db cleanup when a tenant token is revoked or duplicated.
    /// </param>
    /// <param name="botContextAccessor">
    /// Async-local bot context accessor used to attribute polling errors to the bot whose receiver failed.
    /// </param>
    /// <param name="runtimeStatusStore">
    /// Process-local status store used by the super-admin runtime status screen.
    /// </param>
    /// <param name="configuration">
    /// Application configuration containing the bounded Telegram startup probe timeout. Bot tokens are resolved
    /// through <paramref name="registry" /> and are never read or logged by this constructor.
    /// </param>
    /// <param name="logger">Logger for receiver lifecycle events.</param>
    /// <param name="telemetry">Optional shared writer for unpersisted admin-control scopes; transport clients use the provider's same writer.</param>
    /// <param name="updateTelemetry">Optional per-update receiver tracker; null preserves existing direct-constructor callers.</param>
    /// <param name="endpointGate">Optional shared routing fence; saved pending migrations never start an ordinary receiver.</param>
    /// <param name="endpointAdmin">Optional global-super-admin endpoint panel; customer/tenant callbacks cannot use it.</param>
    /// <remarks>The host tracks receiver, initialization, and recovery lifetimes; each replacement waits for the previous receiver generation to terminate.</remarks>
    public MultiBotHostedService(
        BotRegistry registry,
        BotClientProvider clientProvider,
        ITelegramUpdateScheduler scheduler,
        IServiceScopeFactory scopeFactory,
        BotContextAccessor botContextAccessor,
        BotRuntimeStatusStore runtimeStatusStore,
        IConfiguration configuration,
        ILogger<MultiBotHostedService> logger,
        LatencyTelemetryService telemetry = null,
        UpdateTelemetryTracker updateTelemetry = null,
        TelegramEndpointRuntimeGate endpointGate = null,
        TelegramEndpointAdminService endpointAdmin = null)
    {
        _registry = registry;
        _clientProvider = clientProvider;
        _scheduler = scheduler;
        _scopeFactory = scopeFactory;
        _botContextAccessor = botContextAccessor;
        _runtimeStatusStore = runtimeStatusStore;
        _updateTelemetry = updateTelemetry;
        _telemetry = telemetry;
        _endpointGate = endpointGate;
        _endpointAdmin = endpointAdmin;
        if (_endpointGate != null) _endpointGate.AvailabilityChanged += SignalEndpointRecovery;
        var appConfig = configuration.Get<AppConfig>() ?? new AppConfig();
        _startupProbeTimeout = TimeSpan.FromSeconds(Math.Clamp(appConfig.TelegramBotStartupProbeTimeoutSeconds, 5, 60));
        _logger = logger;
    }

    /// <summary>
    /// Starts receivers for all enabled bots known at application startup and schedules recovery for transient misses.
    /// </summary>
    /// <param name="cancellationToken">Host shutdown token.</param>
    /// <returns>A task that completes after the first startup pass has been requested.</returns>
    /// <remarks>
    /// Startup is deliberately not all-or-nothing. A bounded <c>GetMe</c> timeout starts one optimistic receiver and
    /// command setup continues in the background. The recovery loop handles only enabled bots that still have no
    /// registered receiver and continues with capped exponential backoff for the host lifetime; duplicate, disabled,
    /// and invalid-token decisions remain non-retryable. Per-bot lifecycle gates prevent startup recovery and owner
    /// actions from creating overlapping polling loops.
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _receivingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var nonRetryableBotIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var bot in _registry.Bots)
        {
            var result = await StartBotAttemptSerializedAsync(bot.Id, cancellationToken);
            if (IsNonRetryableStartupResult(result) && !(result == BotStartupResult.Skipped && _endpointGate != null && !_endpointGate.CanReceive(bot.Id)))
                nonRetryableBotIds.Add(bot.Id);
        }

        TrackBackgroundTask(Task.Run(
            () => RecoverMissingStartupReceiversAsync(nonRetryableBotIds, _receivingCts.Token),
            CancellationToken.None));
    }

    /// <summary>
    /// Starts receiving updates for one bot if it is enabled and has a usable token.
    /// </summary>
    /// <param name="botId">
    /// Internal runtime bot id from the registry. New storefronts use <c>tenant-{ownerId}-{storeNumber}</c>; legacy stores retain <c>tenant-{ownerId}</c>.
    /// This is a database identity, not the numeric Telegram bot id.
    /// </param>
    /// <param name="cancellationToken">Token that cancels waiting for the per-bot lifecycle gate and startup work.</param>
    /// <returns>
    /// <c>true</c> when the receiver is already running or successfully started; <c>false</c> when the bot is
    /// disabled, missing a token, duplicated, or rejected by Telegram.
    /// </returns>
    /// <remarks>
    /// This method is intentionally fail-soft. A revoked tenant token disables only that tenant row in users.db
    /// and never stops other owned or tenant bots from starting. Owned bot tokens come from configuration and are
    /// never modified automatically. Before each receiver generation, webhook absence is confirmed and an active
    /// webhook is removed without dropping pending updates. A webhook probe/removal failure does not start polling
    /// and remains eligible for the persistent, capped-backoff recovery loop. A transient <c>getMe</c> timeout may
    /// still start one optimistic receiver after that webhook preflight succeeds.
    /// </remarks>
    public async Task<bool> StartBotAsync(string botId, CancellationToken cancellationToken = default)
    {
        const int maxAttempts = 3;
        var lifecycleGate = GetLifecycleGate(botId);
        await lifecycleGate.WaitAsync(cancellationToken);

        try
        {
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var result = await StartBotCoreAsync(botId, cancellationToken);
                if (result == BotStartupResult.Started || result == BotStartupResult.AlreadyRunning)
                    return true;

                if (result != BotStartupResult.TransientFailure || attempt == maxAttempts)
                    return false;

                _logger.LogInformation(
                    "Retrying Telegram bot receiver after transient startup failure. botId={BotId}, attempt={Attempt}/{MaxAttempts}",
                    botId,
                    attempt + 1,
                    maxAttempts);

                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }

            return false;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Runs one receiver startup attempt under the per-bot lifecycle gate.
    /// </summary>
    /// <param name="botId">Internal registry bot id whose startup must be serialized.</param>
    /// <param name="cancellationToken">Token that cancels waiting for the gate and the startup attempt.</param>
    /// <returns>The exact startup classification returned by the serialized core attempt.</returns>
    /// <remarks>
    /// Application startup and background recovery use this helper. Owner-triggered startup uses
    /// <see cref="StartBotAsync" /> so its bounded retry sequence remains inside one uninterrupted lifecycle lease.
    /// </remarks>
    private async Task<BotStartupResult> StartBotAttemptSerializedAsync(
        string botId,
        CancellationToken cancellationToken)
    {
        var lifecycleGate = GetLifecycleGate(botId);
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            return await StartBotCoreAsync(botId, cancellationToken);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Starts one bot receiver and returns the exact startup classification needed by startup recovery.
    /// </summary>
    /// <param name="botId">
    /// Internal runtime bot id from <see cref="BotRegistry" />. This is not a Telegram numeric bot id and may
    /// represent an owned bot, sales-assistant bot, or tenant storefront bot.
    /// </param>
    /// <param name="cancellationToken">
    /// Token used for Telegram validation calls and database cleanup when startup is cancelled by the host.
    /// </param>
    /// <param name="strictEndpointValidation">True only for a staged migration destination: identity and short polling must validate without optimistic startup.</param>
    /// <returns>
    /// A <see cref="BotStartupResult" /> value describing whether a receiver started, was already running, should
    /// be skipped permanently, or failed in a way that can be retried by the persistent startup recovery loop.
    /// </returns>
    /// <remarks>
    /// This method is the single startup path for owned, tenant, and assistant bots and must be called while holding
    /// the corresponding lifecycle gate. It mutates tenant rows only when Telegram proves the token is invalid or
    /// duplicate-token protection chooses another bot. Webhook preflight must succeed before registration; only a
    /// transient <c>getMe</c> failure may use optimistic registration outside migration. Staged migration requires exact
    /// BotFather identity and a zero-timeout, non-dropping getUpdates proof before registration. Command setup and identity refresh continue in the background.
    /// Startup/receiver health and update reception emit only nonblocking metadata. Each update opens its
    /// receiver timeline before the bounded admin control path or durable admission; control-path completion
    /// is explicit and never creates a durable inbox receipt. SDK-validated polls own health recovery. The existing
    /// receiver-start log and tenant lifecycle message include the captured receiver generation's Cloud/Local origin;
    /// a staged migration start is explicitly not proof of final destination activation.
    /// </remarks>
    private async Task<BotStartupResult> StartBotCoreAsync(string botId, CancellationToken cancellationToken = default,
        bool strictEndpointValidation = false)
    {
        var bot = _registry.GetById(botId);
        if (bot == null || !string.Equals(bot.Id, botId, StringComparison.OrdinalIgnoreCase))
            return BotStartupResult.Skipped;

        if (!bot.Enabled || string.IsNullOrWhiteSpace(bot.Token))
        {
            _logger.LogInformation("Telegram bot receiver skipped. botId={BotId}, enabled={Enabled}, hasToken={HasToken}", bot.Id, bot.Enabled, !string.IsNullOrWhiteSpace(bot.Token));
            _runtimeStatusStore.MarkSkipped(bot, bot.Enabled ? "missing token" : "disabled by configuration");
            return BotStartupResult.Skipped;
        }
        if (_endpointGate != null && TelegramBotTokenIdentity.ExtractBotId(bot.Token) is long currentIdentity)
            await _endpointGate.HydrateAsync(bot.Id, currentIdentity, cancellationToken);
        if (!strictEndpointValidation && _endpointGate != null && !_endpointGate.CanReceive(bot.Id))
        {
            _runtimeStatusStore.MarkSkipped(bot, "endpoint migration pending");
            return BotStartupResult.Skipped;
        }


        lock (_syncRoot)
        {
            if (_botReceivers.ContainsKey(bot.Id))
                return BotStartupResult.AlreadyRunning;
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
                    cancellationToken,
                    lifecycleGateHeld: true);
            }
            else
            {
                _logger.LogCritical(
                    "Configured non-tenant Telegram bot token conflict. botId={BotId}, conflictBotId={ConflictBotId}, token={MaskedToken}",
                    bot.Id,
                    duplicate.ConflictBotId,
                    TelegramBotTokenIdentity.MaskToken(bot.Token));
            }

            _runtimeStatusStore.MarkFailed(bot, "duplicate", $"duplicate token conflict with {duplicate.ConflictBotId}");
            return BotStartupResult.DuplicateConflict;
        }

        await DisableTenantConflictsWonByAsync(bot, cancellationToken);

        CancellationTokenSource botCts = null;
        try
        {
            _clientProvider.PollingTelemetry.Startup(bot.Id, "starting");
            // Each bot receives with its own token but dispatches through the shared TelegramBotService.
            var parentToken = _receivingCts?.Token ?? cancellationToken;
            botCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            var receiverToken = botCts.Token;
            var client = _clientProvider.GetEndpointReceiverClient(bot.Id);
            var expectedBotIdentity = TelegramBotTokenIdentity.ExtractBotId(bot.Token);
            var receiverRoute = _endpointGate != null && expectedBotIdentity.HasValue ?
                _endpointGate.GetRoute(bot.Id, expectedBotIdentity.Value) : (TelegramEndpointRoute?)null;
            var endpointGeneration = receiverRoute?.Generation;
            var receiverEndpoint = receiverRoute?.Endpoint ?? TelegramEndpointType.Cloud;
            Telegram.Bot.Types.User me = null;
            Exception transientProbeError = null;

            await EnsureLongPollingWebhookClearedAsync(client, bot, cancellationToken);

            try
            {
                using var probeCts = CreateStartupProbeCancellation(cancellationToken);
                using var probeMetadata = _clientProvider.IsTelemetryEnabled ?
                    TelegramRequestCancellationScope.PushStartupProbe(cancellationToken, probeCts.Token) : null;
                me = await client.GetMe(probeCts.Token);
                if (strictEndpointValidation)
                {
                    if (!expectedBotIdentity.HasValue || me.Id != expectedBotIdentity.Value)
                        throw new BotTransportUnavailableException("destination_identity_mismatch");
                    // Offset zero does not confirm/remove this batch. The real receiver admits it durably before its next poll.
                    await client.GetUpdates(offset: 0, limit: 1, timeout: 0,
                        allowedUpdates: Array.Empty<UpdateType>(), cancellationToken: probeCts.Token);
                }
            }
            catch (Exception ex) when (!strictEndpointValidation && IsTelegramTransientStartupError(ex))
            {
                // A transient getMe failure does not prove the token is invalid. Register the receiver exactly once
                // and let polling plus background initialization establish connectivity without taking the bot offline.
                transientProbeError = ex;
            }


            Task previousReceiver;
            lock (_syncRoot) _receiverTasks.TryGetValue(bot.Id, out previousReceiver);
            await TelegramReceiverLifetime.ObservePreviousAsync(previousReceiver, cancellationToken);

            var receiverGeneration = _clientProvider.PollingTelemetry.Started(bot.Id);
            var receiverTask = client.ReceiveAsync(
                updateHandler: async (_, update, token) =>
                {
                    using var received = _updateTelemetry?.Receive(bot.Id, update.Id, update.Type.ToString());
                    _clientProvider.PollingTelemetry.ReceivedUpdate(bot.Id);
                    // The tracked receiver owns this bounded super-admin control path, which must remain usable
                    // when durable customer capacity is full. It never replays a terminal handler receipt.
                    var adminLatency = _telemetry?.Enabled == true ? TelegramUpdateLatencyScope.Push(
                        0, bot.Id, update.Id, TimeSpan.FromSeconds(2), null,
                        telemetry: _telemetry, traceId: received?.TraceId) : null;
                    bool handled;
                    try
                    {
                        handled = false;
                        if (_endpointGate == null || _endpointGate.CanReceive(bot.Id))
                        {
                            // Receiver privileges must never escape into operator/customer requests.
                            var updateClient = expectedBotIdentity.HasValue ?
                                _clientProvider.GetClient(bot.Id, expectedBotIdentity.Value) : _clientProvider.GetClient(bot.Id);
                            handled = _endpointAdmin != null && await _endpointAdmin.TryHandleAsync(bot.Id, updateClient, update, token);
                            if (!handled)
                            {
                                using var admin = _scopeFactory.CreateScope();
                                handled = await admin.ServiceProvider.GetRequiredService<TelegramInboxAdminService>()
                                    .TryHandleAsync(bot.Id, updateClient, update, token);
                            }
                        }
                    }
                    finally
                    {
                        adminLatency?.Dispose();
                    }
                    if (handled)
                    {
                        received?.CompleteControlPath(adminLatency?.CaptureTelemetry());
                        return;
                    }
                    await _scheduler.EnqueueAsync(bot.Id, update, token);
                },
                errorHandler: (_, exception, token) => token.IsCancellationRequested ||
                    !IsCurrentEndpointGeneration(bot.Id, expectedBotIdentity, endpointGeneration) ? Task.CompletedTask :
                    HandleBotPollingErrorAsync(bot.Id, exception, token),
                receiverOptions: new ReceiverOptions
                {
                    AllowedUpdates = Array.Empty<UpdateType>()
                },
                cancellationToken: botCts.Token);

            lock (_syncRoot)
            {
                _receiverTasks[bot.Id] = receiverTask;
                _botReceivers[bot.Id] = botCts;
            }
            TrackBackgroundTask(receiverTask);
            receiverTask.GetAwaiter().OnCompleted(() => CompleteReceiverGeneration(bot.Id, botCts, receiverTask, receiverGeneration));
            _clientProvider.PollingTelemetry.Startup(bot.Id, "completed");


            if (transientProbeError == null)
            {
                _runtimeStatusStore.MarkStarted(bot, me?.Username ?? bot.Username);
            }
            else
            {
                _runtimeStatusStore.MarkInitializing(bot, transientProbeError.Message);
                _logger.LogInformation(
                    "Telegram bot receiver started optimistically after a transient startup probe failure. botId={BotId}, error={Error}",
                    bot.Id,
                    transientProbeError.Message);
            }

            _logger.LogInformation(
                "Started Telegram bot receiver. botId={BotId}, username=@{Username}\nTelegram API: {Endpoint}{MigrationNotice}",
                bot.Id,
                me?.Username ?? bot.Username,
                TelegramEndpointPresentation.EndpointBadge(receiverEndpoint),
                strictEndpointValidation ? "\n⏳ مقصد در حال آماده‌سازی است؛ فعال‌سازی نهایی انتقال هنوز تأیید نشده است." : string.Empty);
            if (IsTenant(bot))
                LogTenantRuntimeEvent(
                    bot,
                    me?.Username ?? bot.Username,
                    strictEndpointValidation ? "گیرندهٔ مقصد شروع شد؛ انتقال هنوز نهایی نشده است" :
                        transientProbeError == null ? "روشن شد" : "روشن شد؛ در حال تکمیل اتصال",
                    null,
                    receiverEndpoint);

            TrackBackgroundTask(Task.Run(
                () => CompleteBotInitializationAsync(
                    bot.Id,
                    TelegramBotTokenIdentity.ExtractBotId(bot.Token),
                    endpointGeneration,
                    receiverToken),
                CancellationToken.None));

            return BotStartupResult.Started;
        }
        catch (Exception ex)
        {
            _clientProvider.PollingTelemetry.Startup(bot.Id, "failed", ex);
            // If a post-registration log/status action throws, remove only this exact CTS. A newer receiver
            // generation must never be removed by cleanup from an older failed start attempt.
            lock (_syncRoot)
            {
                if (botCts != null &&
                    _botReceivers.TryGetValue(bot.Id, out var registeredCts) &&
                    ReferenceEquals(registeredCts, botCts))
                {
                    _botReceivers.Remove(bot.Id);
                }
            }

            try { botCts?.Cancel(); } catch (ObjectDisposedException) { }
            botCts?.Dispose();
            _clientProvider.Invalidate(bot.Id);

            if (strictEndpointValidation)
            {
                // A destination-specific rejection is migration evidence, not authority to revoke a tenant's token.
                _runtimeStatusStore.MarkFailed(bot, "endpoint_receiver_not_ready", "destination receiver validation failed");
                _logger.LogWarning("Telegram endpoint receiver validation failed. Category={Category}", "receiver_not_ready");
                return BotStartupResult.TransientFailure;
            }

            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                return BotStartupResult.TransientFailure;

            if (IsTenant(bot) && IsTelegramTokenInvalidError(ex))
            {
                _runtimeStatusStore.MarkFailed(bot, "invalid_token", ex.Message);
                await DisableTenantTokenAsync(
                    bot,
                    ex.Message,
                    notifyOwner: true,
                    cancellationToken,
                    lifecycleGateHeld: true);
                return BotStartupResult.InvalidToken;
            }

            if (!IsTenant(bot) && IsTelegramTokenInvalidError(ex))
            {
                _runtimeStatusStore.MarkFailed(bot, "invalid_token", ex.Message);
                _logger.LogCritical(ex, "Configured Telegram bot receiver failed because Telegram rejected the token. botId={BotId}", bot.Id);
                return BotStartupResult.InvalidToken;
            }

            if (IsTelegramTransientStartupError(ex))
            {
                _runtimeStatusStore.MarkFailed(bot, "transient_startup_failed", ex.Message);
                _logger.LogInformation(
                    "Telegram bot receiver startup hit a transient Telegram/network error; persistent recovery remains active. botId={BotId}, errorType={ErrorType}",
                    bot.Id,
                    ex.GetType().Name);
                return BotStartupResult.TransientFailure;
            }

            _runtimeStatusStore.MarkFailed(bot, "startup_failed", ex.Message);
            if (IsTenant(bot))
            {
                _logger.LogError(ex, "Tenant Telegram bot receiver failed to start. botId={BotId}", bot.Id);
                LogTenantRuntimeEvent(bot, bot.Username, "خطا در روشن شدن", ex.Message, null);
            }
            else
            {
                _logger.LogCritical(ex, "Configured Telegram bot receiver failed to start. botId={BotId}", bot.Id);
            }

            return BotStartupResult.TransientFailure;
        }
    }

    /// <summary>
    /// Verifies that Telegram has no active webhook before one long-polling receiver generation starts.
    /// </summary>
    /// <param name="client">Telegram client already resolved for the current internal bot id.</param>
    /// <param name="bot">
    /// Runtime bot configuration used only for safe structured attribution. Its token and webhook URL are never
    /// logged by this method.
    /// </param>
    /// <param name="cancellationToken">Host or owner-operation token that cancels the bounded Telegram probes.</param>
    /// <returns>A task that completes only after webhook absence has been confirmed.</returns>
    /// <remarks>
    /// This guard is called once per receiver generation, not once per polling iteration. When Telegram reports an
    /// active webhook, it calls <c>deleteWebhook</c> with <c>dropPendingUpdates=false</c> and performs a second read-only
    /// probe. Any probe, delete, or verification failure prevents <c>StartReceiving</c>; the existing serialized
    /// startup recovery can then retry without creating two receivers for the same bot.
    /// Probe deadlines publish cancellation provenance only; diagnostic code never inspects or retains the webhook URL.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when Telegram still reports an active webhook after the delete operation completes.
    /// </exception>
    private async Task EnsureLongPollingWebhookClearedAsync(
        ITelegramBotClient client,
        BotInstanceConfig bot,
        CancellationToken cancellationToken)
    {
        using var initialProbeCts = CreateStartupProbeCancellation(cancellationToken);
        using var initialMetadata = _clientProvider.IsTelemetryEnabled ?
            TelegramRequestCancellationScope.PushStartupProbe(cancellationToken, initialProbeCts.Token) : null;
        var webhookInfo = await client.GetWebhookInfo(initialProbeCts.Token);
        if (string.IsNullOrWhiteSpace(webhookInfo?.Url))
            return;

        _logger.LogWarning(
            "Active Telegram webhook detected before long polling; removing it without dropping pending updates. botId={BotId}, botType={BotType}",
            bot.Id,
            bot.Type);

        using var deleteCts = CreateStartupProbeCancellation(cancellationToken);
        using var deleteMetadata = _clientProvider.IsTelemetryEnabled ?
            TelegramRequestCancellationScope.PushStartupProbe(cancellationToken, deleteCts.Token) : null;
        await client.DeleteWebhook(
            dropPendingUpdates: false,
            cancellationToken: deleteCts.Token);

        using var verificationCts = CreateStartupProbeCancellation(cancellationToken);
        using var verificationMetadata = _clientProvider.IsTelemetryEnabled ?
            TelegramRequestCancellationScope.PushStartupProbe(cancellationToken, verificationCts.Token) : null;
        var verified = await client.GetWebhookInfo(verificationCts.Token);
        if (!string.IsNullOrWhiteSpace(verified?.Url))
            throw new InvalidOperationException("Telegram still reports an active webhook after deletion.");
    }

    /// <summary>
    /// Completes Telegram identity validation and command-menu setup after the receiver has already been registered.
    /// </summary>
    /// <param name="botId">Internal registry bot id whose active receiver is being initialized.</param>
    /// <param name="expectedTelegramBotId">
    /// Numeric bot id extracted from the token used to create the receiver. A changed token cancels this background
    /// worker so it cannot validate or disable a newer receiver generation.
    /// </param>
    /// <param name="expectedEndpointGeneration">Captured positive route epoch, or null when endpoint routing is not installed; obsolete workers cannot initialize a replacement receiver.</param>
    /// <param name="cancellationToken">Host/receiver lifetime token that cancels background retries during shutdown.</param>
    /// <returns>A task that completes after success, a definitive token rejection, receiver stop, or retry exhaustion.</returns>
    /// <remarks>
    /// This operation never creates a receiver. Transient failures preserve the existing receiver and mark it
    /// degraded; a definitive invalid tenant token is routed through the serialized cleanup path. No token or API
    /// secret is written to logs.
    /// Existing bounded probes publish startup-deadline provenance, so telemetry does not label a local
    /// initialization deadline as host cancellation. The configured budgets and retry sequence are unchanged.
    /// </remarks>
    private async Task CompleteBotInitializationAsync(
        string botId,
        long? expectedTelegramBotId,
        long? expectedEndpointGeneration,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;

        for (var attempt = 1; attempt <= maxAttempts && !cancellationToken.IsCancellationRequested; attempt++)
        {
            if (!IsReceiverRunning(botId))
                return;

            var bot = _registry.GetById(botId);
            if (bot == null ||
                !bot.Enabled ||
                string.IsNullOrWhiteSpace(bot.Token) ||
                TelegramBotTokenIdentity.ExtractBotId(bot.Token) != expectedTelegramBotId ||
                !IsCurrentEndpointGeneration(botId, expectedTelegramBotId, expectedEndpointGeneration))
            {
                return;
            }

            try
            {
                var client = _clientProvider.GetEndpointReceiverClient(bot.Id);
                using var probeCts = CreateStartupProbeCancellation(cancellationToken);
                using var probeMetadata = _clientProvider.IsTelemetryEnabled ?
                    TelegramRequestCancellationScope.PushStartupProbe(cancellationToken, probeCts.Token) : null;
                var me = await client.GetMe(probeCts.Token);
                await ConfigureBotCommandsAsync(client, bot, probeCts.Token);

                if (!IsReceiverRunning(botId) || !IsCurrentEndpointGeneration(botId, expectedTelegramBotId, expectedEndpointGeneration))
                    return;

                _runtimeStatusStore.MarkStarted(bot, me.Username);
                _logger.LogInformation(
                    "Telegram bot background initialization completed. botId={BotId}, username=@{Username}, attempt={Attempt}",
                    bot.Id,
                    me.Username,
                    attempt);
                return;
            }
            catch (Exception ex) when (IsTelegramTransientStartupError(ex))
            {
                _runtimeStatusStore.MarkDegraded(bot, ex.Message);
                _logger.LogInformation(
                    "Telegram bot background initialization remains degraded. botId={BotId}, attempt={Attempt}/{MaxAttempts}, error={Error}",
                    bot.Id,
                    attempt,
                    maxAttempts,
                    ex.Message);

                if (attempt < maxAttempts)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, attempt * 5)), cancellationToken);
            }
            catch (Exception ex) when (IsTelegramTokenInvalidError(ex))
            {
                _runtimeStatusStore.MarkFailed(bot, "invalid_token", ex.Message);
                if (IsTenant(bot))
                {
                    await DisableTenantTokenAsync(bot, ex.Message, notifyOwner: true, cancellationToken);
                }
                else
                {
                    await StopBotAsync(bot.Id, cancellationToken);
                    _logger.LogCritical(
                        ex,
                        "Configured Telegram bot receiver stopped after background validation rejected its token. botId={BotId}",
                        bot.Id);
                }
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _runtimeStatusStore.MarkDegraded(bot, ex.Message);
                _logger.LogError(
                    ex,
                    "Telegram bot background initialization failed without stopping the receiver. botId={BotId}",
                    bot.Id);
                return;
            }
        }
    }

    /// <summary>Removes only the completed receiver's exact registration and wakes existing recovery after a fence exits polling.</summary>
    /// <param name="botId">Canonical runtime bot id captured when this receiver started.</param>
    /// <param name="receiver">Exact owned CTS; a predecessor must never remove/cancel a replacement.</param>
    /// <param name="task">Completed receiving task retained for lifecycle join.</param>
    /// <param name="pollingGeneration">Existing polling telemetry generation, distinct from endpoint generation.</param>
    /// <remarks>A prelogout failure can reopen the source after its fenced poll exited; leaving its CTS registered would suppress recovery forever.</remarks>
    private void CompleteReceiverGeneration(string botId, CancellationTokenSource receiver, Task task, long pollingGeneration)
    {
        _clientProvider.PollingTelemetry.Stopped(botId, pollingGeneration);
        bool removed;
        lock (_syncRoot)
        {
            removed = _botReceivers.TryGetValue(botId, out var current) && ReferenceEquals(current, receiver) &&
                _receiverTasks.TryGetValue(botId, out var registeredTask) && ReferenceEquals(registeredTask, task);
            if (removed)
            {
                _botReceivers.Remove(botId);
                _runtimeStatusStore.MarkStopped(botId, "receiver generation completed");
            }
        }
        if (!removed) return;
        _transientPollingBackoff.Remove(botId);
        try { receiver.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { }
        receiver.Dispose();
        SignalEndpointRecovery();
    }

    /// <summary>Prevents completed probes or errors from an obsolete endpoint generation mutating a replacement receiver.</summary>
    /// <param name="botId">Exact canonical runtime bot id captured at receiver startup.</param>
    /// <param name="identity">Captured positive BotFather identity, or null for legacy deterministic runtime factories.</param>
    /// <param name="generation">Captured endpoint epoch, or null when routing is not installed.</param>
    /// <returns>True only for the current configured identity/epoch; legacy no-routing receivers retain original behavior.</returns>
    /// <remarks>This performs only a short metadata read. Receiver cancellation remains the primary shutdown signal.</remarks>
    private bool IsCurrentEndpointGeneration(string botId, long? identity, long? generation)
    {
        if (_endpointGate == null || !generation.HasValue) return true;
        if (!identity.HasValue) return false;
        try { return _endpointGate.GetRoute(botId, identity.Value).Generation == generation.Value; }
        catch (BotTransportUnavailableException) { return false; }
    }

    /// <summary>Acquires the existing per-bot lifecycle semaphore for a fenced endpoint migration.</summary>
    /// <param name="botId">Required exact configured runtime bot id, never a username or default-bot alias.</param>
    /// <param name="token">Host/migration cancellation while waiting; callback foreground tokens must not own migration work.</param>
    /// <returns>An asynchronous lease serializing stop, join, strict destination startup and committed route publication.</returns>
    /// <exception cref="BotTransportUnavailableException">The exact configured bot is absent.</exception>
    /// <exception cref="OperationCanceledException">Lifecycle ownership waiting is cancelled.</exception>
    /// <remarks>No shared Local process/container is controlled. The coordinator owns request fencing and durable intent.</remarks>
    /// <example><code>await using var lifecycle = await runtime.AcquireAsync(botId, token); await lifecycle.StopAndWaitAsync(token);</code></example>
    public async Task<ITelegramEndpointReceiverLease> AcquireAsync(string botId, CancellationToken token)
    {
        var bot = _registry.GetById(botId);
        if (string.IsNullOrWhiteSpace(botId) || bot == null || !string.Equals(bot.Id, botId, StringComparison.OrdinalIgnoreCase))
            throw new BotTransportUnavailableException("migration_bot_unavailable");
        var gate = GetLifecycleGate(bot.Id);
        await gate.WaitAsync(token);
        return new EndpointReceiverLease(this, bot.Id, gate);
    }

    /// <summary>Bot-specific receiver ownership backed by the runtime's existing start/stop/recovery semaphore.</summary>
    private sealed class EndpointReceiverLease : ITelegramEndpointReceiverLease
    {
        /// <summary>Runtime owner released exactly once; no client or shared API server ownership is transferred.</summary>
        private MultiBotHostedService _owner;
        /// <summary>Canonical internal bot id isolated by this lease.</summary>
        private readonly string _botId;
        /// <summary>Already-acquired runtime lifecycle semaphore.</summary>
        private readonly SemaphoreSlim _gate;
        /// <summary>Wraps an already-acquired per-bot runtime lifecycle gate.</summary>
        /// <param name="owner">Runtime that owns the receiver dictionaries and host cancellation.</param>
        /// <param name="botId">Exact configured canonical bot id.</param>
        /// <param name="gate">Existing acquired lifecycle semaphore.</param>
        internal EndpointReceiverLease(MultiBotHostedService owner, string botId, SemaphoreSlim gate)
        { _owner = owner; _botId = botId; _gate = gate; }

        /// <inheritdoc />
        public async Task StopAndWaitAsync(CancellationToken token)
        {
            var owner = _owner ?? throw new ObjectDisposedException(nameof(EndpointReceiverLease));
            owner.StopBotCore(_botId, "endpoint migration");
            Task previous;
            lock (owner._syncRoot) owner._receiverTasks.TryGetValue(_botId, out previous);
            await TelegramReceiverLifetime.ObservePreviousAsync(previous, token);
        }

        /// <inheritdoc />
        public async Task<bool> StartValidatedAsync(CancellationToken token)
        {
            var owner = _owner ?? throw new ObjectDisposedException(nameof(EndpointReceiverLease));
            if (owner._receivingCts == null || owner._receivingCts.IsCancellationRequested) return false;
            return await owner.StartBotCoreAsync(_botId, token, strictEndpointValidation: true) == BotStartupResult.Started;
        }

        /// <summary>Releases lifecycle ownership once; it does not start/stop a receiver or alter committed routing state.</summary>
        /// <returns>A completed value task after releasing the semaphore.</returns>
        /// <remarks>Failures remain durable coordinator states. Another bot's receiver is never affected.</remarks>
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _owner, null) != null) _gate.Release();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Creates a linked cancellation scope for one bounded Telegram startup or initialization probe.
    /// </summary>
    /// <param name="outerCancellationToken">Host, owner-update, or receiver token that must also cancel the probe.</param>
    /// <returns>
    /// A disposable cancellation source that expires after the configured startup probe timeout, clamped to five
    /// through sixty seconds.
    /// </returns>
    private CancellationTokenSource CreateStartupProbeCancellation(CancellationToken outerCancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(outerCancellationToken);
        source.CancelAfter(_startupProbeTimeout);
        return source;
    }

    /// <summary>
    /// Retries enabled bots that missed their receiver during the first application startup pass.
    /// </summary>
    /// <param name="nonRetryableBotIds">
    /// Internal bot ids that failed with a permanent startup decision such as disabled configuration, duplicate
    /// token, or invalid token. These ids are skipped so the recovery loop never hammers Telegram with known-bad
    /// tokens.
    /// </param>
    /// <param name="cancellationToken">
    /// Linked host shutdown token. Cancelling it stops the recovery loop without throwing into the hosted service.
    /// </param>
    /// <returns>
    /// A task completing after initial recovery in no-routing direct construction, or after host cancellation when
    /// endpoint routing keeps the bounded lifetime waiting for newly available persisted routes.
    /// </returns>
    /// <remarks>
    /// This is a process-local safety net for transient Telegram startup failures. It does not replace the normal
    /// tenant owner start button; it only repairs the common Ubuntu restart race where one or more configured owned
    /// bots fail <c>GetMe</c> or <c>SetMyCommands</c> once and would otherwise remain offline until another service
    /// restart. Recovery is persistent but its delay is exponentially backed off and capped. With endpoint routing,
    /// a coalesced availability wake restores a safely reactivated source after restart without polling or spawning a task per health event.
    /// </remarks>
    private async Task RecoverMissingStartupReceiversAsync(
        HashSet<string> nonRetryableBotIds,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        var routingWake = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var missingBots = GetRetryableMissingBots(nonRetryableBotIds);
                if (missingBots.Count == 0)
                {
                    if (_endpointGate == null) return;
                    attempt = 0;
                    await _endpointRecoveryWake.WaitAsync(cancellationToken);
                    routingWake = true;
                    continue;
                }

                attempt++;
                var delay = CalculateStartupRecoveryDelay(attempt);
                if (!routingWake)
                {
                    foreach (var bot in missingBots)
                        _clientProvider.PollingTelemetry.Backoff(bot.Id, delay, "startup_recovery");
                    if (_endpointGate == null) await Task.Delay(delay, cancellationToken);
                    else await _endpointRecoveryWake.WaitAsync(delay, cancellationToken);
                }
                // A committed availability wake repairs the source immediately; real startup failures still use existing backoff.
                routingWake = false;

                foreach (var bot in missingBots)
                {
                    _logger.LogInformation(
                        "Retrying Telegram bot receiver startup. botId={BotId}, attempt={Attempt}, delaySeconds={DelaySeconds}",
                        bot.Id,
                        attempt,
                        delay.TotalSeconds);

                    var result = await StartBotAttemptSerializedAsync(bot.Id, cancellationToken);
                    if (IsNonRetryableStartupResult(result) && !(result == BotStartupResult.Skipped && _endpointGate != null && !_endpointGate.CanReceive(bot.Id)))
                        nonRetryableBotIds.Add(bot.Id);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Telegram bot receiver startup recovery pass failed; persistent recovery remains active.");
            }
        }
    }

    /// <summary>
    /// Calculates a capped exponential delay for persistent receiver startup or webhook recovery.
    /// </summary>
    /// <param name="attemptNumber">One-based transient recovery attempt number; values below one are treated as one.</param>
    /// <returns>A delay starting at 15 seconds and capped at five minutes.</returns>
    /// <remarks>
    /// The cap bounds outage recovery latency while avoiding rapid repeated Telegram webhook probes. Lifecycle gates
    /// remain the concurrency boundary; this delay never authorizes a second receiver.
    /// </remarks>
    /// <example>
    /// Attempt one waits 15 seconds, attempt two 30 seconds, and later attempts never exceed five minutes.
    /// </example>
    private static TimeSpan CalculateStartupRecoveryDelay(int attemptNumber)
    {
        var exponent = Math.Clamp(attemptNumber - 1, 0, 20);
        var seconds = StartupRecoveryDelay.TotalSeconds * Math.Pow(2d, exponent);
        return TimeSpan.FromSeconds(Math.Min(StartupRecoveryMaximumDelay.TotalSeconds, seconds));
    }

    /// <summary>
    /// Gets the enabled bot configurations that still need a receiver and are eligible for startup retry.
    /// </summary>
    /// <param name="nonRetryableBotIds">
    /// Internal bot ids that have already been classified as disabled, invalid, or duplicate during startup.
    /// </param>
    /// <returns>
    /// A snapshot of enabled bots that have a token, are not marked non-retryable, and currently do not have an
    /// active receiver cancellation token registered in this hosted service.
    /// </returns>
    /// <remarks>
    /// The method reads the registry each time so tenant rows updated by duplicate-token cleanup or owner actions
    /// are reflected before the next retry attempt.
    /// </remarks>
    private IReadOnlyList<BotInstanceConfig> GetRetryableMissingBots(HashSet<string> nonRetryableBotIds)
    {
        return _registry.Bots
            .Where(bot => bot != null)
            .Where(bot => bot.Enabled)
            .Where(bot => !string.IsNullOrWhiteSpace(bot.Token))
            .Where(bot => !nonRetryableBotIds.Contains(bot.Id))
            .Where(bot => _endpointGate == null || _endpointGate.CanReceive(bot.Id))
            .Where(bot => !IsReceiverRunning(bot.Id))
            .ToList();
    }

    /// <summary>Wakes the existing receiver recovery lifetime after a meaningful endpoint availability change.</summary>
    /// <remarks>One semaphore token coalesces changes across bots; health-only revisions do not spawn work.</remarks>
    private void SignalEndpointRecovery()
    {
        try { _endpointRecoveryWake.Release(); } catch (SemaphoreFullException) { }
    }

    /// <summary>
    /// Checks whether the hosted service currently owns a Telegram receiver for the specified bot id.
    /// </summary>
    /// <param name="botId">Internal runtime bot id stored in <see cref="BotRegistry" />.</param>
    /// <returns><c>true</c> when a receiver cancellation token is registered; otherwise <c>false</c>.</returns>
    private bool IsReceiverRunning(string botId)
    {
        if (string.IsNullOrWhiteSpace(botId))
            return false;

        lock (_syncRoot)
            return _botReceivers.ContainsKey(botId);
    }

    /// <summary>
    /// Determines whether a startup result should be excluded from automatic recovery retries.
    /// </summary>
    /// <param name="result">Classification returned by <see cref="StartBotCoreAsync" />.</param>
    /// <returns>
    /// <c>true</c> for disabled/missing-token bots, duplicate token conflicts, and invalid tokens; <c>false</c>
    /// for transient failures that should be retried.
    /// </returns>
    private static bool IsNonRetryableStartupResult(BotStartupResult result)
    {
        return result is BotStartupResult.Skipped or BotStartupResult.DuplicateConflict or BotStartupResult.InvalidToken;
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
    /// <c>tenant-{ownerId}</c> or <c>tenant-{ownerId}-{storeNumber}</c> format. Owner wallet identity remains separate.
    /// </param>
    /// <param name="exception">Exception raised by the Telegram polling loop.</param>
    /// <param name="cancellationToken">Receiver cancellation token.</param>
    /// <returns>A task that completes after logging and any tenant cleanup attempt.</returns>
    /// <remarks>
    /// Telegram can report revoked tokens after a receiver has already been started. This handler disables only
    /// the affected tenant bot and then delegates normal error logging to the shared dispatcher.
    /// User-block and chat-not-found errors are treated as definitive per-user delivery failures. Request timeouts
    /// and Telegram 5xx responses are treated as transient polling transport failures and do not change chat state
    /// or stop the receiver. Transient failures additionally apply a bounded exponential delay with jitter, tracked
    /// independently per internal bot id through <see cref="TelegramPollingBackoffTracker" />, so a Telegram outage that
    /// hits every owned and tenant receiver at once cannot produce one synchronized retry storm; the delay is awaited
    /// with the receiver cancellation token and a cancelled wait is the normal shutdown path. A Telegram 429 rate limit
    /// pauses this receiver for Telegram's <c>RetryAfter</c> window
    /// (plus a small buffer) before the polling loop issues the next <c>getUpdates</c>. These runtime clients disable
    /// Telegram.Bot 22.10.3's own rate-limit retry (<c>RetryCount=0</c> in <c>BotClientProvider</c>), so without this
    /// pause the receiver would tight-loop for the whole rate-limit window. A Telegram 409
    /// getUpdates conflict means another process or receiver is already polling the same token; this receiver is
    /// stopped to prevent noisy conflict loops. Telegram's distinct "webhook is active" conflict schedules one
    /// bot-scoped recovery generation, which clears the webhook through the startup guard before polling resumes.
    /// Telemetry reports the policy-selected backoff without changing it; telemetry's degraded window resets
    /// only after a fully validated successful poll, independently of the existing operational backoff counter.
    /// </remarks>
    private async Task HandleBotPollingErrorAsync(string botId, Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || exception is OperationCanceledException)
            return;

        if (TelegramRateLimitPolicy.IsRateLimited(exception))
        {
            // A 429 proves Telegram's HTTP path is reachable, so the current transient-5xx incident is over; the wasted
            // exponential counter is cleared. The delay itself stays Telegram's authoritative Retry-After and is never
            // replaced or combined with the exponential 5xx backoff.
            _transientPollingBackoff.RecordHealthyPolling(botId);

            var retryDelay = TelegramRateLimitPolicy.GetRetryDelay(exception);
            _clientProvider.PollingTelemetry.Backoff(botId, retryDelay, "rate_limit");
            _logger.LogDebug(
                "Telegram polling rate limited; pausing this receiver before the next getUpdates call. botId={BotId}, retryAfterSeconds={RetryAfterSeconds}",
                botId,
                retryDelay.TotalSeconds);
            try
            {
                await Task.Delay(retryDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Receiver shutdown while waiting for the rate-limit window to pass is the normal stop path.
            }

            return;
        }

        if (IsTelegramUserDeliveryError(exception))
        {
            // A per-user delivery failure means an update was actually received from Telegram, which is direct evidence
            // that long polling is healthy again, so any transient gateway incident is cleared.
            _transientPollingBackoff.RecordHealthyPolling(botId);

            _logger.LogDebug(
                "Telegram polling delivery error ignored. botId={BotId}, telegramError={Message}",
                botId,
                exception.Message);
            return;
        }

        if (IsTelegramTransientGatewayPollingError(exception))
        {
            // One bounded, jittered delay PER BOT. Telegram.Bot 22.10.3 awaits this polling error handler before issuing the
            // next getUpdates, exactly like the existing 429 pause, so the delay itself breaks the synchronized retry
            // storm. The decision is computed quickly under the tracker's short per-bot lock and the delay is awaited
            // afterwards with no registry, lifecycle, or database lock held.
            var decision = _transientPollingBackoff.RegisterTransientFailure(botId);
            _clientProvider.PollingTelemetry.Backoff(botId, decision.Delay, "transient_gateway");

            if (decision.ShouldLogOperational)
            {
                // Compact operational summary: at most one line per bot per logging window. The raw Telegram message is
                // intentionally not logged so a provider payload can never leak into local or forwarded logs, and this
                // message text is suppressed from the Telegram logger channel by TelegramLogSuppression.
                _logger.LogInformation(
                    "Telegram polling degraded. botId={BotId} consecutiveFailures={ConsecutiveFailures} delaySeconds={DelaySeconds} errorType={ErrorType}",
                    botId,
                    decision.ConsecutiveFailures,
                    Math.Round(decision.Delay.TotalSeconds, 2),
                    exception.GetType().Name);
            }
            else
            {
                _logger.LogDebug(
                    "Transient Telegram polling gateway error ignored. botId={BotId}, consecutiveFailures={ConsecutiveFailures}, delaySeconds={DelaySeconds}",
                    botId,
                    decision.ConsecutiveFailures,
                    Math.Round(decision.Delay.TotalSeconds, 2));
            }

            // Shutdown during the backoff window is the normal stop path: it is never logged as an error, never marks
            // the bot failed, and never restarts the receiver.
            await TelegramPollingBackoffPolicy.DelayAsync(decision.Delay, cancellationToken);
            return;
        }

        var bot = _registry.GetById(botId);
        if (bot != null &&
            string.Equals(bot.Id, botId, StringComparison.OrdinalIgnoreCase) &&
            IsTelegramWebhookPollingConflict(exception))
        {
            ScheduleWebhookConflictRecovery(bot);
            return;
        }

        if (bot != null &&
            string.Equals(bot.Id, botId, StringComparison.OrdinalIgnoreCase) &&
            IsTelegramGetUpdatesConflict(exception))
        {
            await StopBotAsync(botId, cancellationToken);
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
                await using var scope = _scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<TelegramBotService>().HandlePollingErrorAsync(client, exception, cancellationToken);
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
    /// Schedules at most one webhook-conflict recovery task for an affected bot receiver generation.
    /// </summary>
    /// <param name="bot">Current runtime bot whose long-polling receiver encountered Telegram's webhook conflict.</param>
    /// <remarks>
    /// The process-local set suppresses duplicate callbacks from the same failing receiver. Recovery stops the
    /// affected receiver through its lifecycle gate and retries the shared serialized startup path with capped
    /// exponential backoff until the bot starts, becomes definitively non-retryable, or the host stops. The receiver
    /// cancellation token is deliberately not reused because stopping that generation cancels it. The per-bot
    /// lifecycle gate and receiver registry remain the only authority capable of creating a polling generation.
    /// </remarks>
    private void ScheduleWebhookConflictRecovery(BotInstanceConfig bot)
    {
        lock (_syncRoot)
        {
            if (!_webhookConflictRecoveries.Add(bot.Id))
                return;
        }

        TrackBackgroundTask(Task.Run(
            async () =>
            {
                try
                {
                    _logger.LogWarning(
                        "Telegram long polling stopped because a webhook is active; bot-scoped recovery will remove it. botId={BotId}, botType={BotType}",
                        bot.Id,
                        bot.Type);

                    await StopBotAsync(bot.Id);
                    var hostToken = _receivingCts?.Token ?? CancellationToken.None;
                    await RecoverWebhookConflictReceiverAsync(bot.Id, bot.Type, hostToken);
                }
                catch (OperationCanceledException) when (_receivingCts?.IsCancellationRequested == true)
                {
                    // Host shutdown is the normal terminal path for an in-flight recovery generation.
                }
                catch (Exception recoveryException)
                {
                    _logger.LogError(
                        recoveryException,
                        "Telegram webhook-conflict recovery failed. botId={BotId}, botType={BotType}",
                        bot.Id,
                        bot.Type);
                }
                finally
                {
                    lock (_syncRoot)
                        _webhookConflictRecoveries.Remove(bot.Id);
                }
            },
            CancellationToken.None));
    }

    /// <summary>
    /// Persistently restarts one receiver after Telegram reported that a webhook blocked long polling.
    /// </summary>
    /// <param name="botId">
    /// Internal registry bot id whose stopped receiver must be recreated; this is not the Telegram numeric bot id.
    /// </param>
    /// <param name="botType">Safe owned/tenant runtime type used only for structured operational attribution.</param>
    /// <param name="cancellationToken">Host-lifetime token that permanently ends recovery during shutdown.</param>
    /// <returns>
    /// A task that completes after startup succeeds, the current registry entry becomes non-retryable, or the host
    /// stops. The method never creates a receiver outside the normal per-bot lifecycle gate.
    /// </returns>
    /// <remarks>
    /// Each attempt re-runs the webhook GET/delete/verify preflight through <see cref="StartBotAttemptSerializedAsync"/>.
    /// Transient Telegram timeouts and transport failures use capped exponential backoff and do not permanently take an
    /// enabled tenant offline. Duplicate-token, disabled, missing-token, and invalid-token decisions stop recovery.
    /// </remarks>
    /// <example>
    /// Runtime webhook conflicts call this helper only after the affected receiver generation has been stopped.
    /// </example>
    private async Task RecoverWebhookConflictReceiverAsync(
        string botId,
        string botType,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            attempt++;
            var result = await StartBotAttemptSerializedAsync(botId, cancellationToken);
            if (result is BotStartupResult.Started or BotStartupResult.AlreadyRunning)
                return;

            if (IsNonRetryableStartupResult(result))
            {
                _logger.LogError(
                    "Telegram webhook-conflict recovery stopped after a non-retryable startup decision. botId={BotId}, botType={BotType}, result={Result}",
                    botId,
                    botType,
                    result);
                return;
            }

            var delay = CalculateStartupRecoveryDelay(attempt);
            _clientProvider.PollingTelemetry.Backoff(botId, delay, "webhook_recovery");
            _logger.LogInformation(
                "Telegram webhook-conflict receiver remains offline after a transient preflight failure; retry is scheduled. botId={BotId}, botType={BotType}, attempt={Attempt}, delaySeconds={DelaySeconds}",
                botId,
                botType,
                attempt,
                delay.TotalSeconds);
            await Task.Delay(delay, cancellationToken);
        }
    }

    /// <summary>
    /// Detects Telegram's conflict response that specifically forbids <c>getUpdates</c> while a webhook is active.
    /// </summary>
    /// <param name="exception">Exception raised by the Telegram long-polling receiver.</param>
    /// <returns><c>true</c> only for the webhook-versus-long-polling conflict; otherwise <c>false</c>.</returns>
    /// <remarks>
    /// This classification must run before duplicate-poller classification. Only this conflict is safe to recover
    /// automatically by deleting the webhook; a real second <c>getUpdates</c> process remains an operator incident.
    /// </remarks>
    private static bool IsTelegramWebhookPollingConflict(Exception exception)
    {
        if (exception is not ApiRequestException apiException || apiException.ErrorCode != 409)
            return false;

        var message = apiException.Message ?? string.Empty;
        return message.Contains("webhook is active", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("use deleteWebhook", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("can't use getUpdates method while webhook", StringComparison.OrdinalIgnoreCase);
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
    /// A duplicate-poller conflict is different from both a user delivery failure and the separately recoverable
    /// webhook conflict. Continuing to poll would create an error loop, so the caller stops only the affected bot and
    /// requires the competing process to be removed by an operator.
    /// </remarks>
    private static bool IsTelegramGetUpdatesConflict(Exception exception)
    {
        if (exception is not ApiRequestException apiException)
            return false;

        var message = apiException.Message ?? string.Empty;
        return !IsTelegramWebhookPollingConflict(exception) &&
               (message.Contains("terminated by other getUpdates request", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("only one bot instance is running", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Detects definitive Telegram delivery errors that mean one user blocked the bot or the chat is unreachable.
    /// </summary>
    /// <param name="exception">Exception raised by the Telegram polling loop or update handler.</param>
    /// <returns>
    /// <c>true</c> when the error is a non-fatal per-user delivery failure; otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// The polling library forwards unhandled update-handler exceptions into the polling error callback. A
    /// customer blocking any owned or tenant bot must not disable that receiver or terminate the process. Transport
    /// timeouts are classified separately because they do not prove the chat is unreachable.
    /// </remarks>
    private static bool IsTelegramUserDeliveryError(Exception exception)
    {
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
    /// Detects temporary Telegram transport and gateway failures returned by long polling.
    /// </summary>
    /// <param name="exception">Exception raised by the Telegram polling loop.</param>
    /// <returns>
    /// <c>true</c> for Telegram request timeouts, HTTP 429 rate limits, HTTP/transport 5xx gateway-server failures, and
    /// network-level transport exceptions that should be retried by polling with a bounded delay; otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// Telegram occasionally returns request timeouts or bursts of 502 Bad Gateway from <c>getUpdates</c>. Those
    /// failures do not mean a user chat, bot token, or receiver is broken. HTTP 429 is included so startup probes and
    /// polling treat rate limits as transient instead of reporting a tenant failure through the Telegram log channel.
    /// Classification delegates to <see cref="TelegramPollingBackoffPolicy.IsTransientGatewayFailure" /> so the shared
    /// dispatcher cannot disagree with the receiver about whether a 502 is transient, including the case where the
    /// Telegram edge returns a plain <c>RequestException</c> carrying only an HTTP status.
    /// </remarks>
    private static bool IsTelegramTransientGatewayPollingError(Exception exception)
    {
        // Delegates to the shared classifier so owned, assistant, and tenant receivers all agree on what is transient.
        // A Telegram edge 502 can arrive as a plain RequestException carrying only an HTTP status, which previously fell
        // through to the noisy legacy polling logger instead of the bounded backoff path.
        return TelegramPollingBackoffPolicy.IsTransientGatewayFailure(exception);
    }

    /// <summary>
    /// Detects temporary Telegram or network failures during receiver startup.
    /// </summary>
    /// <param name="exception">
    /// Exception raised while calling Telegram <c>getMe</c>, setting bot commands, or creating a receiver.
    /// </param>
    /// <returns>
    /// <c>true</c> when startup should be retried without clearing a tenant token or sending a central failure
    /// notification; otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// Startup probes use normal Telegram HTTP requests and can fail with short-lived timeout or 5xx responses.
    /// Those failures do not prove that the tenant token, channel, or bot configuration is wrong, so they should
    /// stay as process-local retryable status instead of being reported as an actionable tenant failure.
    /// </remarks>
    private static bool IsTelegramTransientStartupError(Exception exception)
    {
        if (exception is TimeoutException || exception is TaskCanceledException)
            return true;

        if (exception is RequestException requestException)
        {
            var requestMessage = requestException.Message ?? string.Empty;
            return requestMessage.Contains("request timed out", StringComparison.OrdinalIgnoreCase) ||
                   requestMessage.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                   requestMessage.Contains("timeout", StringComparison.OrdinalIgnoreCase);
        }

        if (IsTelegramTransientGatewayPollingError(exception))
            return true;

        var message = exception?.Message ?? string.Empty;
        return message.Contains("request timed out", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("timeout", StringComparison.OrdinalIgnoreCase);
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
    /// <param name="cancellationToken">
    /// Caller token used when no hosted-service lifetime token exists. Polling receiver cancellation is not reused
    /// after stop because it would abort the required users.db cleanup.
    /// </param>
    /// <param name="lifecycleGateHeld">
    /// Whether the caller already owns this bot's lifecycle gate. Startup validation passes <c>true</c> to avoid
    /// reacquiring the same non-reentrant semaphore; polling and duplicate cleanup callers leave it <c>false</c>.
    /// </param>
    /// <returns>A task that completes after best-effort cleanup and owner notification.</returns>
    /// <remarks>
    /// The tenant username and settings are preserved for the owner panel, but <c>Token</c> is cleared and
    /// <c>Enabled</c> is set to <c>false</c>. Customer-wallet approval evidence is also revoked, so a replacement identity cannot inherit trust. This prevents one revoked tenant token from breaking the whole process.
    /// </remarks>
    private async Task DisableTenantTokenAsync(
        BotInstanceConfig bot,
        string reason,
        bool notifyOwner,
        CancellationToken cancellationToken,
        bool lifecycleGateHeld = false)
    {
        // Polling supplies the receiver token, which becomes cancelled as soon as this bot is stopped. Database
        // cleanup must instead follow the host lifetime so token invalidation cannot leave an enabled stale row.
        var cleanupToken = _receivingCts?.Token ?? cancellationToken;

        if (lifecycleGateHeld)
            StopBotCore(bot.Id, "tenant token cleanup");
        else
            await StopBotAsync(bot.Id, cancellationToken);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var tenant = await db.BotInstances.FirstOrDefaultAsync(x => x.Id == bot.Id, cleanupToken);
        if (tenant == null)
            return;

        tenant.Enabled = false;
        tenant.Token = null;
        TenantCustomerWalletPolicy.Revoke(tenant);
        tenant.TelegramBotId = null;
        tenant.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cleanupToken);

        _registry.Upsert(tenant);
        _clientProvider.Invalidate(tenant.Id);
        _runtimeStatusStore.MarkFailed(bot, "disabled_token_cleared", reason);

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
            reason,
            null);

        if (notifyOwner && tenant.OwnerTelegramUserId.HasValue)
            await NotifyTenantOwnerTokenClearedAsync(tenant.OwnerTelegramUserId.Value, tenant.Username, cleanupToken);
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

            await _clientProvider.GetDefaultClient().SendMessage(
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
    /// <param name="bot">Required current tenant configuration from the exact runtime registry; owns the internal bot identity and tenant settings, never a default owned-bot substitute.</param>
    /// <param name="telegramUsername">Optional public bot username returned by GetMe; null/empty falls back only to this tenant's configured username.</param>
    /// <param name="status">Required internal lifecycle description for the private operator channel; not customer payload or migration-success evidence.</param>
    /// <param name="error">Optional non-secret error text; never pass a token-bearing URL, customer message or payment credential.</param>
    /// <param name="receiverEndpoint">Captured receiver origin for a successful start; null for failure/stop events without a proven receiver origin.</param>
    /// <remarks>Retains the existing durable HTML event and central logger channel. Endpoint metadata is not proof of network health or migration completion, and creates no financial backup intent.</remarks>
    /// <example><code>LogTenantRuntimeEvent(bot, username, "روشن شد", null, receiverRoute.Endpoint);</code></example>
    private void LogTenantRuntimeEvent(BotInstanceConfig bot, string telegramUsername, string status, string error, TelegramEndpointType? receiverEndpoint)
    {
        LogTenantRuntimeEvent(
            bot.Id,
            string.IsNullOrWhiteSpace(telegramUsername) ? bot.Username : telegramUsername,
            bot.OwnerTelegramUserId,
            bot.TenantChannelIds ?? new List<string>(),
            bot.SupportAccount,
            status,
            error,
            receiverEndpoint);
    }

    /// <summary>
    /// Writes a tenant runtime lifecycle event as durable operational HTML using extracted tenant settings.
    /// </summary>
    /// <param name="tenantId">Required exact internal tenant BotId owning this lifecycle event, not a Telegram user/chat id.</param>
    /// <param name="tenantUsername">Optional last-known public bot username; missing identity is shown explicitly rather than guessed.</param>
    /// <param name="ownerTelegramUserId">Optional numeric Telegram user id of this storefront owner; retained only in the existing private operational audit.</param>
    /// <param name="channels">Optional tenant-owned forced-join channel references; existing private HTML formatting handles empty values.</param>
    /// <param name="supportAccount">Optional tenant-configured public support reference, not a credential.</param>
    /// <param name="status">Required internal lifecycle description for the private logger channel; ordinary receiver start is not final migration activation.</param>
    /// <param name="error">Optional secret-free error explanation; customer payload and token-bearing request URLs are forbidden.</param>
    /// <param name="receiverEndpoint">Optional proven receiver-start Cloud/Local origin; null omits it instead of guessing a route for a stopped or rejected identity.</param>
    /// <remarks>Preserves the current bot context and central logger routing. A receiver origin alone never claims final migration activation. This operational event never creates financial backup intent.</remarks>
    /// <example><code>LogTenantRuntimeEvent("tenant-123456-1", "sample_store_bot", 123456L, [], "@sample_support", "روشن شد", null, TelegramEndpointType.Cloud);</code></example>
    private void LogTenantRuntimeEvent(
        string tenantId,
        string tenantUsername,
        long? ownerTelegramUserId,
        IEnumerable<string> channels,
        string supportAccount,
        string status,
        string error,
        TelegramEndpointType? receiverEndpoint)
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
            (receiverEndpoint.HasValue ? $"Telegram API: {Html(TelegramEndpointPresentation.EndpointBadge(receiverEndpoint.Value))}\n" : string.Empty) +
            $"ربات: <code>{Html(username)}</code>\n" +
            $"شناسه داخلی: <code>{Html(tenantId)}</code>\n" +
            $"مالک: {owner}\n" +
            $"کانال جوین اجباری: {FormatTelegramReferences(channels)}\n" +
            $"پشتیبانی: {FormatTelegramReference(supportAccount)}" +
            (string.IsNullOrWhiteSpace(error) ? string.Empty : $"\nخطا: <code>{Html(error)}</code>");

        _logger.LogTelegramHtml(message);
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
    /// receive <c>/start</c>, its <c>/refresh</c> reset alias, and the public account commands used by customers and
    /// colleagues.
    /// </param>
    /// <param name="cancellationToken">
    /// Startup cancellation token used to abort the Telegram API call when the host is stopping.
    /// </param>
    /// <returns>
    /// A task that completes after Telegram accepts the command menu or immediately when the bot type does not need
    /// commands.
    /// </returns>
    /// <remarks>
    /// Tenant customers use only <c>/start</c> to return to the storefront home menu. Owned bot users can use either
    /// <c>/start</c> or <c>/refresh</c> to clear bot-scoped transient state and return to their role-appropriate main
    /// menu, or start account operations without relying on a persistent reply keyboard. Telegram receives the whole
    /// command array atomically through <c>SetMyCommands</c> each time the bot runtime starts; this method does not
    /// change conversation state itself.
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
                new BotCommand { Command = "refresh", Description = "پاک کردن وضعیت موقت و برگشت به منوی اصلی" },
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

        await client.SetMyCommands(commands, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Gets the process-local asynchronous lifecycle gate for one internal bot id.
    /// </summary>
    /// <param name="botId">Internal owned, tenant, or assistant bot id; it must not be null or whitespace.</param>
    /// <returns>A stable semaphore shared by every start, stop, recovery, and cleanup operation for that bot.</returns>
    /// <remarks>
    /// Gate objects live for the hosted-service lifetime. Keeping one stable instance per bot prevents a concurrent
    /// start from overwriting the registered receiver CTS and prevents stop from leaving an orphan polling loop.
    /// </remarks>
    private SemaphoreSlim GetLifecycleGate(string botId)
    {
        if (string.IsNullOrWhiteSpace(botId))
            throw new ArgumentException("A bot id is required for lifecycle serialization.", nameof(botId));

        lock (_syncRoot)
        {
            if (!_lifecycleGates.TryGetValue(botId, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _lifecycleGates[botId] = gate;
            }

            return gate;
        }
    }

    /// <summary>
    /// Stops one bot receiver under its lifecycle gate without stopping the whole application.
    /// </summary>
    /// <param name="botId">Internal runtime bot id whose receiver must be cancelled.</param>
    /// <param name="cancellationToken">Optional caller cancellation while waiting for lifecycle ownership; receiver callbacks pass their own token to avoid self-join deadlocks during migration.</param>
    /// <returns>A task that completes after any concurrent startup finishes and the registered receiver is cancelled.</returns>
    /// <exception cref="OperationCanceledException">Lifecycle ownership waiting is cancelled before this call stops a receiver.</exception>
    /// <remarks>
    /// Waiting on the same per-bot gate used by startup makes the final state deterministic. The method is idempotent:
    /// stopping an already stopped bot does not create or cancel another receiver.
    /// </remarks>
    public async Task StopBotAsync(string botId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(botId))
            return;

        var lifecycleGate = GetLifecycleGate(botId);
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            StopBotCore(botId, "receiver stopped");
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Removes and cancels the receiver currently registered for a bot while the caller owns its lifecycle gate.
    /// </summary>
    /// <param name="botId">Internal runtime bot id whose registered receiver should be removed.</param>
    /// <param name="reason">Non-secret stop reason recorded in process-local runtime status.</param>
    /// <returns><c>true</c> when a receiver existed and was cancelled; otherwise <c>false</c>.</returns>
    /// <remarks>
    /// Callers must hold the bot lifecycle gate, except host shutdown after the shared parent token has already been
    /// cancelled. This helper never touches another bot's CTS.
    /// Receiver health metadata is emitted once and removed so stopped historical bot ids do not accumulate.
    /// </remarks>
    private bool StopBotCore(string botId, string reason)
    {
        CancellationTokenSource cts = null;
        lock (_syncRoot)
        {
            if (_botReceivers.TryGetValue(botId, out cts))
                _botReceivers.Remove(botId);
        }

        if (cts == null)
            return false;

        // Receiver lifecycle ended: drop this bot's transient backoff state so a later, unrelated incident starts again
        // at the first step and so historical tenant bot ids cannot accumulate unbounded in-memory state.
        _transientPollingBackoff.Remove(botId);
        _clientProvider.PollingTelemetry.Stopped(botId);

        cts.Cancel();
        cts.Dispose();
        _runtimeStatusStore.MarkStopped(botId, reason);
        _logger.LogInformation("Stopped Telegram bot receiver. botId={BotId}, reason={Reason}", botId, reason);
        return true;
    }

    /// <summary>
    /// Stops all active Telegram receivers during host shutdown.
    /// </summary>
    /// <param name="cancellationToken">Host shutdown token.</param>
    /// <returns>A completed task after cancellation tokens are disposed.</returns>
    /// <remarks>The host tracks receiver, initialization, and recovery lifetimes; each replacement waits for the previous receiver generation to terminate.</remarks>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _scheduler.StopAdmission();
        if (_endpointGate != null) _endpointGate.AvailabilityChanged -= SignalEndpointRecovery;
        lock (_syncRoot)
        {
            foreach (var receiver in _botReceivers.ToList())
            {
                receiver.Value.Cancel();
                receiver.Value.Dispose();
                _runtimeStatusStore.MarkStopped(receiver.Key, "host shutdown");
                _clientProvider.PollingTelemetry.Stopped(receiver.Key);
                _transientPollingBackoff.Remove(receiver.Key);
            }
            _botReceivers.Clear();
        }

        _receivingCts?.Cancel();
        Task[] pending;
        lock (_syncRoot) pending = _backgroundTasks.ToArray();
        try { await Task.WhenAll(pending).WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { _logger.LogWarning("Receiver task ended during shutdown. ErrorType={ErrorType}", ex.GetType().Name); }
        _receivingCts?.Dispose();
    }

    /// <summary>Tracks a bot-lifecycle task and observes failures without keeping completed task history.</summary>
    /// <param name="task">Required receiver or bot-scoped initialization/recovery lifetime.</param>
    /// <remarks>The completion callback is synchronous and never dispatches work; all live tasks are awaited on shutdown.</remarks>
    private void TrackBackgroundTask(Task task)
    {
        lock (_syncRoot) _backgroundTasks.Add(task);
        task.GetAwaiter().OnCompleted(() =>
        {
            if (task.IsFaulted)
                _logger.LogError("Bot lifecycle task failed. ErrorType={ErrorType}", task.Exception?.GetBaseException().GetType().Name);
            lock (_syncRoot) _backgroundTasks.Remove(task);
        });
    }

    /// <summary>
    /// Internal classification for one Telegram receiver startup attempt.
    /// </summary>
    /// <remarks>
    /// The hosted service exposes <see cref="StartBotAsync" /> as a boolean API for owner/admin flows, but startup
    /// recovery needs to distinguish temporary Telegram/network failures from permanent token or configuration
    /// problems. These values are intentionally process-local and are never stored in the database.
    /// </remarks>
    private enum BotStartupResult
    {
        /// <summary>
        /// The receiver was created and registered during this attempt.
        /// </summary>
        Started,

        /// <summary>
        /// The receiver was already running when startup was requested.
        /// </summary>
        AlreadyRunning,

        /// <summary>
        /// The bot is disabled, missing a token, or no exact registry entry exists for the requested id.
        /// </summary>
        Skipped,

        /// <summary>
        /// Startup was blocked because another configured runtime bot owns the same Telegram token or username.
        /// </summary>
        DuplicateConflict,

        /// <summary>
        /// Telegram rejected the token as unauthorized or invalid, so automatic retries should not continue.
        /// </summary>
        InvalidToken,

        /// <summary>
        /// Startup failed in a way that may be temporary, such as a network timeout or short Telegram outage.
        /// </summary>
        TransientFailure
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
