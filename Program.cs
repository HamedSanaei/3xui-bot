using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Telegram.Bot;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Adminbot.Domain.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Adminbot.Domain;


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

        builder.Services.AddSingleton<IConfiguration>(configuration);
        builder.Services.AddTransient<PaymentFactory>();

        builder.Services.AddHostedService<TelegramBotService>();

        //services.AddHostedService<ZibalPaymentCheckerService>();

        builder.Services.AddSingleton<UserDbContext>(sp =>
        {
            // Initialize and configure your Dbcontext here
            return new UserDbContext();
        });


        var optionsBuilder = new DbContextOptionsBuilder<CredentialsDbContext>();
        optionsBuilder.UseSqlite("Data Source=./Data/credentials.db;Mode=ReadWrite;Cache=Shared");
        var context = new CredentialsDbContext(optionsBuilder.Options);
        //context.Database.Migrate();

        builder.Services.AddSingleton<CredentialsDbContext>(sp =>
        {
            // Initialize and configure your Dbcontext here
            return new CredentialsDbContext(optionsBuilder.Options);
        });
        builder.Services.AddSingleton<BroadcastManager>();

        if (builder.Environment.IsProduction())
        {
            {
                builder.WebHost.ConfigureKestrel(options =>
                {
                    options.ListenAnyIP(443);
                });
            }
            Console.WriteLine("development");
        }
        else
        {
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(443, listenOptions =>
                {
                    listenOptions.UseHttps("Data/certificate.pfx");
                });
            });
            Console.WriteLine("production");

        }

        builder.Services.AddSingleton<ITelegramBotClient>(sp =>
        {

            return new TelegramBotClient("6034372537:AAGU3YjVo7a5NBoGwyVBy_eiVuQbE0kPFg8");
            // return new TelegramBotClient(configuration["botToken"]);

        });

        builder.Services.AddLogging(builder =>
       {
           // Use a factory to resolve dependencies more cleanly
           builder.Services.AddSingleton<ILoggerProvider>(sp => new TelegramLoggerProvider((_, logLevel) => logLevel >= LogLevel.Information,
               sp.GetRequiredService<ITelegramBotClient>(),
               configuration["loggerChannel"],
               configuration["backupChannel"]
               ));
       });

        var app = builder.Build();
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
}
