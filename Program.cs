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
class Program
{
    /// <summary>
    /// Builds the ASP.NET host, applies migrations, synchronizes bot instances, and starts HTTP plus Telegram processing.
    /// </summary>
    /// <param name="args">Command-line arguments passed by the hosting environment.</param>
    /// <returns>A task that completes when the host shuts down.</returns>
    static async Task Main(string[] args)
    {

        // var ucontext = new UserDbContext();
        // ucontext.Database.Migrate();

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddControllers();
        // Build configuration manually
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("./Data/configuration.json", optional: false, reloadOnChange: true)
            .Build();
        var appConfig = configuration.Get<AppConfig>() ?? new AppConfig();

        ConfigureDatabasePaths(builder.Environment.ContentRootPath, appConfig);
        ConfigureWebServer(builder, appConfig);

        builder.Services.AddSingleton<IConfiguration>(configuration);
        builder.Services.AddSingleton<NowPayments>();
        builder.Services.AddSingleton<NowPaymentsSettlementService>();
        builder.Services.AddSingleton<HooshPay>();
        builder.Services.AddSingleton<HooshPaySettlementService>();
        builder.Services.AddSingleton<BotContextAccessor>();
        builder.Services.AddSingleton<BotRegistry>();
        builder.Services.AddSingleton<BotClientProvider>();
        builder.Services.AddSingleton<BotRuntimeStatusStore>();
        builder.Services.AddSingleton<XuiV3PurchaseService>();
        builder.Services.AddSingleton<XuiV3PurchaseSessionStore>();
        builder.Services.AddSingleton<UserActivityLogService>();
        builder.Services.AddSingleton<WalletLedgerService>();
        builder.Services.AddSingleton<GozargahSiteApiClient>();
        builder.Services.AddSingleton<GozargahSiteSyncService>();
        builder.Services.AddSingleton<OwnedBotNotificationService>();
        builder.Services.AddSingleton<SalesAssistantService>();
        builder.Services.AddSingleton<TenantBotService>();
        builder.Services.AddSingleton<XuiV3BotFlowService>();
        builder.Services.AddSingleton<XuiV3AdminFlowService>();
        builder.Services.AddHostedService<XuiV3AccountExpiryReminderService>();
        builder.Services.AddHostedService<GozargahSiteSyncRetryService>();

        builder.Services.AddSingleton<TelegramBotService>();
        builder.Services.AddSingleton<MultiBotHostedService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MultiBotHostedService>());

        //services.AddHostedService<ZibalPaymentCheckerService>();

        builder.Services.AddSingleton<UserDbContext>(sp =>
        {
            // Initialize and configure your Dbcontext here
            return new UserDbContext();
        });


        var optionsBuilder = new DbContextOptionsBuilder<CredentialsDbContext>();
        optionsBuilder.UseSqlite(BuildSqliteConnectionString(appConfig.CredentialsDatabasePath, readWriteCreate: true));
        var context = new CredentialsDbContext(optionsBuilder.Options);
        //context.Database.Migrate();

        builder.Services.AddSingleton<CredentialsDbContext>(sp =>
        {
            // Initialize and configure your Dbcontext here
            return new CredentialsDbContext(optionsBuilder.Options);
        });
        builder.Services.AddSingleton<BroadcastManager>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<BroadcastManager>());

        builder.Services.AddSingleton<ITelegramBotClient>(sp =>
        {

            return sp.GetRequiredService<BotClientProvider>().GetDefaultClient();

        });

        builder.Services.AddLogging(loggingBuilder =>
        {
            // Keep Telegram channel clean: app logs go to Telegram, framework request noise does not.
            loggingBuilder.Services.AddSingleton<ILoggerProvider>(sp => new TelegramLoggerProvider(ShouldSendTelegramLog,
                sp.GetRequiredService<BotClientProvider>(),
                sp.GetRequiredService<BotRegistry>(),
                sp.GetRequiredService<BotContextAccessor>(),
                configuration["loggerChannel"],
                configuration["backupChannel"],
                appConfig
                ));
        });

        var app = builder.Build();
        using (var scope = app.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.Database.Migrate();
            var botRegistry = scope.ServiceProvider.GetRequiredService<BotRegistry>();
            // Sync configured brand bots first, then hydrate runtime-created tenant bots from users.db.
            await SyncBotInstancesAsync(userDb, botRegistry);
            await botRegistry.LoadTenantBotsFromDatabaseAsync(userDb);
            var credentialsDb = scope.ServiceProvider.GetRequiredService<CredentialsDbContext>();
            credentialsDb.Database.Migrate();
        }
        app.MapControllers();
        app.Run();

        // await new HostBuilder()
        //     .ConfigureServices(async (hostContext, services) =>
        //     {
        //         // Build configuration manually
        //         var configuration = new ConfigurationBuilder()
        //             .AddJsonFile("./Data/configuration.json", optional: false, reloadOnChange: true)
        //             .Build();

        //         services.AddSingleton<IConfiguration>(configuration);

        //         services.AddHostedService<TelegramBotService>();

        //         //services.AddHostedService<ZibalPaymentCheckerService>();

        //         services.AddSingleton<UserDbContext>(sp =>
        //         {
        //             // Initialize and configure your Dbcontext here
        //             return new UserDbContext();
        //         });


        //         var optionsBuilder = new DbContextOptionsBuilder<CredentialsDbContext>();
        //         optionsBuilder.UseSqlite("Data Source=./Data/credentials.db;Mode=ReadWrite;Cache=Shared");
        //         var context = new CredentialsDbContext(optionsBuilder.Options);
        //         //context.Database.Migrate();

        //         services.AddSingleton<CredentialsDbContext>(sp =>
        //         {
        //             // Initialize and configure your Dbcontext here
        //             return new CredentialsDbContext(optionsBuilder.Options);
        //         });
        //         services.AddSingleton<BroadcastManager>();


        //         services.AddSingleton<ITelegramBotClient>(sp =>
        //         {
        //             return new TelegramBotClient("6034372537:AAGU3YjVo7a5NBoGwyVBy_eiVuQbE0kPFg8");
        //             // return new TelegramBotClient(configuration["botToken"]);

        //         });

        //         services.AddLogging(builder =>
        //        {
        //            // Use a factory to resolve dependencies more cleanly
        //            builder.Services.AddSingleton<ILoggerProvider>(sp => new TelegramLoggerProvider((_, logLevel) => logLevel >= LogLevel.Information,
        //                sp.GetRequiredService<ITelegramBotClient>(),
        //                configuration["loggerChannel"],
        //                configuration["backupChannel"]
        //                ));
        //        });
        //     })
        //     .RunConsoleAsync();
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
    /// Builds a SQLite connection string with shared cache and the requested open mode.
    /// </summary>
    /// <param name="databasePath">Absolute database file path.</param>
    /// <param name="readWriteCreate">When true the database may be created; otherwise it must already exist.</param>
    /// <returns>SQLite connection string used by EF Core.</returns>
    private static string BuildSqliteConnectionString(string databasePath, bool readWriteCreate)
    {
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = readWriteCreate
                ? Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate
                : Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
            Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Shared
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
