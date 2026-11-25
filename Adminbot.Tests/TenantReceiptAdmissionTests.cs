using System.Reflection;
using Adminbot.Domain;
using Adminbot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Types;
using Xunit;

public sealed partial class ConcurrencyTests
{
    /// <summary>Two concurrent uploads share one pending slot, and the owner's combined action is atomic and storefront-local.</summary>
    /// <returns>A task that verifies sticky evidence, isolation, stale controls, order admission, and lifting the block.</returns>
    /// <remarks>Uses independent handler scopes against the production SQLite writer. No payment, Telegram network or XUI mutation is performed.</remarks>
    [Fact]
    public async Task Tenant_receipt_admission_serializes_orders_and_owner_can_lift_a_local_block()
    {
        using var databases = new Databases();
        await using var provider = StorefrontProvider(databases);
        var tenant = await SeedReceiptTenantAsync(databases, "first-receipt-race", TenantBotOrderKinds.Purchase);
        var other = new BotInstance { Id = "independent-store", Type = BotInstanceTypes.Tenant, OwnerTelegramUserId = ReceiptOwnerId,
            TenantStoreNumber = 2, Enabled = true, CreatedAtUtc = DateTime.UtcNow };
        var second = ReceiptOrder(tenant, "second-receipt-race", TenantBotOrderKinds.Renew);
        var otherOrder = ReceiptOrder(other, "other-store-receipt", TenantBotOrderKinds.WalletCharge);
        int firstId;
        await using (var db = databases.Users.CreateDbContext())
        {
            await db.BotInstances.Where(x => x.Id == tenant.Id).ExecuteUpdateAsync(set => set.SetProperty(x => x.TenantStoreNumber, 1));
            db.AddRange(second, other, otherOrder);
            await db.SaveChangesAsync();
            firstId = await db.TenantBotOrders.Where(x => x.OrderId == "first-receipt-race").Select(x => x.Id).SingleAsync();
        }

        var receipts = await Task.WhenAll(
            Task.Run(() => WriteTenantReceiptAsync(provider, firstId, "first-image")),
            Task.Run(() => WriteTenantReceiptAsync(provider, second.Id, "second-image")));
        var winner = Assert.Single(receipts, x => x > 0);
        Assert.Contains(-3, receipts);
        var winningOrderId = receipts[0] == winner ? firstId : second.Id;
        var losingOrderId = receipts[0] == winner ? second.Id : firstId;
        Assert.Equal(-3, await WriteTenantReceiptAsync(provider, winningOrderId, "replacement"));

        var client = new StorefrontClient();
        using (new BotContextAccessor().Push(new BotRuntimeContext { Config = new BotInstanceConfig { Id = tenant.Id }, Client = client }))
            await HandleCustomerDocumentAsync(provider, client, new Document { FileId = "too-soon", MimeType = "image/jpeg" });
        Assert.Contains(TenantCustomerAdmission.PendingReceiptMessage, client.Texts);
        Assert.True(await WriteTenantReceiptAsync(provider, otherOrder.Id, "independent-image") > 0);

        await using (var db = databases.Users.CreateDbContext())
        {
            var receipt = await db.TenantManualPaymentReceipts.SingleAsync(x => x.Id == winner);
            Assert.Equal(receipts[0] == winner ? "first-image" : "second-image", receipt.PhotoFileId);
            Assert.Equal(1, await db.TenantManualReceiptNotifications.CountAsync(x => x.ReceiptId == winner));
            Assert.False(await db.TenantBotLedgerEntries.AnyAsync());
        }

        await using (var review = provider.CreateAsyncScope())
        {
            var service = review.ServiceProvider.GetRequiredService<TenantBotService>();
            await service.RejectAndBlockManualReceiptAsync(winner, ReceiptOwnerId + 1, default);
            Assert.Equal(-3, await WriteTenantReceiptAsync(provider, losingOrderId, "not-yet"));
            Assert.Contains("مسدود شد", await service.RejectAndBlockManualReceiptAsync(winner, ReceiptOwnerId, default));
            Assert.Contains("دیگر در انتظار", await service.APPROVEMANUALRECEIPTASYNC(winner, ReceiptOwnerId, default));
        }

        Assert.Equal(-4, await WriteTenantReceiptAsync(provider, losingOrderId, "blocked-image"));
        var writer = new UserWorkflowStore(databases.Users);
        writer.Add(ReceiptOrder(tenant, "blocked-new-order"));
        await Assert.ThrowsAsync<TenantCustomerBlockedException>(() => writer.SaveAsync());
        var otherWriter = new UserWorkflowStore(databases.Users);
        otherWriter.Add(ReceiptOrder(other, "allowed-new-order"));
        await otherWriter.SaveAsync();

        var owner = new CredUser { TelegramUserId = ReceiptOwnerId, IsColleague = true };
        await using (var db = databases.Users.CreateDbContext())
            tenant = await db.BotInstances.AsNoTracking().SingleAsync(x => x.Id == tenant.Id);
        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(tenant, "panel"));
        await using (var db = databases.Users.CreateDbContext())
            tenant = await db.BotInstances.AsNoTracking().SingleAsync(x => x.Id == tenant.Id);
        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(tenant, $"order:{winningOrderId}:0"));
        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(tenant, $"unblock:{winningOrderId:X}:{winner:X}"));

        await using (var replay = provider.CreateAsyncScope())
            await replay.ServiceProvider.GetRequiredService<TenantBotService>().RejectAndBlockManualReceiptAsync(winner, ReceiptOwnerId, default);
        Assert.True(await WriteTenantReceiptAsync(provider, losingOrderId, "after-unblock") > 0);
        await using var verify = databases.Users.CreateDbContext();
        Assert.Empty(await verify.TenantCustomerBlocks.ToListAsync());
        Assert.Equal(TenantManualPaymentReceiptStatuses.Rejected,
            (await verify.TenantManualPaymentReceipts.SingleAsync(x => x.Id == winner)).Status);
    }

    /// <summary>A receipt rejected before the courtesy POST boundary cannot be used to authorize the reserved client.</summary>
    /// <returns>A task proving the durable POST authorization is denied after rejection.</returns>
    [Fact]
    public async Task Tenant_receipt_rejection_prevents_a_reserved_courtesy_post()
    {
        using var databases = new Databases();
        await using var provider = StorefrontProvider(databases);
        await SeedReceiptTenantAsync(databases, "receipt-post-race", TenantBotOrderKinds.Purchase);
        int orderId;
        await using (var db = databases.Users.CreateDbContext())
            orderId = await db.TenantBotOrders.Select(x => x.Id).SingleAsync();
        var receiptId = await WriteTenantReceiptAsync(provider, orderId, "valid-image");
        await using (var db = databases.Users.CreateDbContext())
            await db.TenantBotOrders.Where(x => x.Id == orderId).ExecuteUpdateAsync(set =>
                set.SetProperty(x => x.ProvisionalDeliveryState, TenantCardProvisionalStates.Provisioning));

        var store = new XuiV3CreationOperationStore(databases.Users);
        var key = TenantCardProvisionalProvisioningService.BuildCreateOperationKey("receipt-post-race");
        await store.ReserveAsync(new XuiV3CreationOperation
        {
            OperationKey = key, TelegramUserId = ReceiptCustomerId, PanelKey = "fixture", ClientJson = "{}",
            InboundIdsJson = "[1]", BusinessParametersJson = "{}", CreatedAtUtc = DateTime.UtcNow
        }, default);
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
        await service.RejectAndBlockManualReceiptAsync(receiptId, ReceiptOwnerId, default);

        Assert.False(await store.TryStartPostAsync(key, default, receiptId));
        Assert.Equal(XuiV3CreationOutcome.Reserved, (await store.FindAsync(key, default)).Outcome);
    }

    /// <summary>Calls the actual durable receipt writer in a new tenant-handler scope.</summary>
    /// <param name="provider">Fixture provider configured for isolated SQLite.</param>
    /// <param name="orderId">Internal id of the exact order selected by the test.</param>
    /// <param name="image">Fake Telegram photo identifier used to check evidence preservation.</param>
    /// <returns>Positive receipt id on successful admission or the production refusal result.</returns>
    private static async Task<int> WriteTenantReceiptAsync(ServiceProvider provider, int orderId, string image)
    {
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
        var write = typeof(TenantBotService).GetMethod("PERSISTTENANTMANUALRECEIPTASYNC", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return await (Task<int>)write.Invoke(service, new object[] { orderId, image, CancellationToken.None })!;
    }
}
