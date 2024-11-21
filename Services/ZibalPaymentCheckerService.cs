using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot.Types;
using Microsoft.Extensions.Configuration; // Assuming ZibalPaymentInfo is here

public class ZibalPaymentCheckerService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ZibalPaymentCheckerService> _logger;
    private readonly ConcurrentQueue<ZibalPaymentInfo> _paymentQueue;
    private readonly IConfiguration _configuration;
    private readonly AppConfig _appConfig;



    public ZibalPaymentCheckerService(IServiceProvider serviceProvider, ILogger<ZibalPaymentCheckerService> logger, IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _paymentQueue = new ConcurrentQueue<ZibalPaymentInfo>();
        _configuration = configuration;
        _appConfig = _configuration.Get<AppConfig>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LoadPendingPaymentsAsync();

        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessPendingPaymentsAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task LoadPendingPaymentsAsync()
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var pendingPayments = await dbContext.ZibalPaymentInfos
                .Where(zpi => !zpi.IsPaid && zpi.AttemptsRemaining > 0 && !zpi.IsExpired)
                .ToListAsync();

            foreach (var paymentInfo in pendingPayments)
            {
                _paymentQueue.Enqueue(paymentInfo);
            }
        }

        //_logger.LogInformation("Loaded {Count} pending payments.", _paymentQueue.Count);
    }

    private async Task ProcessPendingPaymentsAsync(CancellationToken stoppingToken)
    {
        while (_paymentQueue.TryDequeue(out var paymentInfo))
        {
            if (stoppingToken.IsCancellationRequested) break;

            var inq = await ZibalAPI.Inquiry(paymentInfo.TrackId, _appConfig.ZibalMerchantCode);

            // paid but not verified
            bool isPaid = inq.Status == 2;

            using (var scope = _serviceProvider.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<UserDbContext>();
                var payment = await dbContext.ZibalPaymentInfos.FindAsync(paymentInfo.Id);

                if (payment == null)
                {
                    Console.WriteLine($"Payment info with ID {paymentInfo.Id} was not found in the database.");
                    // _logger.LogWarning("Payment info with ID {Id} was not found in the database.", paymentInfo.Id);
                    continue;
                }

                else if (isPaid)
                {
                    payment = ZibalAPI.MarkAsPaid(payment, inq);
                    Console.WriteLine($"Payment with ID {paymentInfo.Id} has been marked as paid.");

                    // _logger.LogInformation("Payment with ID {Id} has been marked as paid.", paymentInfo.Id);

                    // Update user's balance or perform any other logic here
                    await UpdateUserBalance(payment);
                }
                else if (inq.Status == 1)
                {
                    // paid and confirned
                    await PrevoiuslyPaid(payment, dbContext, inq);
                }
                else
                {
                    payment.AttemptsRemaining--;
                    if (payment.AttemptsRemaining > 0)
                    {
                        _paymentQueue.Enqueue(payment);
                        // _logger.LogInformation("Payment with ID {Id} was not paid. Attempts remaining: {AttemptsRemaining}", paymentInfo.Id, payment.AttemptsRemaining);
                    }
                    else
                    {
                        // _logger.LogWarning("Payment with ID {Id} has expired after max attempts.", paymentInfo.Id);
                    }
                }

                dbContext.ZibalPaymentInfos.Update(payment);
                await dbContext.SaveChangesAsync(stoppingToken);
            }
        }
    }

    private async Task UpdateUserBalance(ZibalPaymentInfo paymentInfo)
    {
        CredUser credUser;

        using (var scope = _serviceProvider.CreateScope())
        {
            var _credDbContext = scope.ServiceProvider.GetRequiredService<CredentialsDbContext>();
            credUser = await _credDbContext.Users.FindAsync(paymentInfo.TelegramUserId);
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var _bot = scope.ServiceProvider.GetRequiredService<TelegramBotService>();
            await _bot.ZibalAddtoBalance(paymentInfo, _configuration.Get<AppConfig>(), credUser, paymentInfo.ChatId, false);
        }

        // var user = await dbContext.Users.FindAsync(paymentInfo.UserId);
        // if (user != null)
        // {
        //     user.Balance += paymentInfo.Amount;
        //     await dbContext.SaveChangesAsync();
        // }
    }

    private async Task PrevoiuslyPaid(ZibalPaymentInfo paymentInfo, UserDbContext dbContext, InquiryResponse inq)
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var _bot = scope.ServiceProvider.GetRequiredService<TelegramBotService>();
            var payment = await dbContext.ZibalPaymentInfos.FindAsync(paymentInfo.Id);
            payment = ZibalAPI.MarkAsPaid(paymentInfo, inq);
            payment.AttemptsRemaining = 0;
            await dbContext.SaveChangesAsync();
        }
    }
}
