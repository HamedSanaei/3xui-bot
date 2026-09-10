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
    public async Task Funding_decision_reuses_existing_or_policy_with_dynamic_minimum()
    {
        using var databases = new Databases();
        var configuration = FundingConfiguration(350_000, 15);
        var credentials = new CredentialsStore(databases.Credentials);
        var sync = new GozargahSiteSyncService(databases.Users, credentials,
            new GozargahSiteApiClient(configuration, NullLogger<GozargahSiteApiClient>.Instance),
            configuration, NullLogger<GozargahSiteSyncService>.Instance);
        var access = new TenantAccessService(databases.Users, credentials, sync, configuration, NullLogger<TenantAccessService>.Instance);

        Assert.Equal(TenantAccessDecision.Allowed, access.ClassifyFundingSnapshot(1, false, null).Decision);
        Assert.Equal(TenantAccessDecision.Allowed, access.ClassifyFundingSnapshot(0, true, 350_000).Decision);
        Assert.Equal(TenantAccessDecision.InsufficientFunding, access.ClassifyFundingSnapshot(0, true, 349_999).Decision);
        Assert.Equal(TenantAccessDecision.InsufficientFunding, access.ClassifyFundingSnapshot(0, false, null).Decision);
        Assert.Equal(350_000, access.ClassifyFundingSnapshot(0, true, 349_999).MinimumSiteWalletToman);

        var alerts = FundingAlertService(databases, configuration);
        var tenant = FundingTenant("tenant-allowed-or-policy");
        await alerts.ObserveAsync(tenant, access.ClassifyFundingSnapshot(1, false, null), true, true, default);
        await alerts.ObserveAsync(tenant, access.ClassifyFundingSnapshot(0, true, 350_000), true, true, default);
        await using var verify = databases.Users.CreateDbContext();
        Assert.Empty(await verify.TenantStorefrontFundingAlerts.ToListAsync());
    }

    [Fact]
    public async Task Underfunded_episode_is_edge_triggered_recovers_and_realerts_after_restart()
    {
        using var databases = new Databases();
        var configuration = FundingConfiguration();
        var tenant = FundingTenant("tenant-episode");
        var underfunded = FundingEvaluation(TenantAccessDecision.InsufficientFunding, 0, 100_000, true, 200_000);
        var allowed = FundingEvaluation(TenantAccessDecision.Allowed, 10_000, 100_000, true, 200_000);
        var service = FundingAlertService(databases, configuration);

        await service.ObserveAsync(tenant, underfunded, false, true, default);
        await service.ObserveAsync(tenant, underfunded, false, true, default);
        await using (var db = databases.Users.CreateDbContext())
            Assert.Equal(1, await db.TenantStorefrontFundingAlerts.CountAsync(x => x.Kind == TenantStorefrontFundingAlertKinds.UnderfundedTransition));

        var restarted = FundingAlertService(databases, configuration);
        await restarted.ObserveAsync(tenant, underfunded, false, true, default);
        await restarted.ObserveAsync(tenant, allowed, false, true, default);
        await restarted.ObserveAsync(tenant, underfunded, false, true, default);

        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal(2, await verify.TenantStorefrontFundingAlerts.CountAsync(x => x.Kind == TenantStorefrontFundingAlertKinds.UnderfundedTransition));
        var state = await verify.TenantStorefrontFundingAlertStates.SingleAsync(x => x.TenantBotId == tenant.Id);
        Assert.True(state.IsUnderfunded);
        Assert.Equal(2, state.EpisodeNumber);
    }

    [Fact]
    public async Task Customer_attempt_alert_has_persisted_cooldown_and_concurrent_single_claim()
    {
        using var databases = new Databases();
        var configuration = FundingConfiguration(cooldown: 15);
        var tenant = FundingTenant("tenant-cooldown");
        var underfunded = FundingEvaluation(TenantAccessDecision.InsufficientFunding, 0, 50_000, true, 200_000);
        var service = FundingAlertService(databases, configuration);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            FundingAlertService(databases, configuration).ObserveAsync(tenant, underfunded, true, true, default)));

        await using (var db = databases.Users.CreateDbContext())
        {
            var first = Assert.Single(await db.TenantStorefrontFundingAlerts.ToListAsync());
            Assert.Equal(TenantStorefrontFundingAlertKinds.CustomerAttempt, first.Kind);
            Assert.Equal($"tenant-funding:{tenant.Id}:episode:1:customer-entry", first.BusinessKey);
            var state = await db.TenantStorefrontFundingAlertStates.SingleAsync(x => x.TenantBotId == tenant.Id);
            Assert.Equal(1, state.EpisodeNumber);
            Assert.NotNull(state.UnderfundedEpisodeNotifiedAtUtc);
            Assert.NotNull(state.LastCustomerAttemptAlertAtUtc);
            state.LastCustomerAttemptAlertAtUtc = DateTime.UtcNow.AddMinutes(-16);
            await db.SaveChangesAsync();
        }

        await service.ObserveAsync(tenant, underfunded, true, true, default);
        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal(2, await verify.TenantStorefrontFundingAlerts.CountAsync(x => x.Kind == TenantStorefrontFundingAlertKinds.CustomerAttempt));
        Assert.Equal(0, await verify.TenantStorefrontFundingAlerts.CountAsync(x => x.Kind == TenantStorefrontFundingAlertKinds.UnderfundedTransition));
    }

    [Fact]
    public async Task Customer_triggered_recovery_starts_new_episode_with_one_customer_entry_alert()
    {
        using var databases = new Databases();
        var service = FundingAlertService(databases, FundingConfiguration());
        var tenant = FundingTenant("tenant-customer-recovery");
        var underfunded = FundingEvaluation(TenantAccessDecision.InsufficientFunding, 0, 50_000, true, 200_000);
        var allowed = FundingEvaluation(TenantAccessDecision.Allowed, 10_000, 50_000, true, 200_000);
        await service.ObserveAsync(tenant, underfunded, true, true, default);
        await service.ObserveAsync(tenant, allowed, false, true, default);
        await service.ObserveAsync(tenant, underfunded, true, true, default);
        await using var db = databases.Users.CreateDbContext();
        var alerts = await db.TenantStorefrontFundingAlerts.OrderBy(x => x.EpisodeNumber).ToListAsync();
        Assert.Equal(2, alerts.Count);
        Assert.All(alerts, x => Assert.Equal(TenantStorefrontFundingAlertKinds.CustomerAttempt, x.Kind));
        Assert.Equal(new[] { 1, 2 }, alerts.Select(x => x.EpisodeNumber));
        Assert.Equal(2, (await db.TenantStorefrontFundingAlertStates.SingleAsync()).EpisodeNumber);
    }

    [Fact]
    public async Task Disabled_or_reset_tenant_and_nonfunding_denials_do_not_queue_low_balance_alert()
    {
        using var databases = new Databases();
        var configuration = FundingConfiguration();
        var service = FundingAlertService(databases, configuration);
        var underfunded = FundingEvaluation(TenantAccessDecision.InsufficientFunding, 0, null, false, 200_000);
        var blocked = FundingEvaluation(TenantAccessDecision.OwnerBlocked, 0, null, false, 200_000);
        var missing = FundingEvaluation(TenantAccessDecision.OwnerMissing, 0, null, false, 200_000);
        var disabled = FundingTenant("tenant-disabled-alert"); disabled.Enabled = false;
        var tokenless = FundingTenant("tenant-tokenless-alert"); tokenless.Token = null;

        Assert.False(TenantBotService.CANQUEUEUNDERFUNDEDALERT(disabled));
        Assert.False(TenantBotService.CANQUEUEUNDERFUNDEDALERT(tokenless));
        Assert.True(TenantBotService.CANQUEUEUNDERFUNDEDALERT(FundingTenant("tenant-enabled-alert")));
        await service.ObserveAsync(disabled, underfunded, true, false, default);
        await service.ObserveAsync(tokenless, underfunded, true, false, default);
        await service.ObserveAsync(FundingTenant("tenant-blocked-owner"), blocked, true, true, default);
        await service.ObserveAsync(FundingTenant("tenant-missing-owner"), missing, true, true, default);

        await using var verify = databases.Users.CreateDbContext();
        Assert.Empty(await verify.TenantStorefrontFundingAlerts.ToListAsync());
    }

    [Fact]
    public void Funding_alert_message_uses_dynamic_minimum_and_unknown_site_balance()
    {
        var alert = new TenantStorefrontFundingAlert
        {
            TenantBotId = "tenant-message", TenantBotUsername = "store_bot", OwnerTelegramUserId = 711,
            Kind = TenantStorefrontFundingAlertKinds.UnderfundedTransition,
            BotBalanceToman = -10_000, SiteWalletToman = null, MinimumSiteWalletToman = 350_000
        };
        var text = TenantStorefrontFundingAlertDeliveryService.BuildMessage(alert);
        Assert.Contains(350_000L.FormatCurrency(), text);
        Assert.Contains("نامشخص / در دسترس نیست", text);
        Assert.Contains("@store_bot", text);
        Assert.DoesNotContain(200_000L.FormatCurrency(), text);
    }

    [Fact]
    public async Task Funding_alert_delivery_uses_owned_default_bot_not_disabled_tenant_transport()
    {
        using var databases = new Databases();
        var (provider, registry, clients) = IncidentProvider(databases);
        await using (provider)
        {
            registry.Upsert(new BotInstance { Id = "tenant-alert-reset", Type = BotInstanceTypes.Tenant, Enabled = false, Token = null, OwnerTelegramUserId = 711 });
            await using var scope = provider.CreateAsyncScope();
            var delivery = scope.ServiceProvider.GetRequiredService<TenantStorefrontFundingAlertDeliveryService>();
            var alert = FundingAlert("tenant-alert-reset", TenantStorefrontFundingAlertKinds.UnderfundedTransition);
            Assert.NotNull(await delivery.SendAsync(alert, default));
            Assert.True(clients.ContainsKey("main"));
            Assert.False(clients.ContainsKey("tenant-alert-reset"));
        }
    }

    [Fact]
    public async Task Monitor_disabled_customer_underfunded_storefront_queues_one_immediate_customer_alert()
    {
        using var databases = new Databases();
        var (provider, registry, clients) = IncidentProvider(databases, fundingMonitorEnabled: false);
        await using (provider)
        {
            var tenant = FundingTenant("tenant-customer-lane");
            await using (var db = databases.Users.CreateDbContext()) { db.BotInstances.Add(tenant); await db.SaveChangesAsync(); }
            registry.Upsert(tenant);
            await using var scope = provider.CreateAsyncScope();
            var credentials = scope.ServiceProvider.GetRequiredService<CredentialsStore>();
            await credentials.AddEmptyUser(711);
            await credentials.AddEmptyUser(722);
            var customer = await credentials.GetUserStatusWithId(722);
            var clientProvider = scope.ServiceProvider.GetRequiredService<BotClientProvider>();
            var botClient = clientProvider.GetClient(tenant.Id);
            var accessor = scope.ServiceProvider.GetRequiredService<BotContextAccessor>();
            using (accessor.Push(new BotRuntimeContext { Config = registry.GetById(tenant.Id), Client = botClient }))
            {
                var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
                var handled = await service.TryHandleTenantUpdateAsync(botClient, Update(98001, 722), customer, new User { Id = 722 }, default)
                    .WaitAsync(TimeSpan.FromSeconds(2));
                Assert.True(handled);
            }
            Assert.Contains(clients[tenant.Id].Texts, x => x.Contains(TenantAccessService.DebtMessage, StringComparison.Ordinal));
            await using var verify = databases.Users.CreateDbContext();
            var alert = Assert.Single(await verify.TenantStorefrontFundingAlerts.ToListAsync());
            Assert.Equal(TenantStorefrontFundingAlertKinds.CustomerAttempt, alert.Kind);
            Assert.Equal($"tenant-funding:{tenant.Id}:episode:1:customer-entry", alert.BusinessKey);
        }
    }

    [Fact]
    public async Task Blocked_owner_telegram_delivery_does_not_block_new_customer_attempt_queue()
    {
        using var databases = new Databases();
        var configuration = FundingConfiguration();
        var tenant = FundingTenant("tenant-blocked-owner-send");
        var underfunded = FundingEvaluation(TenantAccessDecision.InsufficientFunding, 0, 10_000, true, 200_000);
        var service = FundingAlertService(databases, configuration);
        await service.ObserveAsync(tenant, underfunded, false, true, default);
        var sender = new BlockingFundingAlertSender();
        var worker = new TenantStorefrontFundingAlertWorker(databases.Users, sender, NullLogger<TenantStorefrontFundingAlertWorker>.Instance);
        var delivery = worker.ProcessOnceAsync();
        await sender.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await service.ObserveAsync(tenant, underfunded, true, true, default).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(delivery.IsCompleted);
        await using (var db = databases.Users.CreateDbContext())
            Assert.Equal(1, await db.TenantStorefrontFundingAlerts.CountAsync(x => x.Kind == TenantStorefrontFundingAlertKinds.CustomerAttempt));
        sender.Release.TrySetResult();
        Assert.Equal(1, await delivery.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Blocked_owner_transport_does_not_hold_real_customer_handler_lane()
    {
        using var databases = new Databases();
        var (provider, registry, clients) = IncidentProvider(databases);
        await using (provider)
        {
            var tenant = FundingTenant("tenant-real-blocked-lane");
            await using (var db = databases.Users.CreateDbContext()) { db.BotInstances.Add(tenant); await db.SaveChangesAsync(); }
            registry.Upsert(tenant);
            await using var scope = provider.CreateAsyncScope();
            var credentials = scope.ServiceProvider.GetRequiredService<CredentialsStore>();
            await credentials.AddEmptyUser(711); await credentials.AddEmptyUser(722);
            var customer = await credentials.GetUserStatusWithId(722);
            var alertService = scope.ServiceProvider.GetRequiredService<TenantStorefrontFundingAlertService>();
            var underfunded = FundingEvaluation(TenantAccessDecision.InsufficientFunding, 0, null, false, 200_000);
            await alertService.ObserveAsync(tenant, underfunded, false, true, default);
            var sender = new BlockingFundingAlertSender();
            var worker = new TenantStorefrontFundingAlertWorker(databases.Users, sender, NullLogger<TenantStorefrontFundingAlertWorker>.Instance);
            var delivery = worker.ProcessOnceAsync();
            await sender.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var botClient = scope.ServiceProvider.GetRequiredService<BotClientProvider>().GetClient(tenant.Id);
            var accessor = scope.ServiceProvider.GetRequiredService<BotContextAccessor>();
            using (accessor.Push(new BotRuntimeContext { Config = registry.GetById(tenant.Id), Client = botClient }))
            {
                var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
                var handled = await service.TryHandleTenantUpdateAsync(botClient, Update(98002, 722), customer, new User { Id = 722 }, default)
                    .WaitAsync(TimeSpan.FromSeconds(2));
                Assert.True(handled);
            }
            Assert.False(delivery.IsCompleted);
            Assert.Contains(clients[tenant.Id].Texts, x => x.Contains(TenantAccessService.DebtMessage, StringComparison.Ordinal));
            await using (var verify = databases.Users.CreateDbContext())
                Assert.Equal(1, await verify.TenantStorefrontFundingAlerts.CountAsync(x => x.Kind == TenantStorefrontFundingAlertKinds.CustomerAttempt));
            sender.Release.TrySetResult();
            Assert.Equal(1, await delivery.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task First_customer_underfunded_event_delivers_exactly_one_customer_attempt_alert()
    {
        using var databases = new Databases();
        var service = FundingAlertService(databases, FundingConfiguration());
        var tenant = FundingTenant("tenant-first-customer-delivery");
        await service.ObserveAsync(tenant,
            FundingEvaluation(TenantAccessDecision.InsufficientFunding, 0, 10_000, true, 200_000),
            customerAttempt: true, allowUnderfundedAlerts: true, default);
        var sender = new CountingFundingAlertSender();
        var worker = new TenantStorefrontFundingAlertWorker(databases.Users, sender,
            NullLogger<TenantStorefrontFundingAlertWorker>.Instance);
        Assert.Equal(1, await worker.ProcessOnceAsync());
        Assert.Equal(1, sender.Count);
        await using var db = databases.Users.CreateDbContext();
        var row = Assert.Single(await db.TenantStorefrontFundingAlerts.ToListAsync());
        Assert.Equal(TenantStorefrontFundingAlertKinds.CustomerAttempt, row.Kind);
        Assert.Equal(TenantStorefrontFundingAlertStatuses.Delivered, row.Status);
        Assert.Contains("یک مشتری", TenantStorefrontFundingAlertDeliveryService.BuildMessage(row));
    }

    [Fact]
    public async Task Funding_alert_worker_changes_only_alert_outbox_state()
    {
        using var databases = new Databases();
        var credentials = new CredentialsStore(databases.Credentials);
        await credentials.AddEmptyUser(711);
        await credentials.MutateWalletAsync(711, 123_000, "funding-alert-worker-baseline", botId: "owner-wallet");
        var beforeBalance = await credentials.GetAccountBalance(711);
        var service = FundingAlertService(databases, FundingConfiguration());
        await service.ObserveAsync(FundingTenant("tenant-worker-isolation"), FundingEvaluation(TenantAccessDecision.InsufficientFunding, 0, null, false, 200_000), false, true, default);

        await using var beforeDb = databases.Users.CreateDbContext();
        var orders = await beforeDb.TenantBotOrders.CountAsync();
        var ledger = await beforeDb.TenantBotLedgerEntries.CountAsync();
        var creations = await beforeDb.XuiV3CreationOperations.CountAsync();
        var debits = await beforeDb.Set<SiteWalletDebitOperation>().CountAsync();
        await beforeDb.DisposeAsync();

        var sender = new CountingFundingAlertSender();
        var worker = new TenantStorefrontFundingAlertWorker(databases.Users, sender, NullLogger<TenantStorefrontFundingAlertWorker>.Instance);
        Assert.Equal(1, await worker.ProcessOnceAsync());
        Assert.Equal(1, sender.Count);
        Assert.Equal(beforeBalance, await credentials.GetAccountBalance(711));
        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal(orders, await verify.TenantBotOrders.CountAsync());
        Assert.Equal(ledger, await verify.TenantBotLedgerEntries.CountAsync());
        Assert.Equal(creations, await verify.XuiV3CreationOperations.CountAsync());
        Assert.Equal(debits, await verify.Set<SiteWalletDebitOperation>().CountAsync());
    }

    [Fact]
    public async Task Alert_queue_failure_rolls_back_alert_state_and_never_mutates_financial_state()
    {
        using var databases = new Databases();
        var tenant = FundingTenant("tenant-queue-failure");
        var credentials = new CredentialsStore(databases.Credentials);
        await credentials.AddEmptyUser(711);
        await credentials.MutateWalletAsync(711, 55_000, "funding-alert-queue-baseline", botId: "owner-wallet");
        var beforeBalance = await credentials.GetAccountBalance(711);
        await using (var db = databases.Users.CreateDbContext())
        {
            db.TenantStorefrontFundingAlerts.Add(new TenantStorefrontFundingAlert
            {
                BusinessKey = $"tenant-funding:{tenant.Id}:episode:1:transition", TenantBotId = tenant.Id,
                OwnerTelegramUserId = 711, Kind = TenantStorefrontFundingAlertKinds.UnderfundedTransition,
                MinimumSiteWalletToman = 200_000, Status = TenantStorefrontFundingAlertStatuses.Pending
            });
            await db.SaveChangesAsync();
        }
        var service = FundingAlertService(databases, FundingConfiguration());
        await Assert.ThrowsAsync<DbUpdateException>(() => service.ObserveAsync(tenant,
            FundingEvaluation(TenantAccessDecision.InsufficientFunding, 0, null, false, 200_000), false, true, default));
        Assert.Equal(beforeBalance, await credentials.GetAccountBalance(711));
        await using var verify = databases.Users.CreateDbContext();
        Assert.False(await verify.TenantStorefrontFundingAlertStates.AnyAsync(x => x.TenantBotId == tenant.Id));
        Assert.Equal(1, await verify.TenantStorefrontFundingAlerts.CountAsync());
        Assert.Empty(await verify.TenantBotLedgerEntries.ToListAsync());
        Assert.Empty(await verify.XuiV3CreationOperations.ToListAsync());
    }

    [Fact]
    public async Task Real_gozargah_debit_before_after_values_drive_transition_without_double_debit()
    {
        using var databases = new Databases();
        var debitCalls = 0;
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Run(async context =>
        {
            using var body = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body);
            var action = body.RootElement.GetProperty("action").GetString();
            if (action == "get_user")
            {
                await context.Response.WriteAsJsonAsync(new { success = true, data = new { username = "owner", telegram_id = "711", wallet = 400000, ban = 0 } });
                return;
            }
            if (action == "deduct_wallet")
            {
                Interlocked.Increment(ref debitCalls);
                await context.Response.WriteAsJsonAsync(new { success = true, data = new { telegram_id = "711", amount = 250000, previous_wallet = 400000, current_wallet = 150000 } });
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
        var credentials = new CredentialsStore(databases.Credentials); await credentials.AddEmptyUser(711);
        var sync = new GozargahSiteSyncService(databases.Users, credentials,
            new GozargahSiteApiClient(configuration, NullLogger<GozargahSiteApiClient>.Instance), configuration,
            NullLogger<GozargahSiteSyncService>.Instance);
        var debit = await sync.DeductSiteWalletAfterPanelSuccessAsync(711, 250_000, "tenant-base-cost", "funding-cross", "test", default);
        var replay = await sync.DeductSiteWalletAfterPanelSuccessAsync(711, 250_000, "tenant-base-cost", "funding-cross", "test", default);
        Assert.True(debit.Success); Assert.True(replay.Success); Assert.Equal(1, Volatile.Read(ref debitCalls));
        var access = new TenantAccessService(databases.Users, credentials, sync, configuration, NullLogger<TenantAccessService>.Instance);
        var alerts = FundingAlertService(databases, configuration);
        await alerts.ObserveSettlementAsync(FundingTenant("tenant-real-debit"),
            access.ClassifyFundingSnapshot(0, true, (long)debit.BeforeWallet),
            access.ClassifyFundingSnapshot(0, true, (long)debit.AfterWallet), default);
        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal(1, await verify.TenantStorefrontFundingAlerts.CountAsync(x => x.Kind == TenantStorefrontFundingAlertKinds.UnderfundedTransition));
        await app.StopAsync();
    }

    [Fact]
    public async Task Monitor_disabled_does_not_disable_post_settlement_transition_observation()
    {
        using var databases = new Databases();
        var service = FundingAlertService(databases,
            FundingConfiguration(monitorEnabled: false, monitorInterval: 0));
        var tenant = FundingTenant("tenant-disabled-monitor-settlement");
        var before = FundingEvaluation(TenantAccessDecision.Allowed, 0, 300_000, true, 200_000);
        var after = FundingEvaluation(TenantAccessDecision.InsufficientFunding, 0, 50_000, true, 200_000);
        await service.ObserveSettlementAsync(tenant, before, after, default);
        await using var db = databases.Users.CreateDbContext();
        var alert = Assert.Single(await db.TenantStorefrontFundingAlerts.ToListAsync());
        Assert.Equal(TenantStorefrontFundingAlertKinds.UnderfundedTransition, alert.Kind);
        Assert.Equal(1, alert.EpisodeNumber);
    }

    [Fact]
    public void Funding_alert_cooldown_configuration_is_positive_and_validated()
    {
        Assert.Equal(15, new AppConfig().TenantUnderfundedCustomerAttemptNotificationCooldownMinutes);
        var validator = typeof(Program).GetMethod("ValidateTenantStorefrontConfiguration", BindingFlags.Static | BindingFlags.NonPublic)!;
        var thrown = Assert.Throws<TargetInvocationException>(() => validator.Invoke(null,
            new object[] { new AppConfig { TenantUnderfundedCustomerAttemptNotificationCooldownMinutes = 0 } }));
        Assert.IsType<InvalidOperationException>(thrown.InnerException);
        validator.Invoke(null, new object[] { new AppConfig { TenantUnderfundedCustomerAttemptNotificationCooldownMinutes = 15 } });
    }

    private static IConfiguration FundingConfiguration(long minimum = 200_000, int cooldown = 15,
        bool monitorEnabled = true, int monitorInterval = 5) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tenantMinimumSiteWalletToman"] = minimum.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["tenantUnderfundedCustomerAttemptNotificationCooldownMinutes"] = cooldown.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["tenantStorefrontFundingMonitorEnabled"] = monitorEnabled.ToString(),
            ["tenantStorefrontFundingMonitorIntervalMinutes"] = monitorInterval.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["gozargahSiteSyncEnabled"] = "false", ["gozargahSiteWalletPaymentsEnabled"] = "false"
        }).Build();

    private static TenantStorefrontFundingAlertService FundingAlertService(Databases databases, IConfiguration configuration) =>
        new(databases.Users, configuration, NullLogger<TenantStorefrontFundingAlertService>.Instance);

    private static BotInstance FundingTenant(string id) => new()
    {
        Id = id, Username = id + "_bot", Type = BotInstanceTypes.Tenant, OwnerTelegramUserId = 711,
        Enabled = true, Token = Token(Math.Abs(id.GetHashCode()) % 80000 + 20000)
    };

    private static TenantAccessEvaluation FundingEvaluation(TenantAccessDecision decision, long bot, long? site,
        bool siteUsable, long minimum) => new(decision, bot, site, siteUsable, minimum);

    private static TenantStorefrontFundingAlert FundingAlert(string tenantId, string kind) => new()
    {
        BusinessKey = Guid.NewGuid().ToString("N"), TenantBotId = tenantId, TenantBotUsername = tenantId + "_bot",
        OwnerTelegramUserId = 711, Kind = kind, BotBalanceToman = 0, SiteWalletToman = null,
        MinimumSiteWalletToman = 200_000, Status = TenantStorefrontFundingAlertStatuses.Pending
    };

    private sealed class BlockingFundingAlertSender : ITenantStorefrontFundingAlertSender
    {
        public TaskCompletionSource Entered { get; } = Signal();
        public TaskCompletionSource Release { get; } = Signal();
        public async Task<int?> SendAsync(TenantStorefrontFundingAlert alert, CancellationToken cancellationToken)
        {
            Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return 8801;
        }
    }

    private sealed class CountingFundingAlertSender : ITenantStorefrontFundingAlertSender
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public Task<int?> SendAsync(TenantStorefrontFundingAlert alert, CancellationToken cancellationToken)
        { Interlocked.Increment(ref _count); return Task.FromResult<int?>(8802); }
    }
}
