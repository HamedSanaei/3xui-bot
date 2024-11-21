using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Telegram.Bot;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Adminbot.Domain.Logging;
using Microsoft.EntityFrameworkCore;

class Program
{
    static async Task Main(string[] args)
    {

        // var ucontext = new UserDbContext();
        // ucontext.Database.Migrate();



        await new HostBuilder()
            .ConfigureServices(async (hostContext, services) =>
            {
                // Build configuration manually
                var configuration = new ConfigurationBuilder()
                    .AddJsonFile("./Data/configuration.json", optional: false, reloadOnChange: true)
                    .Build();

                services.AddSingleton<IConfiguration>(configuration);

                services.AddHostedService<TelegramBotService>();

                //services.AddHostedService<ZibalPaymentCheckerService>();

                services.AddSingleton<UserDbContext>(sp =>
                {
                    // Initialize and configure your Dbcontext here
                    return new UserDbContext();
                });


                var optionsBuilder = new DbContextOptionsBuilder<CredentialsDbContext>();
                optionsBuilder.UseSqlite("Data Source=./Data/credentials.db;Mode=ReadWrite;Cache=Shared");
                var context = new CredentialsDbContext(optionsBuilder.Options);
                //context.Database.Migrate();

                services.AddSingleton<CredentialsDbContext>(sp =>
                {
                    // Initialize and configure your Dbcontext here
                    return new CredentialsDbContext(optionsBuilder.Options);
                });

                services.AddSingleton<ITelegramBotClient>(sp =>
                {
                    // Initialize and configure your TelegramBotClient here

                    //weswap
                    //hamed test




                    return new TelegramBotClient(configuration["botToken"]);

                    // return new TelegramBotClient(bot1);
                });

                services.AddLogging(builder =>
               {
                   // Use a factory to resolve dependencies more cleanly
                   builder.Services.AddSingleton<ILoggerProvider>(sp => new TelegramLoggerProvider((_, logLevel) => logLevel >= LogLevel.Information,
                       sp.GetRequiredService<ITelegramBotClient>(),
                       configuration["loggerChannel"],
                       configuration["backupChannel"]
                       ));
               });
            })
            .RunConsoleAsync();
    }
}
