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

/// <summary>Checks queued Zibal payments using fresh execution scopes and durable wallet operation keys.</summary>
/// <remarks>The legacy worker is retained for explicitly configured deployments; HTTP calls never share a database write transaction.</remarks>
public class ZibalPaymentCheckerService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ZibalPaymentCheckerService> _logger;
    private readonly ConcurrentQueue<ZibalPaymentInfo> _paymentQueue;
    private readonly IConfiguration _configuration;
    private readonly AppConfig _appConfig;



    /// <summary>Creates a checker that resolves database and settlement services in disposable scopes.</summary>
    /// <param name="serviceProvider">Root provider used only to create scopes.</param>
    /// <param name="logger">Operational logger; provider credentials must not be logged.</param>
    /// <param name="configuration">Private Zibal runtime settings.</param>
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

    /// <summary>Verifies queued payments and persists authoritative provider facts before invoking wallet settlement.</summary>
    /// <param name="stoppingToken">Host cancellation for local reads and writes.</param>
    /// <returns>A task completing when the current queue has been inspected.</returns>
    /// <remarks>Settlement reloads its own payment row. Save provider facts first, then avoid attaching the old snapshot
    /// over fields written by settlement. The unique credentials receipt prevents duplicate credits across callers.</remarks>
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
                    await dbContext.SaveChangesAsync(stoppingToken);
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

                await dbContext.SaveChangesAsync(stoppingToken);
            }
        }
    }

    /// <summary>Delegates one verified Zibal payment to durable wallet settlement in an independent scope.</summary>
    /// <param name="paymentInfo">Detached or caller-tracked payment identity; settlement reloads the persisted target.</param>
    /// <returns>A task completing after settlement and its notification attempt.</returns>
    /// <remarks>No live context crosses the scope boundary and duplicate callers reuse the same wallet receipt key.</remarks>
    private async Task UpdateUserBalance(ZibalPaymentInfo paymentInfo)
    {
        CredUser credUser;

        using (var scope = _serviceProvider.CreateScope())
        {
            var _credDbContext = scope.ServiceProvider.GetRequiredService<CredentialsStore>();
            credUser = await _credDbContext.GetUserStatusWithId(paymentInfo.TelegramUserId);
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
