using System.Net;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

public sealed partial class ConcurrencyTests
{
    /// <summary>Provider bot/gateway choices stay separate and ordered without altering raw response fields or financial state.</summary>
    /// <param name="gateway">Optional synthetic online gateway URL, including whitespace to model disabled merchants.</param>
    /// <param name="legacy">Optional synthetic legacy bot URL.</param>
    /// <param name="expected">Expected selected link, or null when the create response is incomplete.</param>
    [Theory]
    [InlineData(" https://gateway.example/invoice/one ", "https://t.me/fixture_bot?start=one", "https://t.me/fixture_bot?start=one")]
    [InlineData("https://gateway.example/invoice/one", null, "https://gateway.example/invoice/one")]
    [InlineData(null, "https://t.me/fixture_bot?start=one", "https://t.me/fixture_bot?start=one")]
    [InlineData("", "https://t.me/fixture_bot?start=one", "https://t.me/fixture_bot?start=one")]
    [InlineData(" \t ", " https://t.me/fixture_bot?start=one ", "https://t.me/fixture_bot?start=one")]
    [InlineData(null, null, null)]
    [InlineData(" ", " ", null)]
    public void Tetraminator_gateway_link_choices_are_separate_and_backward_compatible(string? gateway, string? legacy, string? expected)
    {
        var response = JsonConvert.DeserializeObject<TetraminatorCreateInvoiceResponse>(new JObject {
            ["status"] = true, ["pay_id"] = "fixture-id", ["gateway_link"] = gateway, ["payment_link"] = legacy
        }.ToString())!;
        Assert.Equal(expected, response.GetPrimaryPaymentLink());
        Assert.Equal(gateway, response.GatewayLink); Assert.Equal(legacy, response.PaymentLink);
        var payment = new TetraminatorPaymentInfo { AmountToman = 100000, PaymentLink = "https://saved.example/invoice/one" };
        payment.Apply(response);
        Assert.Equal(string.IsNullOrWhiteSpace(legacy) ? "https://saved.example/invoice/one" : legacy.Trim(), payment.PaymentLink);
        Assert.Equal(string.IsNullOrWhiteSpace(gateway) ? null : gateway.Trim(), payment.GatewayLink);
        Assert.Equal("fixture-id", payment.PayId); Assert.Equal(100000, payment.AmountToman);
        Assert.Equal(TetraminatorStatuses.Pending, payment.PaymentStatus); Assert.False(payment.IsAddedToBalance);
    }

    /// <summary>Real HTTP creation accepts a gateway-only response, preserves legacy fallback and never replays incomplete/failed POSTs.</summary>
    /// <param name="gateway">Optional provider online URL.</param><param name="legacy">Optional legacy bot URL.</param>
    /// <param name="status">Provider success flag; false must never produce a usable invoice.</param>
    /// <param name="payId">Provider invoice identity; empty remains invalid even with a usable URL.</param>
    /// <param name="httpStatus">Synthetic provider HTTP result.</param><param name="accepted">Whether creation should return a validated response.</param>
    /// <returns>A task after asserting the single POST, unchanged request contract and provider response validation.</returns>
    [Theory]
    [InlineData("https://gateway.example/invoice/one", null, true, "fixture-id", 201, true)]
    [InlineData("https://gateway.example/invoice/one", "https://t.me/fixture_bot?start=one", true, "fixture-id", 201, true)]
    [InlineData(null, "https://t.me/fixture_bot?start=one", true, "fixture-id", 201, true)]
    [InlineData(null, null, true, "fixture-id", 201, false)]
    [InlineData("https://gateway.example/invoice/one", null, false, "fixture-id", 201, false)]
    [InlineData("https://gateway.example/invoice/one", null, true, "", 201, false)]
    [InlineData(null, null, true, "fixture-id", 500, false)]
    public async Task Tetraminator_gateway_link_create_validation_never_replays_post(
        string? gateway, string? legacy, bool status, string payId, int httpStatus, bool accepted)
    {
        var calls = 0;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["TetraminatorApiKey"] = "test-only", ["TetraminatorApiBaseUrl"] = "https://tetra.example/v1/",
            ["TetraminatorInquiryRetryCount"] = "3"
        }).Build();
        using var handler = new AtlasHttpHandler((_, request, body, _) => {
            calls++; Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("/v1/invoice/create", request.RequestUri!.AbsolutePath);
            Assert.NotNull(body);
            var payload = JObject.Parse(body); Assert.Equal(100000, payload["price"]!.Value<long>());
            Assert.Equal("https://merchant.example/callback?orderId=fixture", payload["callback_url"]!.Value<string>());
            return Task.FromResult(JsonResponse((HttpStatusCode)httpStatus, new JObject {
                ["status"] = status, ["pay_id"] = payId, ["gateway_link"] = gateway, ["payment_link"] = legacy
            }.ToString()));
        });
        using var client = new HttpClient(handler);
        var api = new Tetraminator(config, client);
        if (accepted)
        {
            var result = await api.CreateInvoiceAsync(100000, "https://merchant.example/callback?orderId=fixture");
            Assert.Equal(legacy ?? gateway, result.GetPrimaryPaymentLink());
        }
        else await Assert.ThrowsAsync<TetraminatorApiException>(() => api.CreateInvoiceAsync(100000, "https://merchant.example/callback?orderId=fixture"));
        Assert.Equal(1, calls);
    }

    /// <summary>Both URLs survive inquiry JSON replacement and database reopen for owned/tenant wallets, purchase and renewal records.</summary>
    /// <param name="purpose">Existing payment purpose; selection must not depend on audience or order kind.</param>
    /// <param name="botType">Owned or tenant runtime origin recorded by the existing payment flow.</param>
    /// <param name="orderKind">Purchase/renew for direct tenant orders; null for wallet charges.</param>
    /// <returns>A task after isolated SQLite persistence and financial-state invariance checks.</returns>
    [Theory]
    [InlineData(TenantBotPaymentPurposes.WalletCharge, BotInstanceTypes.Owned, null)]
    [InlineData(TenantBotPaymentPurposes.WalletCharge, BotInstanceTypes.Tenant, null)]
    [InlineData(TenantBotPaymentPurposes.TenantOrder, BotInstanceTypes.Tenant, TenantBotOrderKinds.Purchase)]
    [InlineData(TenantBotPaymentPurposes.TenantOrder, BotInstanceTypes.Tenant, TenantBotOrderKinds.Renew)]
    public async Task Tetraminator_gateway_link_persists_across_inquiry_without_financial_changes(string purpose, string botType, string? orderKind)
    {
        using var dbs = new Databases();
        var payment = new TetraminatorPaymentInfo { OrderId = "link-selection", AmountToman = 100000, PaymentPurpose = purpose,
            BotId = botType == "owned" ? "owned-test" : "tenant-test", WalletOriginBotType = botType };
        payment.Apply(new TetraminatorCreateInvoiceResponse { Status = true, PayId = "fixture-id", GatewayLink = "https://gateway.example/invoice/one",
            PaymentLink = "https://t.me/fixture_bot?start=one" });
        await using (var db = dbs.Users.CreateDbContext())
        {
            TenantBotOrder? order = null;
            if (orderKind != null)
            {
                order = new TenantBotOrder { OrderId = payment.OrderId, TenantBotId = payment.BotId, OrderKind = orderKind,
                    PaymentProvider = "Tetraminator", SalePriceToman = payment.AmountToman, PaymentUrl = payment.PaymentLink };
                db.Add(order); await db.SaveChangesAsync(); payment.TenantBotOrderId = order.Id;
            }
            db.Add(payment); await db.SaveChangesAsync();
            if (order != null) { order.TetraminatorPaymentInfoId = payment.Id; await db.SaveChangesAsync(); }
        }
        await using var reloaded = dbs.Users.CreateDbContext();
        var saved = await reloaded.TetraminatorPaymentInfos.SingleAsync();
        Assert.Equal("https://t.me/fixture_bot?start=one", saved.PaymentLink);
        Assert.Equal("https://gateway.example/invoice/one", saved.GatewayLink); Assert.Equal(purpose, saved.PaymentPurpose);
        saved.RawResponseJson = "{\"status\":true,\"payment_status\":\"pending\",\"pay_id\":\"fixture-id\"}";
        saved.Apply(new TetraminatorInquiryResponse { Status = true, PaymentStatus = "pending", PayId = saved.PayId, Amount = saved.AmountToman });
        await reloaded.SaveChangesAsync();
        reloaded.ChangeTracker.Clear();
        saved = await reloaded.TetraminatorPaymentInfos.AsNoTracking().SingleAsync();
        Assert.DoesNotContain("gateway_link", saved.RawResponseJson);
        Assert.Equal("https://gateway.example/invoice/one", saved.GatewayLink);
        var choices = TetraminatorCustomerPaymentUi.BuildKeyboard(saved.PaymentLink, saved.GatewayLink, "tmchk_1");
        Assert.Equal(new[] { saved.PaymentLink, saved.GatewayLink }, choices.InlineKeyboard.SelectMany(r => r).Where(b => b.Url != null).Select(b => b.Url));
        Assert.Equal("fixture-id", saved.PayId); Assert.False(saved.IsAddedToBalance);
        if (orderKind != null)
        {
            var order = await reloaded.TenantBotOrders.AsNoTracking().SingleAsync();
            Assert.Equal(orderKind, order.OrderKind); Assert.Equal(saved.PaymentLink, order.PaymentUrl);
            Assert.Equal(saved.Id, order.TetraminatorPaymentInfoId); Assert.False(order.IsFulfilled);
        }
        Assert.Empty(await reloaded.WalletLedgerEntries.ToListAsync()); Assert.Empty(await reloaded.TenantBotLedgerEntries.ToListAsync());
    }

    /// <summary>The additive URL migration preserves historical invoice/financial facts and performs no backfill or settlement.</summary>
    /// <returns>A task after applying the actual migration chain to isolated old and empty SQLite databases.</returns>
    [Fact]
    public async Task Tetraminator_gateway_link_migration_preserves_historical_invoices()
    {
        using var dbs = new Databases(initialize: false);
        await using var db = dbs.Users.CreateDbContext();
        var migrations = db.Database.GetMigrations().ToList();
        var index = migrations.IndexOf("20261104181421_AddTetraminatorGatewayLink");
        Assert.True(index > 0);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(migrations[index - 1]);
        // Seed using the old schema: the new CLR property must not be selected before its column exists.
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO TetraminatorPaymentInfos
            (OrderId,PayId,PaymentLink,AmountToman,PaymentStatus,TelegramUserId,ChatId,CallbackReceived,
             InquiryAttemptCount,CreatedAtUtc,IsAddedToBalance,IsProvisionallyApproved,BalanceBefore,BalanceAfter)
            VALUES ('historical-choice','old-pay-id','https://t.me/fixture_bot?start=old',100000,'paid',123,123,1,
                    2,'2026-01-01',1,0,5000,105000)
            """);
        await migrator.MigrateAsync();
        var saved = await db.TetraminatorPaymentInfos.AsNoTracking().SingleAsync();
        Assert.Null(saved.GatewayLink);
        Assert.Equal("https://t.me/fixture_bot?start=old", saved.PaymentLink);
        Assert.Equal("old-pay-id", saved.PayId); Assert.Equal("paid", saved.PaymentStatus);
        Assert.Equal(100000, saved.AmountToman); Assert.True(saved.IsAddedToBalance);
        Assert.Equal(5000, saved.BalanceBefore); Assert.Equal(105000, saved.BalanceAfter);
        Assert.Equal(2, saved.InquiryAttemptCount);
        Assert.Empty(await db.WalletLedgerEntries.ToListAsync()); Assert.Empty(await db.TenantBotLedgerEntries.ToListAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        using var fresh = new Databases(initialize: false);
        await using var empty = fresh.Users.CreateDbContext();
        await empty.Database.MigrateAsync();
        Assert.Empty(await empty.TetraminatorPaymentInfos.ToListAsync());
        Assert.False(empty.Database.HasPendingModelChanges());
    }

    /// <summary>Shared keyboards preserve provider order and optional links without modifying status/return callback contracts.</summary>
    /// <param name="bot">Optional synthetic bot URL.</param><param name="gateway">Optional online gateway URL.</param>
    [Theory]
    [InlineData("https://t.me/fixture_bot", "https://gateway.example/invoice/one")]
    [InlineData("https://same.example/invoice", "https://same.example/invoice")]
    [InlineData("https://t.me/fixture_bot", null)]
    [InlineData(null, "https://gateway.example/invoice/one")]
    public void Tetraminator_gateway_link_keyboard_shows_bot_then_online_choice(string? bot, string? gateway)
    {
        var markup = TetraminatorCustomerPaymentUi.BuildKeyboard(bot!, gateway!, "TN:chk:42", "TN:home");
        var buttons = markup.InlineKeyboard.SelectMany(r => r).ToList();
        Assert.Equal(new[] { bot, gateway }.Where(x => x != null), buttons.Where(b => b.Url != null).Select(b => b.Url));
        if (bot != null) Assert.Equal("🤖 پرداخت از طریق ربات", buttons[0].Text);
        if (gateway != null) Assert.Equal("🌐 پرداخت از طریق درگاه آنلاین", buttons[bot == null ? 0 : 1].Text);
        Assert.Equal("TN:chk:42", buttons[^2].CallbackData); Assert.Equal("TN:home", buttons[^1].CallbackData);
        Assert.Contains("فقط از یکی", TetraminatorCustomerPaymentUi.Guidance);
    }
}
