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


class Program
{
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
        builder.Services.AddSingleton<XuiV3PurchaseService>();
        builder.Services.AddSingleton<XuiV3PurchaseSessionStore>();
        builder.Services.AddSingleton<UserActivityLogService>();
        builder.Services.AddSingleton<XuiV3BotFlowService>();
        builder.Services.AddSingleton<XuiV3AdminFlowService>();

        builder.Services.AddHostedService<TelegramBotService>();

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

            return new TelegramBotClient(configuration["botToken"]);

        });

        builder.Services.AddLogging(loggingBuilder =>
        {
            // Keep Telegram channel clean: app logs go to Telegram, framework request noise does not.
            loggingBuilder.Services.AddSingleton<ILoggerProvider>(sp => new TelegramLoggerProvider(ShouldSendTelegramLog,
                sp.GetRequiredService<ITelegramBotClient>(),
                configuration["loggerChannel"],
                configuration["backupChannel"]
                ));
        });

        var app = builder.Build();
        using (var scope = app.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.Database.Migrate();
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

    private static string ResolveContentPath(string contentRootPath, string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;

        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(contentRootPath, configuredPath));
    }

    private static void EnsureFileExists(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException($"{label} file was not found.", path);
    }

    private static void EnsureDirectoryForFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

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
