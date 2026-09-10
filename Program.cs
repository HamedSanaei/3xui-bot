using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Telegram.Bot;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Adminbot.Domain.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Adminbot.Domain;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;


/// <summary>
/// Application entry point that wires the web server, Telegram bot runtimes, databases, payments,
/// tenant storefront services, background jobs, and logging.
/// </summary>
/// <remarks>
/// Multi-instance execution starts here: configured owned bots are loaded into <see cref="BotRegistry"/>,
/// synced into <c>users.db</c>, tenant bots are hydrated from the database, and
/// <see cref="MultiBotHostedService"/> starts one Telegram receiver per enabled bot.
/// </remarks>
public class Program
{
    /// <summary>
    /// Builds the ASP.NET host, applies migrations, synchronizes bot instances, and starts HTTP plus Telegram processing.
    /// </summary>
    /// <param name="args">Command-line arguments passed by the hosting environment.</param>
    /// <returns>A task that completes when the host shuts down.</returns>
    /// <remarks>
    /// <c>--migration-check</c> exits before host construction and validates both real EF migration models against
    /// isolated temporary databases. Normal startup prints the embedded commit/configuration before loading private
    /// configuration, then preserves the existing migrate-before-receiver ordering.
    ///
    /// In addition to the Telegram operational logger, startup registers a fail-soft daily diagnostic file logger for
    /// warning/error/critical entries. This keeps full exception chains on disk even when channel-noise suppression
    /// intentionally keeps transient delivery and framework traffic out of the private Telegram logger channel.
    /// </remarks>
    static async Task Main(string[] args)
    {
        Console.WriteLine($"[Build] Commit={BuildInfo.Commit}");
        Console.WriteLine($"[Build] Configuration={BuildInfo.Configuration}");

        if (MigrationPreflight.IsRequested(args))
        {
            Environment.ExitCode = await MigrationPreflight.RunAsync(args, Console.Out, CancellationToken.None);
            return;
        }

        // var ucontext = new UserDbContext();
        // ucontext.Database.Migrate();

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddControllers();
        // Build configuration manually
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("./Data/configuration.json", optional: false, reloadOnChange: true)
            .Build();
        var appConfig = configuration.Get<AppConfig>() ?? new AppConfig();
        ReferralConfigurationValidator.ValidateConfigurationAndThrow(configuration);
        ValidateXuiV3LinkChangeConfiguration(appConfig);
        ValidateXuiV3VolumeReminderConfiguration(appConfig);
        ValidateTetraminatorConfiguration(appConfig);
        ValidateTenantStorefrontConfiguration(appConfig);
        ValidateTenantOrderNotificationConfiguration(appConfig);

        ConfigureDatabasePaths(builder.Environment.ContentRootPath, appConfig);
        // Telegrams logs use their own SQLite outbox next to the runtime databases. The path is resolved against
        // the content root (never the shell current directory) so systemctl/reboot restarts always reopen the same
        // durable file, and it is printed at startup like the main database paths.
        var telegramOutboxDatabasePath = Path.Combine(builder.Environment.ContentRootPath, "Data", "telegram-log-outbox.db");
        Console.WriteLine($"[TelegramOutbox] path: {telegramOutboxDatabasePath}");
        ConfigureWebServer(builder, appConfig);

        RegisterApplicationServices(builder.Services, configuration, appConfig, builder.Environment.ContentRootPath);

        builder.Host.UseDefaultServiceProvider(options => { options.ValidateScopes = true; options.ValidateOnBuild = true; });
        var app = builder.Build();
        using (var scope = app.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.Database.Migrate();
            var botRegistry = scope.ServiceProvider.GetRequiredService<BotRegistry>();
            // Sync configured brand bots first, then hydrate runtime-created tenant bots from users.db.
            await SyncBotInstancesAsync(userDb, botRegistry);
            await botRegistry.LoadTenantBotsFromDatabaseAsync(userDb);
            await using var credentialsDb = scope.ServiceProvider.GetRequiredService<CredentialsDbContextFactory>().CreateDbContext();
            credentialsDb.Database.Migrate();
            await credentialsDb.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            await userDb.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        }
        app.MapControllers();
        app.Run();


    }

    /// <summary>Registers the production service graph without starting receivers or opening deployed databases.</summary>
    /// <param name="services">Required application service collection.</param>
    /// <param name="configuration">Runtime configuration; private values must never be written to logs or test output.</param>
    /// <param name="appConfig">Validated application options with resolved absolute database paths.</param>
    /// <param name="contentRootPath">Application content root for local configuration and logging outbox paths.</param>
    /// <remarks>Singletons retain factories only. Legacy coordinated handler graphs are scoped to one execution; state and wallet stores own shorter contexts.
    /// Backup channels resolve nonblank global configuration before the default-owned channel; the dispatcher supplies durable fallback.</remarks>
    public static void RegisterApplicationServices(IServiceCollection services, IConfiguration configuration, AppConfig appConfig, string contentRootPath)
    {
        var telegramOutboxDatabasePath = Path.Combine(contentRootPath, "Data", "telegram-log-outbox.db");
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(appConfig);
        services.AddSingleton<IPaymentGatewayAvailability>(sp =>
            new PaymentGatewayAvailabilityService(
                appConfig,
                Path.Combine(contentRootPath, "Data", "configuration.json"),
                sp.GetRequiredService<ILogger<PaymentGatewayAvailabilityService>>()));
        services.AddSingleton<NowPayments>();
        services.AddScoped<NowPaymentsSettlementService>();
        services.AddSingleton<HooshPay>();
        services.AddScoped<HooshPaySettlementService>();
        services.AddSingleton<Tetraminator>();
        services.AddScoped<TetraminatorSettlementService>();
        services.AddSingleton<UniquePay>();
        services.AddScoped<UniquePaySettlementService>();
        services.AddSingleton<BotContextAccessor>();
        services.AddSingleton<BotRegistry>();
        services.AddSingleton<BotClientProvider>();
        // The outbox dispatcher resolves each bot client by internal BotId at delivery time, so no Telegram client
        // object ever has to survive a restart. The options carry the runtime database paths used by payment backups.
        services.AddSingleton<TelegramLogDispatcher>(sp =>
        {
            var clientProvider = sp.GetRequiredService<BotClientProvider>();
            return new TelegramLogDispatcher(
                botId => new TelegramBotLogSender(clientProvider.GetClient(botId)),
                TelegramLogDispatcherOptions.CreateDefault(
                    telegramOutboxDatabasePath,
                    appConfig.UserDatabasePath,
                    appConfig.CredentialsDatabasePath) with
                {
                    BackupBotId = sp.GetRequiredService<BotRegistry>().DefaultBot?.Id,
                    BackupChannelId = TelegramLogDispatcherOptions.SelectDestination(configuration["backupChannel"], sp.GetRequiredService<BotRegistry>().DefaultBot?.BackupChannel)
                });
        });
        services.AddSingleton<BotRuntimeStatusStore>();
        services.AddSingleton<XuiV3PurchaseService>();
        services.AddSingleton<XuiV3PurchaseSessionStore>();
        services.AddSingleton<UserActivityLogService>();
        services.AddSingleton<UsageAnalyticsService>();
        services.AddSingleton<UsageReportChartRenderer>();
        services.AddSingleton<UsageReportDispatchStore>();
        services.AddSingleton<XuiV3VolumeReminderStateStore>();
        services.AddSingleton<XuiV3RenewalOperationStore>();
        services.AddSingleton<WalletLedgerService>();
        services.AddHostedService<WalletOperationReconciliationService>();
        services.AddSingleton<IReferralNotificationSender, ReferralNotificationSender>();
        services.AddSingleton<ReferralService>();
        services.AddSingleton<GozargahSiteApiClient>();
        services.AddScoped<GozargahSiteSyncService>();
        services.AddScoped<OwnedBotNotificationService>();
        services.AddScoped<SalesAssistantService>();
        services.AddScoped<TenantOrderNotificationDeliveryService>();
        services.AddScoped<TenantBotService>();
        services.AddSingleton<TenantStoreStore>();
        services.AddScoped<TenantAccessService>();
        services.AddScoped<TenantStorefrontFundingAlertService>();
        services.AddScoped<TenantStorefrontFundingAlertDeliveryService>();
        services.AddScoped<TenantProvisioningAttemptCoordinator>();
        services.AddSingleton<XuiV3LinkChangeOperationStore>();
        services.AddScoped<XuiV3BotFlowService>();
        services.AddScoped<XuiV3AdminFlowService>();
        services.AddHostedService<XuiV3AccountExpiryReminderService>();
        services.AddHostedService<XuiV3VolumeExpirationReminderService>();
        services.AddHostedService<XuiV3LinkChangeRecoveryService>();
        services.AddHostedService<XuiV3RenewalRecoveryService>();
        services.AddHostedService<GozargahSiteSyncRetryService>();
        services.AddHostedService<ReferralReconciliationHostedService>();
        services.AddHostedService<WeeklyUsageReportHostedService>();
        // Delivery-only workers read users.db outboxes and Telegram; they cannot repeat settlement or XUI mutations.
        services.AddHostedService<PaymentSettlementNotificationWorker>();
        services.AddHostedService<TenantManualReceiptNotificationWorker>();
        services.AddHostedService<TenantOrderNotificationWorker>();
        services.AddHostedService<TenantStorefrontFundingAlertWorker>();
        services.AddHostedService<TenantStorefrontFundingMonitorHostedService>();
        services.AddSingleton<UniquePayReconciliationHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<UniquePayReconciliationHostedService>());

        services.AddScoped<TelegramBotService>();
        TelegramUpdateScheduler.ValidateConfiguration(appConfig);
        TenantStoreStore.ValidateConfiguration(appConfig);
        services.Configure<HostOptions>(options => options.ShutdownTimeout =
            TimeSpan.FromSeconds(appConfig.TelegramUpdateShutdownDrainSeconds + 30));
        services.AddSingleton<TelegramUpdateInboxStore>(sp =>
        {
            var store = new TelegramUpdateInboxStore(sp.GetRequiredService<UserDbContextFactory>(), sp.GetRequiredService<CredentialsDbContextFactory>());
            sp.GetRequiredService<BotRegistry>().AvailabilityChanged += store.NotifyReady;
            return store;
        });
        services.AddSingleton<ITelegramUpdateExecutor, TelegramUpdateExecutor>();
        services.AddSingleton<TelegramInboxAdminService>();
        services.AddSingleton<TelegramUpdateScheduler>();
        services.AddSingleton<ITelegramUpdateScheduler>(sp => sp.GetRequiredService<TelegramUpdateScheduler>());
        // Hosted services stop in reverse registration order: receivers stop before the scheduler drains accepted work.
        services.AddHostedService(sp => sp.GetRequiredService<TelegramUpdateScheduler>());
        services.AddSingleton<MultiBotHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<MultiBotHostedService>());

        //services.AddHostedService<ZibalPaymentCheckerService>();

        services.AddScoped<UserDbContext>(sp => sp.GetRequiredService<UserDbContextFactory>().CreateDbContext());
        var userContextOptions = new DbContextOptionsBuilder<UserDbContext>()
            .UseSqlite(BuildSqliteConnectionString(appConfig.UserDatabasePath, readWriteCreate: true))
            .Options;
        services.AddSingleton(new UserDbContextFactory(userContextOptions));
        services.AddSingleton<UserStateStore>();
        services.AddScoped<UserWorkflowStore>();


        var optionsBuilder = new DbContextOptionsBuilder<CredentialsDbContext>();
        optionsBuilder.UseSqlite(BuildSqliteConnectionString(appConfig.CredentialsDatabasePath, readWriteCreate: true));
        services.AddSingleton(new CredentialsDbContextFactory(optionsBuilder.Options));
        services.AddSingleton<CredentialsStore>();
        services.AddSingleton<BroadcastManager>();
        services.AddHostedService(sp => sp.GetRequiredService<BroadcastManager>());

        services.AddSingleton<ITelegramBotClient>(sp =>
        {

            return sp.GetRequiredService<BotClientProvider>().GetDefaultClient();

        });

        services.AddLogging(loggingBuilder =>
        {
            // Keep Telegram channel clean: app logs go to Telegram, framework request noise does not.
            loggingBuilder.Services.AddSingleton<ILoggerProvider>(sp => new TelegramLoggerProvider(ShouldSendTelegramLog,
                sp.GetRequiredService<BotRegistry>(),
                sp.GetRequiredService<BotContextAccessor>(),
                configuration["loggerChannel"],
                configuration["backupChannel"],
                sp.GetRequiredService<TelegramLogDispatcher>()
                ));
            // Keep the complete operational diagnostic trail on disk even when the Telegram channel suppresses noise.
            loggingBuilder.Services.AddSingleton<ILoggerProvider>(sp => new DailyErrorFileLoggerProvider(
                configuration,
                sp.GetRequiredService<BotContextAccessor>()));
        });

    }

    /// <summary>
    /// Configures Kestrel for HTTP or HTTPS based on <see cref="AppConfig"/>.
    /// </summary>
    /// <param name="builder">Web application builder whose Kestrel options are being configured.</param>
    /// <param name="appConfig">Application configuration containing port and certificate settings.</param>
    private static void ConfigureWebServer(WebApplicationBuilder builder, AppConfig appConfig)
    {
        builder.WebHost.ConfigureKestrel(options =>
        {
            if (!appConfig.HttpsEnabled)
            {
                var httpPort = appConfig.HttpPort > 0 ? appConfig.HttpPort : 80;
                options.ListenAnyIP(httpPort);
                Console.WriteLine($"[WebServer] HTTP enabled on port {httpPort}.");
                return;
            }

            var httpsPort = appConfig.HttpsPort > 0 ? appConfig.HttpsPort : 443;
            options.ListenAnyIP(httpsPort, listenOptions =>
            {
                var pfxPath = ResolveContentPath(builder.Environment.ContentRootPath, appConfig.HttpsCertificatePfxPath);
                if (!string.IsNullOrWhiteSpace(pfxPath))
                {
                    EnsureFileExists(pfxPath, "HTTPS PFX certificate");
                    listenOptions.UseHttps(pfxPath, appConfig.HttpsCertificatePassword);
                    Console.WriteLine($"[WebServer] HTTPS enabled on port {httpsPort} with PFX certificate.");
                    return;
                }

                var certPath = ResolveContentPath(builder.Environment.ContentRootPath, appConfig.HttpsCertificatePath);
                var keyPath = ResolveContentPath(builder.Environment.ContentRootPath, appConfig.HttpsCertificateKeyPath);
                EnsureFileExists(certPath, "HTTPS certificate");
                EnsureFileExists(keyPath, "HTTPS private key");

                var certificate = X509Certificate2.CreateFromPemFile(certPath, keyPath);
                EnsureCertificateAllowsServerAuthentication(certificate, certPath);
                listenOptions.UseHttps(certificate);
                Console.WriteLine($"[WebServer] HTTPS enabled on port {httpsPort} with PEM certificate for Cloudflare proxy.");
            });
        });
    }

    /// <summary>
    /// Copies configured owned bots from the runtime registry into <c>users.db</c>.
    /// </summary>
    /// <remarks>
    /// This keeps the database representation of first-party bots aligned with <c>configuration.json</c>.
    /// Numeric bot identity is reserved by a unique index so a configured bot cannot reuse a storefront identity before receivers start.
    /// Runtime-created tenant bots are loaded separately by <see cref="BotRegistry.LoadTenantBotsFromDatabaseAsync"/>.
    /// </remarks>
    /// <param name="userDb">Runtime database context that owns the <c>BotInstances</c> table.</param>
    /// <param name="botRegistry">Registry already populated from application configuration.</param>
    /// <returns>A task that completes after configured bot rows are inserted or updated.</returns>
    private static async Task SyncBotInstancesAsync(UserDbContext userDb, BotRegistry botRegistry)
    {
        foreach (var bot in botRegistry.Bots)
        {
            var existing = await userDb.BotInstances.FirstOrDefaultAsync(x => x.Id == bot.Id);
            if (existing == null)
            {
                existing = new BotInstance { Id = bot.Id, CreatedAtUtc = DateTime.UtcNow };
                userDb.BotInstances.Add(existing);
            }

            existing.Username = bot.Username;
            existing.Token = bot.Token;
            existing.TelegramBotId = TelegramBotTokenIdentity.ExtractBotId(bot.Token);
            existing.BrandName = bot.BrandName;
            existing.Type = string.IsNullOrWhiteSpace(bot.Type) ? BotInstanceTypes.Owned : bot.Type;
            existing.Enabled = bot.Enabled;
            existing.IsDefault = bot.IsDefault;
            existing.OwnerTelegramUserId = bot.OwnerTelegramUserId;
            existing.ChannelIdsJson = BotInstanceConfigExtensions.SerializeStringArray(bot.ChannelIds);
            existing.SupportAccount = bot.SupportAccount;
            existing.LoggerChannel = bot.LoggerChannel;
            existing.BackupChannel = bot.BackupChannel;
            existing.IosTutorialJson = BotInstanceConfigExtensions.SerializeStringArray(bot.IosTutorial);
            existing.AndroidTutorialJson = BotInstanceConfigExtensions.SerializeStringArray(bot.AndroidTutorial);
            existing.WindowsTutorialJson = BotInstanceConfigExtensions.SerializeStringArray(bot.WindowsTutorial);
            existing.TenantPriceMarkupPercent = bot.TenantPriceMarkupPercent;
            existing.TenantWelcomeText = bot.TenantWelcomeText;
            existing.TenantMandatoryJoinEnabled = bot.TenantMandatoryJoinEnabled;
            existing.TenantChannelIdsJson = BotInstanceConfigExtensions.SerializeStringArray(bot.TenantChannelIds);
            existing.TenantCardPaymentEnabled = bot.TenantCardPaymentEnabled;
            existing.TenantCardNumber = bot.TenantCardNumber;
            existing.TenantCardHolderName = bot.TenantCardHolderName;
            existing.TenantHooshPayEnabled = bot.TenantHooshPayEnabled;
            existing.TenantNowPaymentsEnabled = bot.TenantNowPaymentsEnabled;
            existing.TenantTetraminatorEnabled = bot.TenantTetraminatorEnabled;
            existing.TenantUniquePayEnabled = bot.TenantUniquePayEnabled;
            existing.UpdatedAtUtc = DateTime.UtcNow;
        }

        await userDb.SaveChangesAsync();
    }

    /// <summary>
    /// Filters framework noise out of Telegram log forwarding while keeping application information and errors.
    /// </summary>
    /// <param name="categoryName">Logger category name.</param>
    /// <param name="logLevel">Log level for the entry.</param>
    /// <returns><c>true</c> when the entry should be forwarded to Telegram.</returns>
    private static bool ShouldSendTelegramLog(string categoryName, LogLevel logLevel)
    {
        if (logLevel == LogLevel.None)
            return false;

        if (string.IsNullOrWhiteSpace(categoryName))
            return logLevel >= LogLevel.Information;

        if (categoryName.StartsWith("Microsoft.", StringComparison.Ordinal) ||
            categoryName.StartsWith("System.", StringComparison.Ordinal))
        {
            return logLevel >= LogLevel.Error;
        }

        return logLevel >= LogLevel.Information;
    }

    /// <summary>
    /// Validates the confirmation, lease, and recovery limits used by durable XUI v3 link changes.
    /// </summary>
    /// <param name="appConfig">
    /// Application configuration bound from <c>Data/configuration.json</c>. Values are expressed in minutes, seconds,
    /// or attempt counts as indicated by their property names and must be within the supported bounds.
    /// </param>
    /// <remarks>
    /// Validation runs before dependency injection and database migration. Older deployments may omit the newly
    /// introduced keys and then use the documented defaults declared on <see cref="AppConfig"/>. Explicit values
    /// outside the supported ranges still fail startup with a precise configuration error.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when any link-change safety value is outside its supported range.</exception>
    private static void ValidateXuiV3LinkChangeConfiguration(AppConfig appConfig)
    {
        ArgumentNullException.ThrowIfNull(appConfig);

        ValidateRange(nameof(appConfig.XuiV3LinkChangeConfirmationMinutes), appConfig.XuiV3LinkChangeConfirmationMinutes, 1, 60);
        ValidateRange(nameof(appConfig.XuiV3LinkChangeRecoveryPollSeconds), appConfig.XuiV3LinkChangeRecoveryPollSeconds, 5, 3600);
        ValidateRange(nameof(appConfig.XuiV3LinkChangeRecoveryMaxAttempts), appConfig.XuiV3LinkChangeRecoveryMaxAttempts, 1, 100);
        ValidateRange(nameof(appConfig.XuiV3LinkChangeRecoveryMaxDelaySeconds), appConfig.XuiV3LinkChangeRecoveryMaxDelaySeconds, 30, 86400);
        ValidateRange(nameof(appConfig.XuiV3LinkChangeLeaseSeconds), appConfig.XuiV3LinkChangeLeaseSeconds, 60, 1800);
    }

    /// <summary>
    /// Validates the configurable complete-list scan interval for XUI v3 volume-expiration reminders.
    /// </summary>
    /// <param name="appConfig">
    /// Startup configuration bound from <c>Data/configuration.json</c>. The interval is measured in whole minutes and
    /// is validated even when the module is disabled so a later enablement cannot activate an unsafe value.
    /// </param>
    /// <remarks>
    /// Values from 5 through 1440 allow responsive warnings without turning the panel clients endpoint into a
    /// high-frequency poll. Older configurations use the documented 30-minute model default and remain disabled until
    /// explicitly enabled.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown before database migration and hosted-service startup when the interval is outside 5 through 1440.
    /// </exception>
    private static void ValidateXuiV3VolumeReminderConfiguration(AppConfig appConfig)
    {
        ArgumentNullException.ThrowIfNull(appConfig);
        ValidateRange(
            nameof(appConfig.VolumeExpirationReminderIntervalMinutes),
            appConfig.VolumeExpirationReminderIntervalMinutes,
            5,
            1440);
        if (appConfig.XuiV3VolumeReminderStateRetentionDays <= 0)
        {
            throw new InvalidOperationException(
                $"Configuration value '{nameof(appConfig.XuiV3VolumeReminderStateRetentionDays)}' must be positive; actual value is {appConfig.XuiV3VolumeReminderStateRetentionDays}.");
        }
    }

    /// <summary>
    /// Validates Tetraminator settings before any bot can expose the live rial gateway.
    /// </summary>
    /// <param name="appConfig">
    /// Application configuration bound from <c>Data/configuration.json</c>. Disabled gateways may omit credentials;
    /// enabled gateways require a secret API key and absolute HTTP callback/API URLs.
    /// </param>
    /// <remarks>
    /// Existing invoices remain settleable after the switch is disabled, so runtime settlement code does not use
    /// <see cref="AppConfig.TetraminatorEnabled"/> as a paid-invoice guard.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when enabled gateway settings are missing or unsafe.</exception>
    private static void ValidateTetraminatorConfiguration(AppConfig appConfig)
    {
        ArgumentNullException.ThrowIfNull(appConfig);
        ValidateRange(nameof(appConfig.TetraminatorRequestTimeoutSeconds), appConfig.TetraminatorRequestTimeoutSeconds, 5, 120);
        ValidateRange(nameof(appConfig.TetraminatorInquiryRetryCount), appConfig.TetraminatorInquiryRetryCount, 0, 5);
        if (appConfig.TetraminatorMinimumAmountToman < 50000)
            throw new InvalidOperationException("Configuration value 'TetraminatorMinimumAmountToman' cannot be below the provider minimum of 50000 toman.");

        if (!appConfig.TetraminatorEnabled)
            return;
        if (string.IsNullOrWhiteSpace(appConfig.TetraminatorApiKey))
            throw new InvalidOperationException("Tetraminator is enabled but 'tetraminatorApiKey' is missing.");
        if (!IsAbsoluteHttpUrl(appConfig.TetraminatorApiBaseUrl))
            throw new InvalidOperationException("Tetraminator is enabled but 'tetraminatorApiBaseUrl' is not an absolute HTTP/HTTPS URL.");
        if (!IsAbsoluteHttpUrl(appConfig.TetraminatorCallbackUrl))
            throw new InvalidOperationException("Tetraminator is enabled but 'tetraminatorCallbackUrl' is not an absolute HTTP/HTTPS URL.");
    }

    /// <summary>
    /// Validates tenant storefront access settings before any bot can gate customer messages.
    /// </summary>
    /// <param name="appConfig">
    /// Application configuration bound from <c>Data/configuration.json</c>. The site-wallet threshold is measured in
    /// Iranian toman and is validated even when no storefront is configured so a later storefront cannot activate an
    /// unsafe value.
    /// </param>
    /// <remarks>
    /// A negative threshold would make the website-based storefront debt gate pass for every wallet balance and is
    /// therefore rejected rather than silently clamped to zero. Older configurations that omit the key keep the
    /// documented default declared on <see cref="AppConfig.TenantMinimumSiteWalletToman"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown before database migration and hosted-service startup when the configured threshold is below zero.
    /// </exception>
    private static void ValidateTenantStorefrontConfiguration(AppConfig appConfig)
    {
        ArgumentNullException.ThrowIfNull(appConfig);
        if (appConfig.TenantMinimumSiteWalletToman < 0)
            throw new InvalidOperationException("Configuration value 'tenantMinimumSiteWalletToman' cannot be negative.");
        if (appConfig.TenantUnderfundedCustomerAttemptNotificationCooldownMinutes <= 0)
            throw new InvalidOperationException("Configuration value 'tenantUnderfundedCustomerAttemptNotificationCooldownMinutes' must be positive.");
        if (appConfig.TenantStorefrontFundingAlertRetentionDays <= 0)
        {
            throw new InvalidOperationException(
                $"Configuration value '{nameof(appConfig.TenantStorefrontFundingAlertRetentionDays)}' must be positive; actual value is {appConfig.TenantStorefrontFundingAlertRetentionDays}.");
        }
        if (appConfig.TenantStorefrontFundingMonitorIntervalMinutes <= 0)
        {
            throw new InvalidOperationException(
                $"Configuration value '{nameof(appConfig.TenantStorefrontFundingMonitorIntervalMinutes)}' must be positive; actual value is {appConfig.TenantStorefrontFundingMonitorIntervalMinutes}.");
        }
    }

    /// <summary>
    /// Validates tenant notification outbox retention before the delivery worker can delete delivered rows.
    /// </summary>
    /// <param name="appConfig">
    /// Application configuration bound from <c>Data/configuration.json</c>. A missing key keeps the documented
    /// 30-day default declared on <see cref="AppConfig.TenantOrderNotificationRetentionDays"/>.
    /// </param>
    /// <remarks>
    /// A non-positive retention would make the worker either delete delivered history immediately or disable the
    /// bounded cleanup silently; both are rejected at startup instead.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown before database migration and hosted-service startup when the retention is not positive.
    /// </exception>
    private static void ValidateTenantOrderNotificationConfiguration(AppConfig appConfig)
    {
        ArgumentNullException.ThrowIfNull(appConfig);
        if (appConfig.TenantOrderNotificationRetentionDays <= 0)
        {
            throw new InvalidOperationException(
                $"Configuration value '{nameof(appConfig.TenantOrderNotificationRetentionDays)}' must be positive; actual value is {appConfig.TenantOrderNotificationRetentionDays}.");
        }
    }

    /// <summary>
    /// Checks whether a configured provider endpoint is an absolute HTTP or HTTPS URL.
    /// </summary>
    /// <param name="value">Raw configuration URL; null and relative values are invalid.</param>
    /// <returns><c>true</c> for absolute HTTP/HTTPS URLs; otherwise <c>false</c>.</returns>
    private static bool IsAbsoluteHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    /// <summary>
    /// Rejects one integer configuration value outside an inclusive safety range.
    /// </summary>
    /// <param name="name">Configuration property name shown in the startup error.</param>
    /// <param name="value">Configured integer value.</param>
    /// <param name="minimum">Smallest accepted inclusive value.</param>
    /// <param name="maximum">Largest accepted inclusive value.</param>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="value"/> is outside the range.</exception>
    private static void ValidateRange(string name, int value, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
        {
            throw new InvalidOperationException(
                $"Configuration value '{name}' must be between {minimum} and {maximum}; actual value is {value}.");
        }
    }

    /// <summary>
    /// Resolves configured database paths, creates their directories, and gives <see cref="UserDbContext"/>
    /// its active <c>users.db</c> path.
    /// </summary>
    /// <param name="contentRootPath">Application content root used to resolve relative paths.</param>
    /// <param name="appConfig">Mutable application config object whose database paths are normalized.</param>
    private static void ConfigureDatabasePaths(string contentRootPath, AppConfig appConfig)
    {
        appConfig.UserDatabasePath = ResolveContentPath(contentRootPath, appConfig.UserDatabasePath) ??
                                     ResolveContentPath(contentRootPath, "./Data/users.db");
        appConfig.CredentialsDatabasePath = ResolveContentPath(contentRootPath, appConfig.CredentialsDatabasePath) ??
                                            ResolveContentPath(contentRootPath, "./Data/credentials.db");

        EnsureDirectoryForFile(appConfig.UserDatabasePath);
        EnsureDirectoryForFile(appConfig.CredentialsDatabasePath);

        UserDbContext.ConfigureDatabasePath(appConfig.UserDatabasePath);

        Console.WriteLine($"[Database] users.db path: {appConfig.UserDatabasePath}");
        Console.WriteLine($"[Database] credentials.db path: {appConfig.CredentialsDatabasePath}");
    }

    /// <summary>
    /// Builds a SQLite connection string with private cache, a five-second busy timeout, and the requested open mode.
    /// </summary>
    /// <param name="databasePath">Absolute database file path.</param>
    /// <param name="readWriteCreate">When true the database may be created; otherwise it must already exist.</param>
    /// <returns>SQLite connection string used by EF Core.</returns>
    /// <remarks>Both application databases use private-cache SQLite connections with a five-second busy timeout; WAL is initialized before receivers start.</remarks>
    private static string BuildSqliteConnectionString(string databasePath, bool readWriteCreate)
    {
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = readWriteCreate
                ? Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate
                : Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
            Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Private,
            DefaultTimeout = 5
        };

        return builder.ToString();
    }

    /// <summary>
    /// Resolves a configured path relative to the application content root.
    /// </summary>
    /// <param name="contentRootPath">Base path for relative file paths.</param>
    /// <param name="configuredPath">Configured absolute or relative path.</param>
    /// <returns>Absolute path, or null when the configured path is empty.</returns>
    private static string ResolveContentPath(string contentRootPath, string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;

        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(contentRootPath, configuredPath));
    }

    /// <summary>
    /// Verifies that a required file exists before Kestrel tries to use it.
    /// </summary>
    /// <param name="path">File path to check.</param>
    /// <param name="label">Human-readable file label used in the exception message.</param>
    private static void EnsureFileExists(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException($"{label} file was not found.", path);
    }

    /// <summary>
    /// Creates the parent directory for a configured file path when it does not already exist.
    /// </summary>
    /// <param name="path">File path whose directory should exist.</param>
    private static void EnsureDirectoryForFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    /// <summary>
    /// Validates that a PEM/PFX certificate can be used by Kestrel for server TLS.
    /// </summary>
    /// <param name="certificate">Certificate loaded from configuration.</param>
    /// <param name="path">Certificate path used for diagnostics.</param>
    /// <exception cref="InvalidOperationException">Thrown when EKU only allows client authentication or lacks server authentication.</exception>
    private static void EnsureCertificateAllowsServerAuthentication(X509Certificate2 certificate, string path)
    {
        const string serverAuthenticationOid = "1.3.6.1.5.5.7.3.1";
        const string clientAuthenticationOid = "1.3.6.1.5.5.7.3.2";

        var ekuExtension = certificate.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .FirstOrDefault();

        if (ekuExtension == null)
            return;

        var usages = ekuExtension.EnhancedKeyUsages
            .Cast<Oid>()
            .Select(oid => oid.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        if (usages.Contains(serverAuthenticationOid))
            return;

        var usageList = string.Join(", ", usages);
        var hint = usages.Contains(clientAuthenticationOid)
            ? "This looks like a Cloudflare Authenticated Origin Pull client certificate, not a Cloudflare Origin Server certificate."
            : "The certificate does not allow TLS Web Server Authentication.";

        throw new InvalidOperationException(
            $"HTTPS certificate cannot be used by Kestrel as a server certificate. Path='{path}', EKU='{usageList}'. {hint} Create a Cloudflare Origin Server Certificate for '*.tofanservice.ir' and 'tofanservice.ir'.");
    }
}
