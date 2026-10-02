namespace Adminbot.Domain;

/// <summary>Global commercial service families; individual catalog prices and storefront settings cannot override them.</summary>
public enum ServiceSalesCategory
{
    /// <summary>Normal metered Internet services.</summary>
    Normal,
    /// <summary>National metered Internet services.</summary>
    National,
    /// <summary>All unlimited fair-usage plans.</summary>
    Unlimited
}

/// <summary>Independent customer admission switches; disabling a sale does not disable renewal or vice versa.</summary>
public enum ServiceSalesOperation
{
    /// <summary>A new customer account purchase, including paid colleague test accounts.</summary>
    Sale,
    /// <summary>A customer-paid extension of an existing account.</summary>
    Renewal
}

/// <summary>Maps validated catalog services and legacy service identifiers to the global commercial policy.</summary>
/// <remarks>Classification never uses a price, quota, customer role, tenant setting or account owner. Pricing and audience validation remain separate.</remarks>
public static class ServiceSalesPolicy
{
    /// <summary>Classifies one authoritative catalog service.</summary>
    /// <param name="service">Required detached service from the shared catalog, already validated by the caller.</param>
    /// <returns>The global normal, national or unlimited category; never a tenant-specific value.</returns>
    /// <exception cref="ArgumentNullException">The service is missing.</exception>
    /// <example><code>var category = ServiceSalesPolicy.GetCategory(resolved.Service);</code></example>
    public static ServiceSalesCategory GetCategory(XuiV3ServiceDefinition service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return GetCategory(service.Key, service.Kind);
    }

    /// <summary>Classifies canonical service keys and legacy metered account types.</summary>
    /// <param name="serviceKey">Catalog key or legacy account type; null or an older metered type belongs to normal service.</param>
    /// <param name="serviceKind">Optional authoritative catalog/metadata kind; unlimited kind takes precedence over a metered key.</param>
    /// <returns>Unlimited for its kind/key, national for the national key, otherwise normal metered service.</returns>
    /// <remarks>The caller must independently resolve and authorize the service; this classifier does not make an unknown key purchasable.</remarks>
    /// <example><code>var category = ServiceSalesPolicy.GetCategory(selection.ServiceKey);</code></example>
    public static ServiceSalesCategory GetCategory(string serviceKey, string serviceKind = null)
        => string.Equals(serviceKind, XuiV3ServiceKinds.Unlimited, StringComparison.OrdinalIgnoreCase)
           || string.Equals(serviceKey, "unlimited", StringComparison.OrdinalIgnoreCase)
            ? ServiceSalesCategory.Unlimited
            : string.Equals(serviceKey, "national", StringComparison.OrdinalIgnoreCase)
                ? ServiceSalesCategory.National : ServiceSalesCategory.Normal;

    /// <summary>Gets a public Persian name for one commercial family.</summary>
    /// <param name="category">Closed global category, never customer-supplied text.</param>
    /// <returns>The normal, national or unlimited label safe for menus and notices.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The category is not recognized.</exception>
    /// <example><code>var label = ServiceSalesPolicy.GetCategoryLabel(ServiceSalesCategory.Normal);</code></example>
    public static string GetCategoryLabel(ServiceSalesCategory category) => category switch
    {
        ServiceSalesCategory.Normal => "نت عادی",
        ServiceSalesCategory.National => "نت ملی",
        ServiceSalesCategory.Unlimited => "نت نامحدود",
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    /// <summary>Explains a global closure without exposing customer, payment or account identifiers.</summary>
    /// <param name="category">The denied global service family.</param>
    /// <param name="operation">Sale or renewal, independently denied by its live switch.</param>
    /// <returns>A customer-facing notice usable in any owned or tenant bot.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The category or operation is unknown.</exception>
    /// <example><code>await client.SendMessage(chat, ServiceSalesPolicy.GetDisabledMessage(category, ServiceSalesOperation.Sale));</code></example>
    public static string GetDisabledMessage(ServiceSalesCategory category, ServiceSalesOperation operation)
        => $"{(operation == ServiceSalesOperation.Sale ? "فروش" : operation == ServiceSalesOperation.Renewal ? "تمدید" : throw new ArgumentOutOfRangeException(nameof(operation)))} {GetCategoryLabel(category)} در حال حاضر توسط مدیریت بسته شده است. لطفاً بعداً دوباره تلاش کنید.";

    /// <summary>Explains a closure for an already resolved service.</summary>
    /// <param name="service">Required validated service selected by the customer or resolved from account metadata.</param>
    /// <param name="operation">The denied sale or renewal operation.</param>
    /// <returns>The same safe global notice regardless of which bot owns the conversation.</returns>
    /// <exception cref="ArgumentNullException">The service is missing.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The operation is unknown.</exception>
    /// <example><code>var notice = ServiceSalesPolicy.GetDisabledMessage(resolved.Service, ServiceSalesOperation.Renewal);</code></example>
    public static string GetDisabledMessage(XuiV3ServiceDefinition service, ServiceSalesOperation operation)
        => GetDisabledMessage(GetCategory(service), operation);
}

/// <summary>Immutable global switch state shared by every owned and tenant bot in the application process.</summary>
/// <param name="NormalSaleEnabled">Whether new normal account sales may be admitted.</param>
/// <param name="NormalRenewalEnabled">Whether new normal renewals may be admitted.</param>
/// <param name="NationalSaleEnabled">Whether new national account sales may be admitted.</param>
/// <param name="NationalRenewalEnabled">Whether new national renewals may be admitted.</param>
/// <param name="UnlimitedSaleEnabled">Whether new sales of any unlimited plan may be admitted.</param>
/// <param name="UnlimitedRenewalEnabled">Whether new unlimited renewals may be admitted.</param>
/// <param name="Revision">Positive process-local revision rejecting stale administrator target-state callbacks.</param>
public sealed record ServiceSalesAvailabilitySnapshot(bool NormalSaleEnabled, bool NormalRenewalEnabled,
    bool NationalSaleEnabled, bool NationalRenewalEnabled, bool UnlimitedSaleEnabled, bool UnlimitedRenewalEnabled, long Revision)
{
    /// <summary>Reads the independent global permission for a category and operation.</summary>
    /// <param name="category">Closed global service family.</param>
    /// <param name="operation">Customer sale or renewal; neither implicitly enables the other.</param>
    /// <returns>True when new work is open; false when closed or either enum is unsupported.</returns>
    /// <remarks>This is an admission decision only, never authorization to repeat or stop an already funded/started operation.</remarks>
    /// <example><code>var open = snapshot.IsEnabled(ServiceSalesCategory.Unlimited, ServiceSalesOperation.Renewal);</code></example>
    public bool IsEnabled(ServiceSalesCategory category, ServiceSalesOperation operation) => (category, operation) switch
    {
        (ServiceSalesCategory.Normal, ServiceSalesOperation.Sale) => NormalSaleEnabled,
        (ServiceSalesCategory.Normal, ServiceSalesOperation.Renewal) => NormalRenewalEnabled,
        (ServiceSalesCategory.National, ServiceSalesOperation.Sale) => NationalSaleEnabled,
        (ServiceSalesCategory.National, ServiceSalesOperation.Renewal) => NationalRenewalEnabled,
        (ServiceSalesCategory.Unlimited, ServiceSalesOperation.Sale) => UnlimitedSaleEnabled,
        (ServiceSalesCategory.Unlimited, ServiceSalesOperation.Renewal) => UnlimitedRenewalEnabled,
        _ => false
    };
}

/// <summary>Result of a persisted administrator transition; a rejected write leaves both live permissions and revision unchanged.</summary>
/// <param name="Applied">True for a persisted target or a valid already-matching target; false for authorization, stale revision or persistence failure.</param>
/// <param name="Snapshot">Current immutable global state, safe to render without credentials.</param>
/// <param name="Message">Safe Persian explanation; never raw file errors or secrets.</param>
public sealed record ServiceSalesToggleResult(bool Applied, ServiceSalesAvailabilitySnapshot Snapshot, string Message);

/// <summary>Live global commercial admission and super-admin-only persistence contract.</summary>
/// <remarks>Missing startup keys default open. Closing affects new unpaid work, not previously issued invoices, committed debit receipts, started panel operations or their settlement/recovery.</remarks>
public interface IServiceSalesAvailability
{
    /// <summary>Gets the lock-free immutable global permissions and administrator callback revision.</summary>
    ServiceSalesAvailabilitySnapshot Snapshot { get; }

    /// <summary>Checks a validated service immediately before new customer admission.</summary>
    /// <param name="service">Required authoritative catalog service; tenant preference cannot override its category.</param>
    /// <param name="operation">The explicit sale or renewal operation being admitted.</param>
    /// <returns>True only while this operation is globally open for the selected category.</returns>
    /// <remarks>Recheck at the final unpaid boundary; pricing resolution alone is not a permission check. Do not use this to block settlement of accepted money or started work.</remarks>
    /// <exception cref="ArgumentNullException">The service is missing.</exception>
    /// <example><code>if (!availability.IsEnabled(resolved.Service, ServiceSalesOperation.Sale)) return;</code></example>
    bool IsEnabled(XuiV3ServiceDefinition service, ServiceSalesOperation operation);

    /// <summary>Authenticates, durably saves and publishes one independent global target-state change.</summary>
    /// <param name="category">Closed service family selected in the super-admin panel.</param>
    /// <param name="operation">Sale or renewal; changing it leaves the opposite operation untouched.</param>
    /// <param name="enabled">True to reopen, false to close new unpaid customer work.</param>
    /// <param name="expectedRevision">Positive revision rendered in the button; a mismatch rejects stale/replayed writes.</param>
    /// <param name="adminActorTelegramUserId">Authenticated Telegram sender id, required in the global configured adminsUserIds allow-list; never tenant owner/recipient authority.</param>
    /// <param name="cancellationToken">Cancellation of write serialization and durable configuration replacement.</param>
    /// <returns>A safe result with the current snapshot. Failed authorization, stale callbacks or disk errors never publish a new live state.</returns>
    /// <remarks>Only one root boolean is edited, preserving unrelated UTF-8 bytes. A committed change applies to all bots without restart and survives restart; it never changes wallets, accounts or financial records.</remarks>
    /// <exception cref="OperationCanceledException">The caller cancels before the durable change completes.</exception>
    /// <example><code>var result = await availability.SetEnabledAsync(ServiceSalesCategory.Normal, ServiceSalesOperation.Sale, false, availability.Snapshot.Revision, callback.From.Id, token);</code></example>
    Task<ServiceSalesToggleResult> SetEnabledAsync(ServiceSalesCategory category, ServiceSalesOperation operation,
        bool enabled, long expectedRevision, long adminActorTelegramUserId, CancellationToken cancellationToken = default);
}

/// <summary>Global commercial permissions backed by the existing atomic, byte-preserving configuration editor.</summary>
/// <remarks>One injected singleton governs every bot. A write is serialized and published only after durable replacement; configured admins alone can change permissions.</remarks>
public sealed class ServiceSalesAvailabilityService : IServiceSalesAvailability
{
    /// <summary>Startup authorization options; runtime permission reads always use the published snapshot instead.</summary>
    private readonly AppConfig _configuration;
    /// <summary>Single application configuration file whose six root booleans persist global permissions.</summary>
    private readonly string _configurationPath;
    private readonly ILogger<ServiceSalesAvailabilityService> _logger;
    /// <summary>Serializes expected-revision validation, durable replacement and snapshot publication.</summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    /// <summary>Immutable current permissions, published only after a successful durable configuration change.</summary>
    private ServiceSalesAvailabilitySnapshot _snapshot;

    /// <summary>Initializes permissions from the six optional default-open startup flags.</summary>
    /// <param name="configuration">Required global startup options and super-admin allow-list, not storefront settings.</param>
    /// <param name="configurationPath">Required writable UTF-8 configuration file path, resolved against the application content root by production DI.</param>
    /// <param name="logger">Required local operational logger; records only actor, category, operation and target, never configuration content.</param>
    /// <remarks>Construction performs no file or financial writes. Isolated callers may supply their own options and private file path; production shares this instance across all flows.</remarks>
    /// <exception cref="ArgumentNullException">Required options or logger is missing.</exception>
    /// <exception cref="ArgumentException">The configuration path is absent or invalid.</exception>
    /// <example><code>var store = new ServiceSalesAvailabilityService(options, configurationPath, logger);</code></example>
    public ServiceSalesAvailabilityService(AppConfig configuration, string configurationPath, ILogger<ServiceSalesAvailabilityService> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        _configurationPath = Path.GetFullPath(configurationPath);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _snapshot = new(configuration.NormalSaleEnabled, configuration.NormalRenewalEnabled,
            configuration.NationalSaleEnabled, configuration.NationalRenewalEnabled,
            configuration.UnlimitedSaleEnabled, configuration.UnlimitedRenewalEnabled, 1);
    }

    /// <inheritdoc />
    public ServiceSalesAvailabilitySnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <inheritdoc />
    public bool IsEnabled(XuiV3ServiceDefinition service, ServiceSalesOperation operation)
        => Snapshot.IsEnabled(ServiceSalesPolicy.GetCategory(service), operation);

    /// <inheritdoc />
    public async Task<ServiceSalesToggleResult> SetEnabledAsync(ServiceSalesCategory category, ServiceSalesOperation operation,
        bool enabled, long expectedRevision, long adminActorTelegramUserId, CancellationToken cancellationToken = default)
    {
        if (adminActorTelegramUserId <= 0 || _configuration.AdminsUserIds?.Contains(adminActorTelegramUserId) != true)
            return new(false, Snapshot, "این بخش فقط برای سوپرادمین‌هاست.");
        if (!Enum.IsDefined(category) || !Enum.IsDefined(operation))
            return new(false, Snapshot, "دکمه نامعتبر است.");
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var current = Snapshot;
            if (current.Revision != expectedRevision)
                return new(false, current, "این پنل قدیمی شده است؛ وضعیت جدید نمایش داده شد.");
            if (current.IsEnabled(category, operation) == enabled)
                return new(true, current, "وضعیت از قبل همین مقدار بود.");
            // Persist first: a read-only or invalid configuration must never falsely close/reopen live customer admission.
            await RootBooleanJsonFileEditor.SetAsync(_configurationPath, GetConfigurationPropertyName(category, operation), enabled, cancellationToken);
            var next = current with
            {
                NormalSaleEnabled = category == ServiceSalesCategory.Normal && operation == ServiceSalesOperation.Sale ? enabled : current.NormalSaleEnabled,
                NormalRenewalEnabled = category == ServiceSalesCategory.Normal && operation == ServiceSalesOperation.Renewal ? enabled : current.NormalRenewalEnabled,
                NationalSaleEnabled = category == ServiceSalesCategory.National && operation == ServiceSalesOperation.Sale ? enabled : current.NationalSaleEnabled,
                NationalRenewalEnabled = category == ServiceSalesCategory.National && operation == ServiceSalesOperation.Renewal ? enabled : current.NationalRenewalEnabled,
                UnlimitedSaleEnabled = category == ServiceSalesCategory.Unlimited && operation == ServiceSalesOperation.Sale ? enabled : current.UnlimitedSaleEnabled,
                UnlimitedRenewalEnabled = category == ServiceSalesCategory.Unlimited && operation == ServiceSalesOperation.Renewal ? enabled : current.UnlimitedRenewalEnabled,
                Revision = current.Revision + 1
            };
            Volatile.Write(ref _snapshot, next);
            _logger.LogInformation("Global service sales permission changed. actor={AdminActor}, category={Category}, operation={Operation}, enabled={Enabled}, revision={Revision}",
                adminActorTelegramUserId, category, operation, enabled, next.Revision);
            return new(true, next, "وضعیت ذخیره شد و روی همه ربات‌های owned و tenant اعمال شد.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.LogError(ex, "Global service sales permission persistence failed. category={Category}, operation={Operation}", category, operation);
            return new(false, Snapshot, "ذخیره تنظیمات ناموفق بود؛ وضعیت فروش و تمدید تغییر نکرد.");
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>Maps one closed category/operation pair to its exact root configuration key.</summary>
    /// <param name="category">Validated global service family.</param>
    /// <param name="operation">Validated customer sale or renewal operation.</param>
    /// <returns>The exact ASCII root boolean property; no arbitrary key is accepted.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The pair is unsupported.</exception>
    /// <example><code>var key = GetConfigurationPropertyName(ServiceSalesCategory.Unlimited, ServiceSalesOperation.Sale);</code></example>
    private static string GetConfigurationPropertyName(ServiceSalesCategory category, ServiceSalesOperation operation) => (category, operation) switch
    {
        (ServiceSalesCategory.Normal, ServiceSalesOperation.Sale) => "normalSaleEnabled",
        (ServiceSalesCategory.Normal, ServiceSalesOperation.Renewal) => "normalRenewalEnabled",
        (ServiceSalesCategory.National, ServiceSalesOperation.Sale) => "nationalSaleEnabled",
        (ServiceSalesCategory.National, ServiceSalesOperation.Renewal) => "nationalRenewalEnabled",
        (ServiceSalesCategory.Unlimited, ServiceSalesOperation.Sale) => "unlimitedSaleEnabled",
        (ServiceSalesCategory.Unlimited, ServiceSalesOperation.Renewal) => "unlimitedRenewalEnabled",
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };
}
