using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Telegram.Bot;
using Microsoft.EntityFrameworkCore;

class Program
{
    static async Task Main(string[] args)
    {
        await new HostBuilder()
            .ConfigureServices((hostContext, services) =>
            {
                services.AddHostedService<TelegramBotService>();
                services.AddSingleton<UserDbContext>(sp =>
                {
                    // Initialize and configure your Dbcontext here
                    return new UserDbContext();
                });

                services.AddSingleton<ITelegramBotClient>(sp =>
                {
                    // Initialize and configure your TelegramBotClient here
                    // var bot1 = "6019665082:AAGBDkTknaoRvTV8wmpS3xOits3XCcwufqU";
                    var bot2 = "6034372537:AAH_iAh1rLrosds9wGqtq-cdUG7yp4um60c";
                    return new TelegramBotClient(bot2);
                });
            })
            .RunConsoleAsync();
    }
}
