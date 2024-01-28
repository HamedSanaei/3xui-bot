using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Telegram.Bot;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

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

                services.AddSingleton<CredentialsDbContext>(sp =>
                {
                    // Initialize and configure your Dbcontext here
                    return new CredentialsDbContext();
                });

                services.AddSingleton<ITelegramBotClient>(sp =>
                {
                    // Initialize and configure your TelegramBotClient here
                    // var bot1 = "6019665082:AAGBDkTknaoRvTV8wmpS3xOits3XCcwufqU";
                    // var bot2 = "6034372537:AAH_iAh1rLrosds9wGqtq-cdUG7yp4um60c";
                    return new TelegramBotClient(configuration["bot_token"]);
                });
            })
            .RunConsoleAsync();
    }
}
