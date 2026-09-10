using Adminbot.Domain;
using Adminbot.Utils;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using Xunit;

public sealed partial class ConcurrencyTests
{
    [Fact]
    public async Task Expired_claim_before_send_started_returns_to_pending_without_invoking_sender()
    {
        using var databases = new Databases();
        var tenant = FundingTenant("tenant-pre-send-crash");
        await InsertAlertRowAsync(databases, tenant, new TenantStorefrontFundingAlert
        {
            BusinessKey = $"tenant-funding:{tenant.Id}:episode:1:transition", TenantBotId = tenant.Id,
            OwnerTelegramUserId = 711, TenantBotUsername = tenant.Username, Kind = TenantStorefrontFundingAlertKinds.UnderfundedTransition,
            MinimumSiteWalletToman = 200_000, Status = TenantStorefrontFundingAlertStatuses.Processing,
            AttemptCount = 1, ClaimToken = "stale-claim", LeaseUntilUtc = DateTime.UtcNow.AddMinutes(-5),
            SendStartedAtUtc = null
        });
        await using (var seed = databases.Users.CreateDbContext())
        {
            seed.TenantStorefrontFundingAlertStates.Add(new TenantStorefrontFundingAlertState
            { TenantBotId = tenant.Id, IsUnderfunded = true, EpisodeNumber = 1 });
            await seed.SaveChangesAsync();
        }
        var sender = new ThrowingFundingAlertSender();
        var worker = new TenantStorefrontFundingAlertWorker(databases.Users, sender, NullLogger<TenantStorefrontFundingAlertWorker>.Instance);

        // The expired pre-send claim is safely recycled without invoking the sender.
        await worker.RecoverExpiredClaimsAsync();
        Assert.Equal(0, sender.Count);
        await using (var verify = databases.Users.CreateDbContext())
        {
            var row = await verify.TenantStorefrontFundingAlerts.SingleAsync();
            Assert.Equal(TenantStorefrontFundingAlertStatuses.Pending, row.Status);
            Assert.Null(row.ClaimToken);
            Assert.Null(row.LeaseUntilUtc);
            Assert.Null(row.SendStartedAtUtc);
            Assert.NotNull(row.NextAttemptAtUtc);
            Assert.True(row.NextAttemptAtUtc.Value >= DateTime.UtcNow.AddSeconds(-5));
        }

        // The recycled row is retryable: the next scan claims and delivers it exactly once.
        var counting = new CountingFundingAlertSender();
        var retryWorker = new TenantStorefrontFundingAlertWorker(databases.Users, counting, NullLogger<TenantStorefrontFundingAlertWorker>.Instance);
        Assert.Equal(1, await retryWorker.ProcessOnceAsync());
        Assert.Equal(1, counting.Count);
        await using var delivered = databases.Users.CreateDbContext();
        Assert.Equal(TenantStorefrontFundingAlertStatuses.Delivered, (await delivered.TenantStorefrontFundingAlerts.SingleAsync()).Status);
    }

    [Fact]
    public async Task Expired_claim_after_send_started_becomes_delivery_uncertain_and_never_replays()
    {
        using var databases = new Databases();
        var tenant = FundingTenant("tenant-possible-send-crash");
        await InsertAlertRowAsync(databases, tenant, new TenantStorefrontFundingAlert
        {
            BusinessKey = $"tenant-funding:{tenant.Id}:episode:1:transition", TenantBotId = tenant.Id,
            OwnerTelegramUserId = 711, TenantBotUsername = tenant.Username, Kind = TenantStorefrontFundingAlertKinds.UnderfundedTransition,
            MinimumSiteWalletToman = 200_000, Status = TenantStorefrontFundingAlertStatuses.Processing,
            AttemptCount = 1, ClaimToken = "stale-claim", LeaseUntilUtc = DateTime.UtcNow.AddMinutes(-5),
            SendStartedAtUtc = DateTime.UtcNow.AddMinutes(-4)
        });
        var sender = new CountingFundingAlertSender();
        var worker = new TenantStorefrontFundingAlertWorker(databases.Users, sender, NullLogger<TenantStorefrontFundingAlertWorker>.Instance);

        Assert.Equal(0, await worker.ProcessOnceAsync());
        Assert.Equal(0, sender.Count);
        await using var verify = databases.Users.CreateDbContext();
        var row = await verify.TenantStorefrontFundingAlerts.SingleAsync();
        Assert.Equal(TenantStorefrontFundingAlertStatuses.DeliveryUncertain, row.Status);
        Assert.Contains("possible_delivery", row.LastError);
        Assert.Null(row.ClaimToken);
        Assert.Null(row.LeaseUntilUtc);

        // A second scan must not claim the uncertain row.
        Assert.Equal(0, await worker.ProcessOnceAsync());
        Assert.Equal(0, sender.Count);
    }

    [Fact]
    public async Task Normal_send_reaches_delivered_and_delivered_row_never_replays()
    {
        using var databases = new Databases();
        var service = FundingAlertService(databases, FundingConfiguration());
        var tenant = FundingTenant("tenant-normal-delivery");
        await service.ObserveAsync(tenant, FundingEvaluation(TenantAccessDecision.InsufficientFunding, 0, 10_000, true, 200_000), false, true, default);

        var sender = new CountingFundingAlertSender();
        var worker = new TenantStorefrontFundingAlertWorker(databases.Users, sender, NullLogger<TenantStorefrontFundingAlertWorker>.Instance);
        Assert.Equal(1, await worker.ProcessOnceAsync());
        Assert.Equal(1, sender.Count);

        await using (var verify = databases.Users.CreateDbContext())
        {
            var row = await verify.TenantStorefrontFundingAlerts.SingleAsync();
            Assert.Equal(TenantStorefrontFundingAlertStatuses.Delivered, row.Status);
            Assert.NotNull(row.DeliveredAtUtc);
            Assert.Null(row.SendStartedAtUtc);
            Assert.Equal(1, row.EpisodeNumber);
        }

        Assert.Equal(0, await worker.ProcessOnceAsync());
        Assert.Equal(1, sender.Count);
    }

    [Fact]
    public async Task Recovery_cancels_claim_before_send_started_and_blocks_send_start_conditional()
    {
        using var databases = new Databases();
        var tenant = FundingTenant("tenant-claim-recovery");
        var token = "live-claim";
        await InsertAlertRowAsync(databases, tenant, new TenantStorefrontFundingAlert
        {
            BusinessKey = $"tenant-funding:{tenant.Id}:episode:1:transition", TenantBotId = tenant.Id,
            OwnerTelegramUserId = 711, TenantBotUsername = tenant.Username, Kind = TenantStorefrontFundingAlertKinds.UnderfundedTransition,
            MinimumSiteWalletToman = 200_000, Status = TenantStorefrontFundingAlertStatuses.Processing,
            ClaimToken = token, LeaseUntilUtc = DateTime.UtcNow.AddMinutes(2), SendStartedAtUtc = null
        });
        var service = FundingAlertService(databases, FundingConfiguration());
        var allowed = FundingEvaluation(TenantAccessDecision.Allowed, 50_000, 100_000, true, 200_000);
        await service.ObserveAsync(tenant, allowed, false, true, default);

        await using (var verify = databases.Users.CreateDbContext())
        {
            var row = await verify.TenantStorefrontFundingAlerts.SingleAsync();
            Assert.Equal(TenantStorefrontFundingAlertStatuses.Cancelled, row.Status);
            Assert.Null(row.ClaimToken);
        }

        var sender = new CountingFundingAlertSender();
        var worker = new TenantStorefrontFundingAlertWorker(databases.Users, sender, NullLogger<TenantStorefrontFundingAlertWorker>.Instance);
        int id;
        await using (var db = databases.Users.CreateDbContext()) id = (await db.TenantStorefrontFundingAlerts.SingleAsync()).Id;
        var alert = new TenantStorefrontFundingAlert { Id = id, ClaimToken = token };
        Assert.False(await worker.MarkSendStartedAsync(alert));
        Assert.Equal(0, sender.Count);
    }

    [Fact]
    public async Task Mark_send_started_conditional_persists_phase_once_for_matching_claim()
    {
        using var databases = new Databases();
        var tenant = FundingTenant("tenant-send-start");
        var token = "live-claim";
        await InsertAlertRowAsync(databases, tenant, new TenantStorefrontFundingAlert
        {
            BusinessKey = $"tenant-funding:{tenant.Id}:episode:1:transition", TenantBotId = tenant.Id,
            OwnerTelegramUserId = 711, TenantBotUsername = tenant.Username, Kind = TenantStorefrontFundingAlertKinds.UnderfundedTransition,
            MinimumSiteWalletToman = 200_000, Status = TenantStorefrontFundingAlertStatuses.Processing,
            ClaimToken = token, LeaseUntilUtc = DateTime.UtcNow.AddMinutes(2), SendStartedAtUtc = null
        });
        var worker = new TenantStorefrontFundingAlertWorker(databases.Users, new CountingFundingAlertSender(), NullLogger<TenantStorefrontFundingAlertWorker>.Instance);
        int id;
        await using (var db = databases.Users.CreateDbContext()) id = (await db.TenantStorefrontFundingAlerts.SingleAsync()).Id;
        var alert = new TenantStorefrontFundingAlert { Id = id, ClaimToken = token };

        Assert.True(await worker.MarkSendStartedAsync(alert));
        Assert.True(await worker.MarkSendStartedAsync(alert) == false);

        await using var verify = databases.Users.CreateDbContext();
        var row = await verify.TenantStorefrontFundingAlerts.SingleAsync();
        Assert.NotNull(row.SendStartedAtUtc);
    }

    [Fact]
    public async Task Delivered_retention_deletes_only_old_delivered_rows_and_is_bounded_to_100()
    {
        using var databases = new Databases();
        var tenant = FundingTenant("tenant-retention");
        await using (var db = databases.Users.CreateDbContext())
        {
            db.TenantStorefrontFundingAlertStates.Add(new TenantStorefrontFundingAlertState { TenantBotId = tenant.Id, IsUnderfunded = true, EpisodeNumber = 1 });
            for (var i = 0; i < 150; i++)
            {
                db.TenantStorefrontFundingAlerts.Add(new TenantStorefrontFundingAlert
                {
                    BusinessKey = $"tenant-funding:{tenant.Id}:retention:{i}", TenantBotId = tenant.Id,
                    OwnerTelegramUserId = 711, TenantBotUsername = tenant.Username, Kind = TenantStorefrontFundingAlertKinds.CustomerAttempt,
                    MinimumSiteWalletToman = 200_000, Status = TenantStorefrontFundingAlertStatuses.Delivered,
                    DeliveredAtUtc = DateTime.UtcNow.AddDays(-40)
                });
            }
            for (var i = 0; i < 5; i++)
            {
                db.TenantStorefrontFundingAlerts.Add(new TenantStorefrontFundingAlert
                {
                    BusinessKey = $"tenant-funding:{tenant.Id}:recent:{i}", TenantBotId = tenant.Id,
                    OwnerTelegramUserId = 711, TenantBotUsername = tenant.Username, Kind = TenantStorefrontFundingAlertKinds.CustomerAttempt,
                    MinimumSiteWalletToman = 200_000, Status = TenantStorefrontFundingAlertStatuses.Delivered,
                    DeliveredAtUtc = DateTime.UtcNow.AddDays(-1)
                });
            }
            db.TenantStorefrontFundingAlerts.Add(new TenantStorefrontFundingAlert
            {
                BusinessKey = $"tenant-funding:{tenant.Id}:pending", TenantBotId = tenant.Id, OwnerTelegramUserId = 711,
                TenantBotUsername = tenant.Username, Kind = TenantStorefrontFundingAlertKinds.UnderfundedTransition,
                MinimumSiteWalletToman = 200_000, Status = TenantStorefrontFundingAlertStatuses.Pending,
                NextAttemptAtUtc = DateTime.UtcNow.AddHours(1)
            });
            db.TenantStorefrontFundingAlerts.Add(new TenantStorefrontFundingAlert
            {
                BusinessKey = $"tenant-funding:{tenant.Id}:processing", TenantBotId = tenant.Id, OwnerTelegramUserId = 711,
                TenantBotUsername = tenant.Username, Kind = TenantStorefrontFundingAlertKinds.UnderfundedTransition,
                MinimumSiteWalletToman = 200_000, Status = TenantStorefrontFundingAlertStatuses.Processing,
                ClaimToken = "c", LeaseUntilUtc = DateTime.UtcNow.AddMinutes(2), SendStartedAtUtc = DateTime.UtcNow
            });
            db.TenantStorefrontFundingAlerts.Add(new TenantStorefrontFundingAlert
            {
                BusinessKey = $"tenant-funding:{tenant.Id}:uncertain", TenantBotId = tenant.Id, OwnerTelegramUserId = 711,
                TenantBotUsername = tenant.Username, Kind = TenantStorefrontFundingAlertKinds.UnderfundedTransition,
                MinimumSiteWalletToman = 200_000, Status = TenantStorefrontFundingAlertStatuses.DeliveryUncertain
            });
            db.TenantStorefrontFundingAlerts.Add(new TenantStorefrontFundingAlert
            {
                BusinessKey = $"tenant-funding:{tenant.Id}:review", TenantBotId = tenant.Id, OwnerTelegramUserId = 711,
                TenantBotUsername = tenant.Username, Kind = TenantStorefrontFundingAlertKinds.UnderfundedTransition,
                MinimumSiteWalletToman = 200_000, Status = TenantStorefrontFundingAlertStatuses.ManualReview
            });
            db.TenantStorefrontFundingAlerts.Add(new TenantStorefrontFundingAlert
            {
                BusinessKey = $"tenant-funding:{tenant.Id}:cancelled", TenantBotId = tenant.Id, OwnerTelegramUserId = 711,
                TenantBotUsername = tenant.Username, Kind = TenantStorefrontFundingAlertKinds.UnderfundedTransition,
                MinimumSiteWalletToman = 200_000, Status = TenantStorefrontFundingAlertStatuses.Cancelled
            });
            await db.SaveChangesAsync();
        }
        await using var beforeDb = databases.Users.CreateDbContext();
        var ordersBefore = await beforeDb.TenantBotOrders.CountAsync();

        var worker = new TenantStorefrontFundingAlertWorker(databases.Users, new CountingFundingAlertSender(),
            NullLogger<TenantStorefrontFundingAlertWorker>.Instance, retentionDays: 30);
        // Exactly one bounded batch of 100 is removed per cycle despite 150 eligible rows.
        Assert.Equal(100, await worker.CompactDeliveredAsync());

        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal(50, await verify.TenantStorefrontFundingAlerts.CountAsync(x => x.Status == TenantStorefrontFundingAlertStatuses.Delivered && x.DeliveredAtUtc < DateTime.UtcNow.AddDays(-30)));
        Assert.Equal(5, await verify.TenantStorefrontFundingAlerts.CountAsync(x => x.Status == TenantStorefrontFundingAlertStatuses.Delivered && x.DeliveredAtUtc >= DateTime.UtcNow.AddDays(-30)));
        Assert.Equal(1, await verify.TenantStorefrontFundingAlerts.CountAsync(x => x.Status == TenantStorefrontFundingAlertStatuses.Pending));
        Assert.Equal(1, await verify.TenantStorefrontFundingAlerts.CountAsync(x => x.Status == TenantStorefrontFundingAlertStatuses.Processing));
        Assert.Equal(1, await verify.TenantStorefrontFundingAlerts.CountAsync(x => x.Status == TenantStorefrontFundingAlertStatuses.DeliveryUncertain));
        Assert.Equal(1, await verify.TenantStorefrontFundingAlerts.CountAsync(x => x.Status == TenantStorefrontFundingAlertStatuses.ManualReview));
        Assert.Equal(1, await verify.TenantStorefrontFundingAlerts.CountAsync(x => x.Status == TenantStorefrontFundingAlertStatuses.Cancelled));
        Assert.Equal(ordersBefore, await verify.TenantBotOrders.CountAsync());
        Assert.Equal(1, await verify.TenantStorefrontFundingAlertStates.CountAsync(x => x.TenantBotId == tenant.Id));
    }

    [Fact]
    public async Task Monitor_cycle_guard_skips_overlapping_cycle_while_website_lookup_is_blocked()
    {
        using var databases = new Databases();
        var entered = Signal();
        var release = Signal();
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Run(async context =>
        {
            using var body = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body);
            if (body.RootElement.GetProperty("action").GetString() == "get_user")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(context.RequestAborted);
                await context.Response.WriteAsJsonAsync(new { success = true, data = new { username = "owner", telegram_id = "711", wallet = 300000, ban = 0 } });
                return;
            }
            await context.Response.WriteAsJsonAsync(new { success = false });
        });
        await app.StartAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["gozargahSiteSyncEnabled"] = "true", ["gozargahSiteWalletPaymentsEnabled"] = "true",
            ["gozargahSiteApiBaseUrl"] = app.Urls.Single(), ["gozargahSiteApiKey"] = "test-only",
            ["tenantMinimumSiteWalletToman"] = "200000"
        }).Build();
        var credentials = new CredentialsStore(databases.Credentials);
        await credentials.AddEmptyUser(711);
        var tenant = FundingTenant("tenant-monitor-overlap");
        await using (var db = databases.Users.CreateDbContext()) { db.BotInstances.Add(tenant); await db.SaveChangesAsync(); }
        var sync = new GozargahSiteSyncService(databases.Users, credentials,
            new GozargahSiteApiClient(configuration, NullLogger<GozargahSiteApiClient>.Instance), configuration,
            NullLogger<GozargahSiteSyncService>.Instance);
        var access = new TenantAccessService(databases.Users, credentials, sync, configuration, NullLogger<TenantAccessService>.Instance);
        var alerts = FundingAlertService(databases, configuration);
        var services = new ServiceCollection();
        services.AddSingleton(databases.Users); services.AddSingleton(access); services.AddSingleton(alerts);
        using var provider = services.BuildServiceProvider();
        var monitor = new TenantStorefrontFundingMonitorHostedService(provider.GetRequiredService<IServiceScopeFactory>(),
            configuration.Get<AppConfig>()!, NullLogger<TenantStorefrontFundingMonitorHostedService>.Instance);

        // First cycle blocks inside the website lookup; the second scheduled cycle must skip immediately.
        var first = monitor.RunCycleGuardedAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = monitor.RunCycleGuardedAsync(default);
        await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(first.IsCompleted);
        release.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await app.StopAsync();
    }

    [Fact]
    public async Task Pending_transition_and_customer_attempt_alerts_are_cancelled_on_funding_recovery()
    {
        using var databases = new Databases();
        var service = FundingAlertService(databases, FundingConfiguration());
        var tenant = FundingTenant("tenant-recovery-cancel");
        var underfunded = FundingEvaluation(TenantAccessDecision.InsufficientFunding, 0, 10_000, true, 200_000);
        await service.ObserveAsync(tenant, underfunded, true, true, default);
        await service.ObserveAsync(tenant, FundingEvaluation(TenantAccessDecision.Allowed, 50_000, 10_000, true, 200_000), false, true, default);

        await using (var verify = databases.Users.CreateDbContext())
        {
            Assert.Equal(2, await verify.TenantStorefrontFundingAlerts.CountAsync(x => x.Status == TenantStorefrontFundingAlertStatuses.Cancelled));
            Assert.Empty(await verify.TenantStorefrontFundingAlerts.Where(x => x.Status == TenantStorefrontFundingAlertStatuses.Pending).ToListAsync());
            Assert.False((await verify.TenantStorefrontFundingAlertStates.SingleAsync()).IsUnderfunded);
        }

        var sender = new CountingFundingAlertSender();
        var worker = new TenantStorefrontFundingAlertWorker(databases.Users, sender, NullLogger<TenantStorefrontFundingAlertWorker>.Instance);
        Assert.Equal(0, await worker.ProcessOnceAsync());
        Assert.Equal(0, sender.Count);
    }

    [Fact]
    public async Task Recovery_after_send_started_preserves_conservative_processing_row()
    {
        using var databases = new Databases();
        var tenant = FundingTenant("tenant-send-started-recovery");
        await InsertAlertRowAsync(databases, tenant, new TenantStorefrontFundingAlert
        {
            BusinessKey = $"tenant-funding:{tenant.Id}:episode:1:transition", TenantBotId = tenant.Id,
            OwnerTelegramUserId = 711, TenantBotUsername = tenant.Username, Kind = TenantStorefrontFundingAlertKinds.UnderfundedTransition,
            MinimumSiteWalletToman = 200_000, Status = TenantStorefrontFundingAlertStatuses.Processing,
            ClaimToken = "live-claim", LeaseUntilUtc = DateTime.UtcNow.AddMinutes(2),
            SendStartedAtUtc = DateTime.UtcNow.AddSeconds(-30)
        });
        var service = FundingAlertService(databases, FundingConfiguration());
        await service.ObserveAsync(tenant, FundingEvaluation(TenantAccessDecision.Allowed, 50_000, 10_000, true, 200_000), false, true, default);

        await using var verify = databases.Users.CreateDbContext();
        var row = await verify.TenantStorefrontFundingAlerts.SingleAsync();
        Assert.Equal(TenantStorefrontFundingAlertStatuses.Processing, row.Status);
        Assert.NotNull(row.SendStartedAtUtc);
    }

    [Fact]
    public async Task Stale_episode_alert_is_cancelled_before_send_and_new_episode_alert_delivers()
    {
        using var databases = new Databases();
        var tenant = FundingTenant("tenant-episode-validation");
        await using (var db = databases.Users.CreateDbContext())
        {
            db.TenantStorefrontFundingAlertStates.Add(new TenantStorefrontFundingAlertState { TenantBotId = tenant.Id, IsUnderfunded = true, EpisodeNumber = 2 });
            db.TenantStorefrontFundingAlerts.Add(new TenantStorefrontFundingAlert
            {
                BusinessKey = $"tenant-funding:{tenant.Id}:episode:1:transition", TenantBotId = tenant.Id,
                OwnerTelegramUserId = 711, TenantBotUsername = tenant.Username, Kind = TenantStorefrontFundingAlertKinds.UnderfundedTransition,
                EpisodeNumber = 1, MinimumSiteWalletToman = 200_000, Status = TenantStorefrontFundingAlertStatuses.Pending
            });
            await db.SaveChangesAsync();
        }
        var sender = new CountingFundingAlertSender();
        var worker = new TenantStorefrontFundingAlertWorker(databases.Users, sender, NullLogger<TenantStorefrontFundingAlertWorker>.Instance);
        // The stale row is claimed once, then cancelled by the pre-send episode validation; Telegram is never invoked.
        Assert.Equal(1, await worker.ProcessOnceAsync());
        Assert.Equal(0, sender.Count);
        await using (var verify = databases.Users.CreateDbContext())
            Assert.Equal(TenantStorefrontFundingAlertStatuses.Cancelled, (await verify.TenantStorefrontFundingAlerts.SingleAsync()).Status);

        // Recovery then a new underfunded episode (3) queues a fresh transition alert that can still be delivered.
        var service = FundingAlertService(databases, FundingConfiguration());
        await service.ObserveAsync(tenant, FundingEvaluation(TenantAccessDecision.Allowed, 50_000, 10_000, true, 200_000), false, true, default);
        await service.ObserveAsync(tenant, FundingEvaluation(TenantAccessDecision.InsufficientFunding, 0, 10_000, true, 200_000), false, true, default);
        Assert.Equal(1, await worker.ProcessOnceAsync());
        Assert.Equal(1, sender.Count);
        await using var verify2 = databases.Users.CreateDbContext();
        var delivered = await verify2.TenantStorefrontFundingAlerts.SingleAsync(x => x.Status == TenantStorefrontFundingAlertStatuses.Delivered);
        Assert.Equal(3, delivered.EpisodeNumber);
    }

    [Fact]
    public async Task Monitor_detects_external_wallet_drop_recovery_and_new_episode_without_mutating_finance()
    {
        using var databases = new Databases();
        var wallet = 300_000;
        var getCalls = 0;
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Run(async context =>
        {
            using var body = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body);
            if (body.RootElement.GetProperty("action").GetString() == "get_user")
            {
                Interlocked.Increment(ref getCalls);
                await context.Response.WriteAsJsonAsync(new { success = true, data = new { username = "owner", telegram_id = "711", wallet, ban = 0 } });
                return;
            }
            await context.Response.WriteAsJsonAsync(new { success = false });
        });
        await app.StartAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["gozargahSiteSyncEnabled"] = "true", ["gozargahSiteWalletPaymentsEnabled"] = "true",
            ["gozargahSiteApiBaseUrl"] = app.Urls.Single(), ["gozargahSiteApiKey"] = "test-only",
            ["tenantMinimumSiteWalletToman"] = "200000", ["tenantUnderfundedCustomerAttemptNotificationCooldownMinutes"] = "15"
        }).Build();
        var credentials = new CredentialsStore(databases.Credentials);
        await credentials.AddEmptyUser(711);
        var tenant = FundingTenant("tenant-monitor");
        var second = FundingTenant("tenant-monitor-second"); second.OwnerTelegramUserId = 711;
        await using (var db = databases.Users.CreateDbContext()) { db.BotInstances.Add(tenant); db.BotInstances.Add(second); await db.SaveChangesAsync(); }
        var sync = new GozargahSiteSyncService(databases.Users, credentials,
            new GozargahSiteApiClient(configuration, NullLogger<GozargahSiteApiClient>.Instance), configuration,
            NullLogger<GozargahSiteSyncService>.Instance);
        var access = new TenantAccessService(databases.Users, credentials, sync, configuration, NullLogger<TenantAccessService>.Instance);
        var alerts = FundingAlertService(databases, configuration);
        using var provider = new ServiceCollection().BuildServiceProvider();
        var monitor = new TenantStorefrontFundingMonitorHostedService(provider.GetRequiredService<IServiceScopeFactory>(),
            configuration.Get<AppConfig>()!, NullLogger<TenantStorefrontFundingMonitorHostedService>.Instance);

        // Healthy external wallet: no alerts, one get_user for both storefronts of the same owner.
        Assert.Equal(2, await monitor.RunCycleAsync(databases.Users, access, alerts));
        await using (var db = databases.Users.CreateDbContext()) Assert.Empty(await db.TenantStorefrontFundingAlerts.ToListAsync());
        Assert.Equal(1, Volatile.Read(ref getCalls));

        // External drop below the threshold: one transition alert per storefront of the same episode.
        wallet = 100_000;
        Assert.Equal(2, await monitor.RunCycleAsync(databases.Users, access, alerts));
        await using (var db = databases.Users.CreateDbContext())
        {
            Assert.Equal(2, await db.TenantStorefrontFundingAlerts.CountAsync(x => x.Kind == TenantStorefrontFundingAlertKinds.UnderfundedTransition));
            Assert.Equal(2, await db.TenantStorefrontFundingAlertStates.CountAsync(x => x.IsUnderfunded && x.EpisodeNumber == 1));
        }
        Assert.Equal(2, Volatile.Read(ref getCalls));

        // External recovery: states healthy and pending alerts cancelled.
        wallet = 300_000;
        Assert.Equal(2, await monitor.RunCycleAsync(databases.Users, access, alerts));
        await using (var db = databases.Users.CreateDbContext())
        {
            Assert.Equal(2, await db.TenantStorefrontFundingAlerts.CountAsync(x => x.Status == TenantStorefrontFundingAlertStatuses.Cancelled));
            Assert.Equal(2, await db.TenantStorefrontFundingAlertStates.CountAsync(x => !x.IsUnderfunded));
        }

        // A later external drop starts episode 2 with fresh alerts.
        wallet = 100_000;
        Assert.Equal(2, await monitor.RunCycleAsync(databases.Users, access, alerts));
        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal(2, await verify.TenantStorefrontFundingAlertStates.CountAsync(x => x.IsUnderfunded && x.EpisodeNumber == 2));
        Assert.Equal(4, await verify.TenantStorefrontFundingAlerts.CountAsync());
        Assert.Empty(await verify.TenantBotOrders.ToListAsync());
        Assert.Empty(await verify.TenantBotLedgerEntries.ToListAsync());
        Assert.Empty(await verify.Set<TenantDebtTransfer>().ToListAsync());
        Assert.Empty(await verify.Set<SiteWalletDebitOperation>().ToListAsync());
        await app.StopAsync();
    }

    [Fact]
    public async Task Monitor_skips_underfunded_alert_when_owner_internal_balance_is_positive()
    {
        using var databases = new Databases();
        var wallet = 50_000;
        var getCalls = 0;
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Run(async context =>
        {
            using var body = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body);
            if (body.RootElement.GetProperty("action").GetString() == "get_user")
            {
                Interlocked.Increment(ref getCalls);
                await context.Response.WriteAsJsonAsync(new { success = true, data = new { username = "owner", telegram_id = "711", wallet, ban = 0 } });
                return;
            }
            await context.Response.WriteAsJsonAsync(new { success = false });
        });
        await app.StartAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["gozargahSiteSyncEnabled"] = "true", ["gozargahSiteWalletPaymentsEnabled"] = "true",
            ["gozargahSiteApiBaseUrl"] = app.Urls.Single(), ["gozargahSiteApiKey"] = "test-only",
            ["tenantMinimumSiteWalletToman"] = "200000"
        }).Build();
        var credentials = new CredentialsStore(databases.Credentials);
        await credentials.AddEmptyUser(711);
        await credentials.MutateWalletAsync(711, 50_000, "monitor-balance", botId: "owner-wallet");
        var tenant = FundingTenant("tenant-monitor-balance");
        await using (var db = databases.Users.CreateDbContext()) { db.BotInstances.Add(tenant); await db.SaveChangesAsync(); }
        var sync = new GozargahSiteSyncService(databases.Users, credentials,
            new GozargahSiteApiClient(configuration, NullLogger<GozargahSiteApiClient>.Instance), configuration,
            NullLogger<GozargahSiteSyncService>.Instance);
        var access = new TenantAccessService(databases.Users, credentials, sync, configuration, NullLogger<TenantAccessService>.Instance);
        var alerts = FundingAlertService(databases, configuration);
        using var provider = new ServiceCollection().BuildServiceProvider();
        var monitor = new TenantStorefrontFundingMonitorHostedService(provider.GetRequiredService<IServiceScopeFactory>(),
            configuration.Get<AppConfig>()!, NullLogger<TenantStorefrontFundingMonitorHostedService>.Instance);

        Assert.Equal(1, await monitor.RunCycleAsync(databases.Users, access, alerts));
        Assert.Equal(0, Volatile.Read(ref getCalls));
        Assert.Equal(50_000, await credentials.GetAccountBalance(711));
        await using var verify = databases.Users.CreateDbContext();
        Assert.Empty(await verify.TenantStorefrontFundingAlerts.ToListAsync());
        await app.StopAsync();
    }

    [Fact]
    public void Funding_alert_retention_and_monitor_interval_are_validated()
    {
        Assert.Equal(30, new AppConfig().TenantStorefrontFundingAlertRetentionDays);
        Assert.Equal(5, new AppConfig().TenantStorefrontFundingMonitorIntervalMinutes);
        var validator = typeof(Program).GetMethod("ValidateTenantStorefrontConfiguration", BindingFlags.Static | BindingFlags.NonPublic)!;
        var thrown = Assert.Throws<TargetInvocationException>(() => validator.Invoke(null,
            new object[] { new AppConfig { TenantStorefrontFundingAlertRetentionDays = 0 } }));
        Assert.IsType<InvalidOperationException>(thrown.InnerException);
        var thrownInterval = Assert.Throws<TargetInvocationException>(() => validator.Invoke(null,
            new object[] { new AppConfig { TenantStorefrontFundingMonitorIntervalMinutes = 0 } }));
        Assert.IsType<InvalidOperationException>(thrownInterval.InnerException);
        validator.Invoke(null, new object[] { new AppConfig() });
    }

    private static async Task InsertAlertRowAsync(Databases databases, BotInstance tenant, TenantStorefrontFundingAlert alert)
    {
        await using var db = databases.Users.CreateDbContext();
        db.TenantStorefrontFundingAlerts.Add(alert);
        await db.SaveChangesAsync();
    }

    private sealed class ThrowingFundingAlertSender : ITenantStorefrontFundingAlertSender
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public Task<int?> SendAsync(TenantStorefrontFundingAlert alert, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            throw new InvalidOperationException("sender must not be invoked");
        }
    }
}