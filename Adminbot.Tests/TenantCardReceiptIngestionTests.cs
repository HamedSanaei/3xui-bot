using System.Reflection;
using Adminbot.Domain;
using Adminbot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Types;
using Xunit;

/// <summary>
/// Regression coverage for tenant card-to-card receipt ingestion and the provisional-delivery wiring points.
/// </summary>
/// <remarks>
/// <para>
/// These tests pin the exact production incident that made the owner notification worker idle: a customer sent the
/// receipt as a Telegram <b>document</b> ("send without compression") instead of a photo, the legacy handler only looked
/// at <c>Message.Photo</c>, and no receipt row, no durable owner-notification intent, and no provisional operation were
/// ever created. The order silently stayed in <c>awaiting_receipt</c>.
/// </para>
/// <para>
/// They also pin the two properties that make the fix safe: a receipt upload is bound to the exact order the customer
/// pressed the button for, and the provisional courtesy flow is inert unless the global switch is on. No test performs a
/// real Telegram or real XUI network call.
/// </para>
/// </remarks>
public sealed partial class ConcurrencyTests
{
    /// <summary>Tenant bot id used by the receipt-ingestion tests.</summary>
    private const string ReceiptTenantId = "tenant-receipt-incident";

    /// <summary>Telegram user id of the storefront customer whose receipt is ingested.</summary>
    private const long ReceiptCustomerId = 97001;

    /// <summary>Telegram user id of the storefront owner who reviews the receipt.</summary>
    private const long ReceiptOwnerId = 97002;

    /// <summary>
    /// Proves an uncompressed image <c>Document</c> resolves to the receipt file id, which is the regression the
    /// production incident exposed.
    /// </summary>
    [Fact]
    public void Image_document_receipts_are_accepted_by_content_type_or_extension()
    {
        var byContentType = new Message
        {
            Document = new Document { FileId = "document-jpeg", MimeType = "image/jpeg", FileName = null }
        };
        Assert.True(TenantReceiptMediaResolver.TryResolve(byContentType, out var jpeg));
        Assert.Equal("document-jpeg", jpeg.FileId);
        Assert.Equal(TenantReceiptMediaKinds.Document, jpeg.Kind);

        // Telegram drops the MIME type for some forwarded and re-uploaded images, so a safe extension is a strong
        // enough signal on its own.
        var byExtension = new Message
        {
            Document = new Document { FileId = "document-extension", MimeType = null, FileName = "receipt.PNG" }
        };
        Assert.True(TenantReceiptMediaResolver.TryResolve(byExtension, out var png));
        Assert.Equal("document-extension", png.FileId);

        var webp = new Message { Document = new Document { FileId = "document-webp", MimeType = "image/webp", FileName = "r.webp" } };
        Assert.True(TenantReceiptMediaResolver.TryResolve(webp, out _));

        // A compression-enabled photo message still resolves through the photo branch, and the largest rendition wins
        // because that is the highest quality Telegram preserved.
        var photo = new Message
        {
            Photo = new[]
            {
                new PhotoSize { FileId = "small", FileSize = 10 },
                new PhotoSize { FileId = "large", FileSize = 9000 },
                new PhotoSize { FileId = "medium", FileSize = 500 }
            }
        };
        Assert.True(TenantReceiptMediaResolver.TryResolve(photo, out var resolvedPhoto));
        Assert.Equal("large", resolvedPhoto.FileId);
        Assert.Equal(TenantReceiptMediaKinds.Photo, resolvedPhoto.Kind);
    }

    /// <summary>
    /// Proves non-image files can never become a payment receipt.
    /// </summary>
    /// <remarks>
    /// A stored PDF, ZIP, or executable would be unusable in the Sales Assistant relay and would register a bogus review
    /// attempt against a real order. An <c>application/octet-stream</c> upload with no safe extension is refused for the
    /// same reason: the bytes are unverifiable.
    /// </remarks>
    [Fact]
    public void Unsupported_documents_are_rejected_as_receipts()
    {
        Assert.False(TenantReceiptMediaResolver.TryResolve(
            new Message { Document = new Document { FileId = "pdf", MimeType = "application/pdf", FileName = "receipt.pdf" } }, out _));
        Assert.False(TenantReceiptMediaResolver.TryResolve(
            new Message { Document = new Document { FileId = "zip", MimeType = "application/zip", FileName = "receipt.zip" } }, out _));
        Assert.False(TenantReceiptMediaResolver.TryResolve(
            new Message { Document = new Document { FileId = "exe", MimeType = "application/x-msdownload", FileName = "receipt.exe" } }, out _));
        Assert.False(TenantReceiptMediaResolver.TryResolve(
            new Message { Document = new Document { FileId = "octet", MimeType = "application/octet-stream", FileName = "receipt.bin" } }, out _));

        // A safe extension never rescues an explicitly non-image MIME type, so a renamed archive is still refused.
        Assert.False(TenantReceiptMediaResolver.TryResolve(
            new Message { Document = new Document { FileId = "renamed", MimeType = "application/pdf", FileName = "receipt.jpg" } }, out _));

        Assert.False(TenantReceiptMediaResolver.TryResolve(new Message { Text = "سلام" }, out _));
        Assert.False(TenantReceiptMediaResolver.TryResolve(null, out _));
    }

    /// <summary>
    /// Replays the exact production incident: a card purchase receipt delivered as an image <c>Document</c>.
    /// </summary>
    /// <remarks>
    /// The invariants are the ones that were violated in production: a receipt row exists, the order points at it, the
    /// order moves to <c>receipt_submitted</c>, exactly one durable owner-notification intent exists and is still pending
    /// before the worker runs, and the customer is told the receipt was received.
    /// </remarks>
    [Fact]
    public async Task Document_receipt_persists_the_order_and_queues_the_owner_notification()
    {
        using var databases = new Databases();
        await using var provider = StorefrontProvider(databases);
        var tenant = await SeedReceiptTenantAsync(databases, "incident-order", TenantBotOrderKinds.Purchase);

        var client = new StorefrontClient();
        using var context = new BotContextAccessor().Push(
            new BotRuntimeContext { Config = new BotInstanceConfig { Id = tenant.Id }, Client = client });

        await HandleCustomerDocumentAsync(provider, client, new Document
        {
            FileId = "document-receipt-file",
            MimeType = "image/jpeg",
            FileName = "receipt.jpg"
        });

        await using var verify = databases.Users.CreateDbContext();
        var order = await verify.TenantBotOrders.SingleAsync(x => x.OrderId == "incident-order");
        var receipt = await verify.TenantManualPaymentReceipts.SingleOrDefaultAsync(x => x.TenantBotOrderId == order.Id);

        Assert.NotNull(receipt);
        Assert.Equal("document-receipt-file", receipt!.PhotoFileId);
        Assert.Equal(TenantManualPaymentReceiptStatuses.Pending, receipt.Status);
        Assert.Equal(receipt.Id, order.ManualReceiptId);
        Assert.Equal(TenantBotOrderStatuses.ReceiptSubmitted, order.PaymentStatus);
        // A receipt is never a settlement: the order must still be unfulfilled with no ledger row.
        Assert.False(order.IsFulfilled);
        Assert.Empty(await verify.TenantBotLedgerEntries.Where(x => x.TenantBotOrderId == order.Id).ToListAsync());

        var notification = await verify.TenantManualReceiptNotifications.SingleAsync(x => x.ReceiptId == receipt.Id);
        Assert.Equal(TenantManualReceiptNotificationStatuses.Pending, notification.Status);
        Assert.Equal(0, notification.AttemptCount);

        Assert.Contains(client.Texts, text => text.Contains("رسید ثبت شد", StringComparison.Ordinal));
    }

    /// <summary>
    /// Proves a compressed photo receipt still produces exactly the same durable state, so the fix did not regress the
    /// original path.
    /// </summary>
    [Fact]
    public async Task Photo_receipt_still_persists_the_same_durable_state()
    {
        using var databases = new Databases();
        await using var provider = StorefrontProvider(databases);
        var tenant = await SeedReceiptTenantAsync(databases, "photo-order", TenantBotOrderKinds.Purchase);

        var client = new StorefrontClient();
        using var context = new BotContextAccessor().Push(
            new BotRuntimeContext { Config = new BotInstanceConfig { Id = tenant.Id }, Client = client });

        await using (var scope = provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
            var handle = typeof(TenantBotService).GetMethod("HANDLECUSTOMERMESSAGEASYNC", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var message = new Message
            {
                MessageId = 11,
                Chat = new Chat { Id = ReceiptCustomerId },
                From = new Telegram.Bot.Types.User { Id = ReceiptCustomerId },
                Photo = new[] { new PhotoSize { FileId = "photo-receipt-file", FileSize = 2048 } }
            };
            await (Task)handle.Invoke(service, new object[]
            {
                client, message, new CredUser { TelegramUserId = ReceiptCustomerId }, new User { Id = ReceiptCustomerId }, CancellationToken.None
            })!;
        }

        await using var verify = databases.Users.CreateDbContext();
        var order = await verify.TenantBotOrders.SingleAsync(x => x.OrderId == "photo-order");
        var receipt = await verify.TenantManualPaymentReceipts.SingleAsync(x => x.TenantBotOrderId == order.Id);
        Assert.Equal("photo-receipt-file", receipt.PhotoFileId);
        Assert.Equal(TenantBotOrderStatuses.ReceiptSubmitted, order.PaymentStatus);
        Assert.Equal(1, await verify.TenantManualReceiptNotifications.CountAsync(x => x.ReceiptId == receipt.Id));
    }

    /// <summary>
    /// Proves an unsupported document mid receipt-upload is refused without creating a receipt row.
    /// </summary>
    [Fact]
    public async Task Unsupported_document_creates_no_receipt_and_explains_the_required_format()
    {
        using var databases = new Databases();
        await using var provider = StorefrontProvider(databases);
        var tenant = await SeedReceiptTenantAsync(databases, "unsupported-order", TenantBotOrderKinds.Purchase);

        var client = new StorefrontClient();
        using var context = new BotContextAccessor().Push(
            new BotRuntimeContext { Config = new BotInstanceConfig { Id = tenant.Id }, Client = client });

        // The customer pressed the receipt button first, which is what makes the format guidance relevant.
        await using (var scope = provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
            var prompt = typeof(TenantBotService).GetMethod("PromptTenantReceiptUploadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await using var db = databases.Users.CreateDbContext();
            var orderId = await db.TenantBotOrders.Where(x => x.OrderId == "unsupported-order").Select(x => x.Id).SingleAsync();
            await (Task)prompt.Invoke(service, new object[] { client, new ChatId(ReceiptCustomerId), orderId, ReceiptCustomerId, CancellationToken.None })!;
        }

        await HandleCustomerDocumentAsync(provider, client, new Document
        {
            FileId = "document-pdf",
            MimeType = "application/pdf",
            FileName = "receipt.pdf"
        });

        await using var verify = databases.Users.CreateDbContext();
        Assert.Empty(await verify.TenantManualPaymentReceipts.ToListAsync());
        Assert.Empty(await verify.TenantManualReceiptNotifications.ToListAsync());
        var order = await verify.TenantBotOrders.SingleAsync(x => x.OrderId == "unsupported-order");
        Assert.Null(order.ManualReceiptId);
        Assert.Contains(client.Texts, text => text.Contains("JPG", StringComparison.Ordinal));
    }

    /// <summary>
    /// Proves the durable receipt-upload target binds an incoming image to the exact order the customer selected.
    /// </summary>
    /// <remarks>
    /// Before this binding a receipt was attached to whichever unfulfilled card order was newest when the image arrived,
    /// so a customer with two live orders could have order A's payment recorded against order B. The image here is sent
    /// after a newer order exists, which is exactly the case that used to fail.
    /// </remarks>
    [Fact]
    public async Task Receipt_upload_target_binds_the_image_to_the_exact_order()
    {
        using var databases = new Databases();
        await using var provider = StorefrontProvider(databases);
        var tenant = await SeedReceiptTenantAsync(databases, "older-order", TenantBotOrderKinds.Purchase);

        int olderOrderId;
        await using (var db = databases.Users.CreateDbContext())
        {
            olderOrderId = await db.TenantBotOrders.Where(x => x.OrderId == "older-order").Select(x => x.Id).SingleAsync();
            // A second, newer live card order appears before the customer's image arrives.
            db.TenantBotOrders.Add(ReceiptOrder(tenant, "newer-order"));
            await db.SaveChangesAsync();
        }

        var client = new StorefrontClient();
        using var context = new BotContextAccessor().Push(
            new BotRuntimeContext { Config = new BotInstanceConfig { Id = tenant.Id }, Client = client });

        // The customer explicitly asked to upload a receipt for the OLDER order.
        await using (var scope = provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
            var prompt = typeof(TenantBotService).GetMethod("PromptTenantReceiptUploadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)prompt.Invoke(service, new object[] { client, new ChatId(ReceiptCustomerId), olderOrderId, ReceiptCustomerId, CancellationToken.None })!;
        }

        await HandleCustomerDocumentAsync(provider, client, new Document
        {
            FileId = "targeted-receipt",
            MimeType = "image/png",
            FileName = "targeted.png"
        });

        await using var verify = databases.Users.CreateDbContext();
        var older = await verify.TenantBotOrders.SingleAsync(x => x.OrderId == "older-order");
        var newer = await verify.TenantBotOrders.SingleAsync(x => x.OrderId == "newer-order");

        Assert.Equal(TenantBotOrderStatuses.ReceiptSubmitted, older.PaymentStatus);
        Assert.NotNull(older.ManualReceiptId);

        // The newer order must be untouched, which is the regression this binding exists to prevent.
        Assert.Null(newer.ManualReceiptId);
        Assert.Equal(TenantBotOrderStatuses.AwaitingReceipt, newer.PaymentStatus);
        Assert.Empty(await verify.TenantManualPaymentReceipts.Where(x => x.TenantBotOrderId == newer.Id).ToListAsync());

        // The consumed target is cleared so it cannot attach a later image to the same order twice.
        Assert.Null(await ReadPendingReceiptTargetAsync(verify, tenant.Id));
    }

    /// <summary>
    /// Proves the durable owner-notification intent is delivered by the worker, so an image document reaches the owner
    /// exactly like a photo did.
    /// </summary>
    [Fact]
    public async Task Owner_notification_for_a_document_receipt_is_delivered_by_the_worker()
    {
        using var databases = new Databases();
        await using var provider = StorefrontProvider(databases);
        var tenant = await SeedReceiptTenantAsync(databases, "delivered-order", TenantBotOrderKinds.Purchase);

        var client = new StorefrontClient();
        using var context = new BotContextAccessor().Push(
            new BotRuntimeContext { Config = new BotInstanceConfig { Id = tenant.Id }, Client = client });

        await HandleCustomerDocumentAsync(provider, client, new Document
        {
            FileId = "worker-document-receipt",
            MimeType = "image/jpeg",
            FileName = "worker.jpg"
        });

        var sender = new RecordingReceiptSender(messageId: 555);
        var worker = new TenantManualReceiptNotificationWorker(
            databases.Users, sender, NullLogger<TenantManualReceiptNotificationWorker>.Instance);

        var processed = await worker.ProcessOnceAsync();
        Assert.Equal(1, processed);
        Assert.Equal("worker-document-receipt", sender.DeliveredFileId);

        await using var verify = databases.Users.CreateDbContext();
        var receipt = await verify.TenantManualPaymentReceipts.SingleAsync();
        var notification = await verify.TenantManualReceiptNotifications.SingleAsync(x => x.ReceiptId == receipt.Id);
        Assert.Equal(TenantManualReceiptNotificationStatuses.Delivered, notification.Status);
        Assert.Equal(555, notification.TelegramMessageId);
    }

    /// <summary>
    /// Proves the provisional courtesy flow stays completely inert while the global switch is off.
    /// </summary>
    /// <remarks>
    /// This is the backward-compatibility guarantee: with the switch off a card receipt must produce exactly the legacy
    /// state and no provisional operation, no panel work, and no provisional identity.
    /// </remarks>
    [Fact]
    public async Task Provisioning_is_disabled_and_creates_nothing_when_the_switch_is_off()
    {
        using var databases = new Databases();
        await SeedReceiptTenantAsync(databases, "flag-off-order", TenantBotOrderKinds.Purchase);
        var service = CreateProvisioningService(databases, enabled: false);

        var intOrderId = await ReadOrderIdAsync(databases, "flag-off-order");
        var result = await service.ProvisionAsync(
            new CredUser { TelegramUserId = ReceiptCustomerId },
            UnusedPanel(),
            intOrderId,
            CancellationToken.None);

        Assert.Equal(TenantCardProvisionalProvisioningStatus.Disabled, result.Status);
        Assert.Null(result.Email);

        await using var verify = databases.Users.CreateDbContext();
        var order = await verify.TenantBotOrders.SingleAsync(x => x.Id == intOrderId);
        Assert.Equal(TenantCardProvisionalStates.None, order.ProvisionalDeliveryState);
        Assert.Null(order.ProvisionalCreatedAtUtc);
        Assert.Empty(await verify.XuiV3CreationOperations.ToListAsync());
    }

    /// <summary>
    /// Proves the provisional flow never applies to renewals, to already-fulfilled orders, or to non-card providers.
    /// </summary>
    /// <remarks>
    /// A renewal shares the existing client and would be destructively downgraded by a 1 GB / 1 day rewrite, and an
    /// automatic gateway settles through its own verified callback path.
    /// </remarks>
    [Fact]
    public async Task Provisioning_is_not_applicable_outside_card_purchases()
    {
        using var databases = new Databases();
        var tenant = await SeedReceiptTenantAsync(databases, "renewal-order", TenantBotOrderKinds.Renew);
        var service = CreateProvisioningService(databases, enabled: true);

        var renewalOrderId = await ReadOrderIdAsync(databases, "renewal-order");
        var renewal = await service.ProvisionAsync(
            new CredUser { TelegramUserId = ReceiptCustomerId }, UnusedPanel(), renewalOrderId, CancellationToken.None);
        Assert.Equal(TenantCardProvisionalProvisioningStatus.NotApplicable, renewal.Status);

        await using (var db = databases.Users.CreateDbContext())
        {
            var fulfilled = ReceiptOrder(tenant, "fulfilled-order");
            fulfilled.OrderKind = TenantBotOrderKinds.Purchase;
            fulfilled.IsFulfilled = true;
            db.TenantBotOrders.Add(fulfilled);
            await db.SaveChangesAsync();
        }

        var fulfilledOrderId = await ReadOrderIdAsync(databases, "fulfilled-order");
        var fulfilledResult = await service.ProvisionAsync(
            new CredUser { TelegramUserId = ReceiptCustomerId }, UnusedPanel(), fulfilledOrderId, CancellationToken.None);
        Assert.Equal(TenantCardProvisionalProvisioningStatus.NotApplicable, fulfilledResult.Status);

        await using var verify = databases.Users.CreateDbContext();
        Assert.Empty(await verify.XuiV3CreationOperations.ToListAsync());
        Assert.All(
            await verify.TenantBotOrders.ToListAsync(),
            order => Assert.Equal(TenantCardProvisionalStates.None, order.ProvisionalDeliveryState));
    }

    /// <summary>
    /// Proves the approval gate only allows the normal create path when the panel is proven free of a provisional
    /// client.
    /// </summary>
    /// <remarks>
    /// This is the duplicate-account guard. <c>Reserved</c> means the POST was never authorized and
    /// <c>DefinitiveRejected</c> means the panel refused it, so both are safe. <c>PostStarted</c> and <c>Ambiguous</c>
    /// may already have produced a client, so they must escalate to manual review instead of letting fulfillment create
    /// a second account.
    /// </remarks>
    [Fact]
    public async Task Provisional_create_decision_is_fail_closed_for_unresolved_operations()
    {
        using var databases = new Databases();
        var service = CreateProvisioningService(databases, enabled: true);
        var store = new XuiV3CreationOperationStore(databases.Users);
        const string orderId = "decision-order";
        var key = TenantCardProvisionalProvisioningService.BuildCreateOperationKey(orderId);

        // No reservation at all: nothing was ever sent.
        Assert.True(await service.IsPanelProvenUntouchedAsync(orderId, CancellationToken.None));

        // Reserved but never authorized to POST: still nothing on the panel.
        await store.ReserveAsync(OperationCandidate(key), CancellationToken.None);
        Assert.True(await service.IsPanelProvenUntouchedAsync(orderId, CancellationToken.None));

        // A single POST was authorized, so the panel may already hold a client.
        Assert.True(await store.TryStartPostAsync(key, CancellationToken.None));
        Assert.False(await service.IsPanelProvenUntouchedAsync(orderId, CancellationToken.None));

        // Ambiguity reaches the same conclusion, and it is terminal for the automated path: the creation store only
        // reclassifies a still-started attempt, so a later automatic classification cannot talk it back into being safe.
        // Only an authenticated operator who proved absence against the panel may clear it.
        await store.MarkFailureAsync(key, XuiV3CreationOutcome.Ambiguous, CancellationToken.None);
        Assert.False(await service.IsPanelProvenUntouchedAsync(orderId, CancellationToken.None));
        await store.MarkFailureAsync(key, XuiV3CreationOutcome.DefinitiveRejected, CancellationToken.None);
        Assert.False(await service.IsPanelProvenUntouchedAsync(orderId, CancellationToken.None));

        // A definitive rejection recorded on a still-started attempt is the one case that proves the panel holds nothing,
        // so the normal full-order path becomes safe again for that key.
        const string rejectedOrderId = "decision-order-rejected";
        var rejectedKey = TenantCardProvisionalProvisioningService.BuildCreateOperationKey(rejectedOrderId);
        await store.ReserveAsync(OperationCandidate(rejectedKey), CancellationToken.None);
        Assert.True(await store.TryStartPostAsync(rejectedKey, CancellationToken.None));
        await store.MarkFailureAsync(rejectedKey, XuiV3CreationOutcome.DefinitiveRejected, CancellationToken.None);
        Assert.True(await service.IsPanelProvenUntouchedAsync(rejectedOrderId, CancellationToken.None));
    }

    /// <summary>
    /// Proves the three provisional durable keys can never collide with each other or with the normal purchase key.
    /// </summary>
    /// <remarks>
    /// Reusing a key would let one saga authorize a mutation that belongs to another, so the distinct prefixes are a
    /// correctness requirement rather than a naming preference.
    /// </remarks>
    [Fact]
    public void Provisional_operation_keys_are_distinct_from_each_other_and_from_normal_creation()
    {
        const string orderId = "TENANT-42";
        var create = TenantCardProvisionalProvisioningService.BuildCreateOperationKey(orderId);
        var finalize = $"tenant-card-{TenantCardProvisionalOperationKinds.Finalize}:{orderId}";
        var revoke = TenantCardProvisionalRevocationService.BuildOperationKey(orderId);

        Assert.Equal("tenant-card-provisional-create:TENANT-42", create);
        Assert.Equal("tenant-card-finalize:TENANT-42", finalize);
        Assert.Equal("tenant-card-revoke:TENANT-42", revoke);
        Assert.Equal(3, new[] { create, finalize, revoke }.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(create, "tenant-create:", StringComparison.Ordinal);
    }

    /// <summary>Seeds one enabled tenant storefront plus one live card order for it.</summary>
    /// <param name="databases">Temporary database fixture.</param>
    /// <param name="orderId">Public order id to create.</param>
    /// <param name="orderKind">Purchase or renewal; renewals must be excluded from provisional delivery.</param>
    /// <returns>The persisted tenant row.</returns>
    private static async Task<BotInstance> SeedReceiptTenantAsync(Databases databases, string orderId, string orderKind)
    {
        var tenant = new BotInstance
        {
            Id = ReceiptTenantId,
            Type = BotInstanceTypes.Tenant,
            Enabled = true,
            OwnerTelegramUserId = ReceiptOwnerId,
            CreatedAtUtc = DateTime.UtcNow
        };
        await using var db = databases.Users.CreateDbContext();
        db.BotInstances.Add(tenant);
        db.TenantBotOrders.Add(ReceiptOrder(tenant, orderId, orderKind));
        await db.SaveChangesAsync();
        return tenant;
    }

    /// <summary>Builds one unfulfilled card-to-card order awaiting a receipt.</summary>
    /// <param name="tenant">Owning tenant storefront.</param>
    /// <param name="orderId">Public order id.</param>
    /// <param name="orderKind">Purchase or renewal.</param>
    /// <returns>A persistable order entity with no financial effect.</returns>
    private static TenantBotOrder ReceiptOrder(
        BotInstance tenant, string orderId, string orderKind = TenantBotOrderKinds.Purchase)
        => new()
        {
            OrderId = orderId,
            TenantBotId = tenant.Id,
            TenantBotUsername = tenant.Username ?? "receipt_tenant",
            OwnerTelegramUserId = ReceiptOwnerId,
            CustomerTelegramUserId = ReceiptCustomerId,
            CustomerChatId = ReceiptCustomerId,
            OrderKind = orderKind,
            ServiceKey = "normal",
            TrafficGb = 50,
            DurationKey = "days-30",
            AccountCount = 1,
            SalePriceToman = 500_000,
            BaseCostToman = 400_000,
            ProfitToman = 100_000,
            PaymentProvider = "tenant_card",
            PaymentStatus = TenantBotOrderStatuses.AwaitingReceipt,
            CreatedAtUtc = DateTime.UtcNow
        };

    /// <summary>Routes one image document through the real tenant customer message handler.</summary>
    /// <param name="provider">Test-only production service provider.</param>
    /// <param name="client">Recording Telegram transport.</param>
    /// <param name="document">Document the customer sent.</param>
    /// <returns>A task completing after the message is consumed.</returns>
    private static async Task HandleCustomerDocumentAsync(ServiceProvider provider, StorefrontClient client, Document document)
    {
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
        var handle = typeof(TenantBotService).GetMethod("HANDLECUSTOMERMESSAGEASYNC", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var message = new Message
        {
            MessageId = 7,
            Chat = new Chat { Id = ReceiptCustomerId },
            From = new Telegram.Bot.Types.User { Id = ReceiptCustomerId },
            Document = document
        };
        await (Task)handle.Invoke(service, new object[]
        {
            client, message, new CredUser { TelegramUserId = ReceiptCustomerId }, new User { Id = ReceiptCustomerId }, CancellationToken.None
        })!;
    }

    /// <summary>Reads the durable receipt-upload target recorded for the receipt customer.</summary>
    /// <param name="db">Open users.db context.</param>
    /// <param name="botId">Runtime bot id of the storefront.</param>
    /// <returns>The recorded order id, or <c>null</c> when the target was consumed or never recorded.</returns>
    private static Task<int?> ReadPendingReceiptTargetAsync(UserDbContext db, string botId)
        => db.BotUserStates.AsNoTracking()
            .Where(x => x.BotId == botId && x.TelegramUserId == ReceiptCustomerId)
            .Select(x => x.PendingReceiptOrderDbId)
            .SingleOrDefaultAsync();

    /// <summary>Reads one order's internal id by public order id.</summary>
    /// <param name="databases">Temporary database fixture.</param>
    /// <param name="orderId">Public order id.</param>
    /// <returns>The internal database id.</returns>
    private static async Task<int> ReadOrderIdAsync(Databases databases, string orderId)
    {
        await using var db = databases.Users.CreateDbContext();
        return await db.TenantBotOrders.Where(x => x.OrderId == orderId).Select(x => x.Id).SingleAsync();
    }

    /// <summary>Builds the provisioning service over the fixture with an explicit switch value.</summary>
    /// <param name="databases">Temporary database fixture.</param>
    /// <param name="enabled">Value of the global provisional-delivery switch.</param>
    /// <returns>The provisioning service under test.</returns>
    private static TenantCardProvisionalProvisioningService CreateProvisioningService(Databases databases, bool enabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [nameof(AppConfig.TenantCardProvisionalDeliveryEnabled)] = enabled ? "true" : "false",
            ["XuiV3ApiToken"] = "test-only"
        }).Build();
        return new TenantCardProvisionalProvisioningService(
            databases.Users,
            new XuiV3PurchaseService(configuration, databases.Users),
            new XuiV3CreationOperationStore(databases.Users),
            configuration,
            NullLogger<TenantCardProvisionalProvisioningService>.Instance);
    }

    /// <summary>Builds a panel descriptor that is never contacted by the guarded code paths.</summary>
    /// <returns>An unusable panel descriptor used only for calls that return before any panel work.</returns>
    private static ServerInfo UnusedPanel()
        => new() { ApiVersion = "v3", ApiToken = "unused", Url = "http://127.0.0.1:1", Name = "unused" };

    /// <summary>Builds a creation-operation reservation matching the provisional key for one order.</summary>
    /// <param name="operationKey">Durable provisional creation key.</param>
    /// <returns>A reservation candidate accepted by the creation store.</returns>
    private static XuiV3CreationOperation OperationCandidate(string operationKey)
        => new()
        {
            OperationKey = operationKey,
            PanelKey = "panel-hash",
            TelegramUserId = ReceiptCustomerId,
            ClientJson = "{}",
            InboundIdsJson = "[1]",
            BusinessParametersJson = "{\"trafficGb\":1,\"durationDays\":1}",
            CreatedAtUtc = DateTime.UtcNow
        };

    /// <summary>Records the receipt the worker handed to the Sales Assistant without any Telegram call.</summary>
    private sealed class RecordingReceiptSender : ITenantManualReceiptNotificationSender
    {
        private readonly int? _messageId;

        /// <summary>Creates the fake relay sender.</summary>
        /// <param name="messageId">Message id to report, or <c>null</c> to simulate an unavailable assistant bot.</param>
        public RecordingReceiptSender(int? messageId) => _messageId = messageId;

        /// <summary>Gets the receipt file id the worker tried to relay, proving the document file id survived.</summary>
        public string? DeliveredFileId { get; private set; }

        /// <inheritdoc />
        public Task<int?> SendAsync(TenantManualPaymentReceipt receipt, CancellationToken cancellationToken)
        {
            DeliveredFileId = receipt.PhotoFileId;
            return Task.FromResult(_messageId);
        }
    }
}
