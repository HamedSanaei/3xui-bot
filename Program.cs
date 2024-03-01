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


        await new HostBuilder()
            .ConfigureServices((hostContext, services) =>
            {
                // Build configuration manually
                var configuration = new ConfigurationBuilder()
                    .AddJsonFile("./Data/configuration.json", optional: false, reloadOnChange: true)
                    .Build();

                services.AddSingleton<IConfiguration>(configuration);

                services.AddHostedService<TelegramBotService>();

                services.AddSingleton<UserDbContext>(sp =>
                {
                    // Initialize and configure your Dbcontext here
                    return new UserDbContext();
                });


                var optionsBuilder = new DbContextOptionsBuilder<CredentialsDbContext>();
                optionsBuilder.UseSqlite("Data Source=./Data/credentials.db;Mode=ReadWrite;Cache=Shared");
                var context = new CredentialsDbContext(optionsBuilder.Options);
                context.Database.Migrate();

                services.AddSingleton<CredentialsDbContext>(sp =>
                {
                    // Initialize and configure your Dbcontext here
                    return new CredentialsDbContext(optionsBuilder.Options);
                });

                services.AddSingleton<ITelegramBotClient>(sp =>
                {
                    // Initialize and configure your TelegramBotClient here

                    //weswap
                    //var bot1 = "6019665082:AAGBDkTknaoRvTV8wmpS3xOits3XCcwufqU";

                    //hamed test
                    // var bot2 = "6034372537:AAH_iAh1rLrosds9wGqtq-cdUG7yp4um60c";

                    //var vpnetiranbot = "6651502559:AAGmpsPINM5OB43vANs28ezkhfVLdJZAMcc";

                    return new TelegramBotClient(configuration["botToken"]);
                    // return new TelegramBotClient(bot1);
                });

                services.AddLogging(builder =>
               {
                   // Use a factory to resolve dependencies more cleanly
                   builder.Services.AddSingleton<ILoggerProvider>(sp => new TelegramLoggerProvider((_, logLevel) => logLevel >= LogLevel.None,
                       sp.GetRequiredService<ITelegramBotClient>(),
                       configuration["loggerChannel"],
                       configuration["backupChannel"]
                       ));
               });
            })
            .RunConsoleAsync();
    }
}
