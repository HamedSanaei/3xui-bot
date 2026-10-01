using System.Reflection;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Xunit;

/// <summary>Exercises purchase quote payment safety when Telegram cannot confirm a price-changing edit.</summary>
public sealed partial class ConcurrencyTests
{
    /// <summary>Unconfirmed edits retire both discounted and legacy previews without duplicating Telegram delivery or admitting financial work.</summary>
    /// <param name="failureKind">Controlled edit outcome; budget exercises the actual foreground decorator deadline.</param>
    /// <param name="alreadyBound">True models a price-changing discount edit; false models conversion of an old legacy payment keyboard.</param>
    /// <returns>A task after original exception identity, tombstones, stale callbacks and absence of financial mutations are verified.</returns>
    /// <remarks>The old gross-priced keyboard and newly attempted net-priced keyboard may both be visible after transport failure. Neither may remain payable.</remarks>
    [Theory]
    [InlineData("budget", false)]
    [InlineData("budget", true)]
    [InlineData("sdk", false)]
    [InlineData("sdk", true)]
    [InlineData("http", false)]
    [InlineData("http", true)]
    [InlineData("cancel", false)]
    [InlineData("cancel", true)]
    [InlineData("io", true)]
    [InlineData("timeout", true)]
    [InlineData("server", false)]
    [InlineData("unexpected", true)]
    public async Task TenantDiscount_Uncertain_quote_edit_expires_without_replacement_or_payment(string failureKind, bool alreadyBound)
    {
        using var databases = new Databases();
        var configuration = RialGatewayLabelConfiguration(databases, "http://127.0.0.1:59999/");
        await using var provider = AtlasTenantProvider(databases, configuration, new AtlasPay(configuration), out _, out _);
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
        var (tenant, selection, quote) = await PreparePurchaseDeliveryQuoteAsync(databases, provider, service, alreadyBound);
        var failure = failureKind switch
        {
            "budget" => null,
            "sdk" => new RequestException("Telegram response lost", new TaskCanceledException("HTTP deadline")),
            "http" => new HttpRequestException("Response connection reset"),
            "cancel" => new OperationCanceledException("Independent transport cancellation"),
            "io" => new IOException("Response stream interrupted"),
            "timeout" => new TimeoutException("Unconfirmed edit"),
            "server" => new ApiRequestException("Internal Server Error", 500),
            _ => (Exception)new InvalidOperationException("Unconfirmed response")
        };
        var transport = new PurchaseQuoteDeliveryClient { EditFailure = failure, StallEdit = failureKind == "budget" };
        ITelegramBotClient client = new ForegroundBoundedTelegramBotClient(transport,
            new TelegramForegroundDeliveryPolicy { OverallBudget = TimeSpan.FromMilliseconds(30) });
        using var context = provider.GetRequiredService<BotContextAccessor>().Push(new BotRuntimeContext
        {
            Config = new BotInstanceConfig { Id = tenant.Id, Type = BotInstanceTypes.Tenant }, Client = client
        });
        var thrown = await Record.ExceptionAsync(() => RenderPurchaseDeliveryQuoteAsync(service, client, tenant, selection, quote));
        if (failureKind == "budget")
        {
            var deadline = Assert.IsType<TelegramForegroundDeliveryTimeoutException>(thrown);
            Assert.Equal("edit_message_text", deadline.RequestKind);
        }
        else Assert.Same(failure, thrown);
        Assert.Equal(1, transport.Edits);
        Assert.Equal(0, transport.Sends);
        await using (var db = databases.Users.CreateDbContext())
        {
            var saved = await db.TenantDiscountQuotes.AsNoTracking().SingleAsync();
            Assert.Equal(TenantDiscountQuoteStates.Expired, saved.State);
            Assert.Equal(41, saved.MessageId);
            Assert.Null(saved.OrderId);
        }
        Assert.True(await provider.GetRequiredService<TenantDiscountService>()
            .HasQuoteForMessageAsync(tenant.Id, 912, 912, 41));

        // Exercise actual payment handlers, not just quote state. Both possible Telegram keyboards must fail closed.
        var callbackClient = new StorefrontClient();
        var actions = new[] { $"TN:DQ:{quote.Id}:CARD", $"TN:DQ:{quote.Id}:W", "TN:" + quote.SelectionKey,
            "TN:PAYCARD:" + quote.SelectionKey, "TN:PAYWALLET:" + quote.SelectionKey };
        foreach (var action in actions)
            await PressPurchaseDeliveryCallbackAsync(provider, service, callbackClient, action, 41);
        await using var verify = databases.Users.CreateDbContext();
        Assert.Empty(await verify.TenantBotOrders.ToListAsync());
        Assert.Empty(await verify.TenantDiscountRedemptions.ToListAsync());
        Assert.Empty(await verify.TenantBotLedgerEntries.ToListAsync());
        Assert.Empty(await verify.WalletLedgerEntries.ToListAsync());
        await using var wallets = databases.Credentials.CreateDbContext();
        Assert.Empty(await wallets.Users.ToListAsync());
        Assert.Empty(await wallets.WalletOperations.ToListAsync());
    }

    /// <summary>Confirmed no-op edits keep the original quote; definitive rejection alone sends a replacement and tombstones its predecessor.</summary>
    /// <param name="noOp">True returns Telegram's exact known 400 no-op; false rejects the edit as a missing message.</param>
    /// <param name="alreadyBound">Whether the original quote was already bound before the edit attempt.</param>
    /// <returns>A task after message binding and successful discounted card admission, including stale legacy rejection, are verified.</returns>
    /// <remarks>This guards the existing no-op fix and the definitive edit-rejection fallback while ambiguous failures become fail-closed.</remarks>
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task TenantDiscount_Confirmed_no_op_and_rejected_edit_preserve_valid_payment(bool noOp, bool alreadyBound)
    {
        using var databases = new Databases();
        var configuration = RialGatewayLabelConfiguration(databases, "http://127.0.0.1:59999/");
        await using var provider = AtlasTenantProvider(databases, configuration, new AtlasPay(configuration), out _, out _);
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
        var (tenant, selection, quote) = await PreparePurchaseDeliveryQuoteAsync(databases, provider, service, alreadyBound);
        var client = new PurchaseQuoteDeliveryClient { EditFailure = new ApiRequestException(noOp
            ? "Bad Request: message is not modified: specified new message content and reply markup are exactly the same"
            : "Bad Request: message to edit not found", 400) };
        using var context = provider.GetRequiredService<BotContextAccessor>().Push(new BotRuntimeContext
        {
            Config = new BotInstanceConfig { Id = tenant.Id, Type = BotInstanceTypes.Tenant }, Client = client
        });
        Assert.True(await RenderPurchaseDeliveryQuoteAsync(service, client, tenant, selection, quote));
        Assert.Equal(1, client.Edits);
        Assert.Equal(noOp ? 0 : 1, client.Sends);
        await using (var db = databases.Users.CreateDbContext())
        {
            var saved = await db.TenantDiscountQuotes.AsNoTracking().SingleAsync(x => x.Id == quote.Id);
            Assert.Equal(TenantDiscountQuoteStates.Open, saved.State);
            Assert.Equal(noOp ? 41 : 42, saved.MessageId);
            if (!noOp)
                Assert.Equal(TenantDiscountQuoteStates.Expired,
                    (await db.TenantDiscountQuotes.AsNoTracking().SingleAsync(x => x.MessageId == 41)).State);
        }
        var callbackClient = new StorefrontClient();
        if (!noOp)
        {
            await PressPurchaseDeliveryCallbackAsync(provider, service, callbackClient, $"TN:DQ:{quote.Id}:CARD", 41);
            await PressPurchaseDeliveryCallbackAsync(provider, service, callbackClient, "TN:PAYCARD:" + quote.SelectionKey, 41);
            await using var stale = databases.Users.CreateDbContext();
            Assert.Empty(await stale.TenantBotOrders.ToListAsync());
            Assert.Empty(await stale.TenantDiscountRedemptions.ToListAsync());
        }
        await PressPurchaseDeliveryCallbackAsync(provider, service, callbackClient, $"TN:DQ:{quote.Id}:CARD", noOp ? 41 : 42);
        await using var admitted = databases.Users.CreateDbContext();
        var order = await admitted.TenantBotOrders.AsNoTracking().SingleAsync();
        Assert.Equal(quote.NetToman, order.SalePriceToman);
        Assert.Equal(TenantBotOrderStatuses.AwaitingReceipt, order.PaymentStatus);
        if (alreadyBound)
            Assert.Equal(TenantDiscountRedemptionStates.Reserved, (await admitted.TenantDiscountRedemptions.SingleAsync()).State);
        else Assert.Empty(await admitted.TenantDiscountRedemptions.ToListAsync());
    }

    /// <summary>Caller cancellation remains cancellation rather than being mistaken for an independent Telegram timeout or a definitive rejection.</summary>
    /// <returns>A task after unchanged cancellation identity, no replacement and no financial admission are verified.</returns>
    /// <remarks>The caller's token is cancelled at the real edit boundary, not before local quote rendering starts.</remarks>
    [Fact]
    public async Task TenantDiscount_Quote_edit_preserves_explicit_caller_cancellation()
    {
        using var databases = new Databases();
        var configuration = RialGatewayLabelConfiguration(databases, "http://127.0.0.1:59999/");
        await using var provider = AtlasTenantProvider(databases, configuration, new AtlasPay(configuration), out _, out _);
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
        var (tenant, selection, quote) = await PreparePurchaseDeliveryQuoteAsync(databases, provider, service, true);
        using var cancellation = new CancellationTokenSource();
        var failure = new OperationCanceledException(cancellation.Token);
        var client = new PurchaseQuoteDeliveryClient { BeforeEdit = cancellation.Cancel, EditFailure = failure };
        var thrown = await Record.ExceptionAsync(() => RenderPurchaseDeliveryQuoteAsync(service, client, tenant, selection, quote, cancellation.Token));
        Assert.Same(failure, thrown);
        Assert.Equal(1, client.Edits);
        Assert.Equal(0, client.Sends);
        await using var db = databases.Users.CreateDbContext();
        Assert.Empty(await db.TenantBotOrders.ToListAsync());
        Assert.Empty(await db.TenantDiscountRedemptions.ToListAsync());
    }

    /// <summary>Creates an isolated tenant with enabled card and wallet methods and an authoritative tariff, optionally changing the bound quote's discount.</summary>
    /// <param name="databases">Existing fixture owning isolated SQLite files.</param>
    /// <param name="provider">Application services registered against that fixture.</param>
    /// <param name="service">Scoped storefront service used to resolve the real catalog price.</param>
    /// <param name="alreadyBound">True binds message 41 and selects a code before attempting its price-changing edit.</param>
    /// <returns>Persisted storefront, server-resolved selection and detached open quote; no wallet, order or redemption is created.</returns>
    /// <remarks>An unbound quote models an existing legacy Pay/PAY* preview. Wallet permission is real, so stale wallet rejection cannot pass merely because wallet access is disabled.</remarks>
    /// <example><code>var (tenant, selection, quote) = await PreparePurchaseDeliveryQuoteAsync(databases, provider, service, true);</code></example>
    private static async Task<(BotInstance Tenant, XuiV3PurchaseSelection Selection, TenantDiscountQuote Quote)>
        PreparePurchaseDeliveryQuoteAsync(Databases databases, ServiceProvider provider, TenantBotService service, bool alreadyBound)
    {
        var tenant = new BotInstance
        {
            Id = "tenant-delivery", Username = "delivery_store", Type = BotInstanceTypes.Tenant,
            OwnerTelegramUserId = 711, TenantStoreNumber = 1, TenantPriceMarkupPercent = 25, Enabled = true,
            TenantCardPaymentEnabled = true, TenantCardNumber = "6037991234567890", TenantCardHolderName = "Test",
            TelegramBotId = 12345, TenantCustomerWalletEnabled = true, TenantCustomerWalletOwnerEnabled = true,
            TenantCustomerWalletApprovedAtUtc = DateTime.UtcNow, TenantCustomerWalletApprovedByTelegramUserId = 999,
            TenantCustomerWalletApprovedBotId = 12345, TenantCustomerWalletApprovedOwnerId = 711
        };
        await using (var db = databases.Users.CreateDbContext())
        {
            db.BotInstances.Add(tenant);
            await db.SaveChangesAsync();
        }
        var selection = new XuiV3PurchaseSelection { ServiceKey = "normal", TrafficGb = 50, DurationKey = "m1", AccountCount = 1 };
        var key = (string)typeof(TenantBotService).GetMethod("BUILDPAYACTION", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { selection })!;
        var price = typeof(TenantBotService).GetMethod("CalculateTenantPrice", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, new object[] { tenant, selection, null! })!;
        var grossToman = (long)price.GetType().GetProperty("SalePriceToman")!.GetValue(price)!;
        var baseCostToman = (long)price.GetType().GetProperty("BaseCostToman")!.GetValue(price)!;
        var discounts = provider.GetRequiredService<TenantDiscountService>();
        var code = await discounts.SaveCodeAsync(tenant.Id, 711, new TenantDiscountCodeInput("delivery-25",
            TenantDiscountKinds.Percent, TenantDiscountScopes.Purchase, null, 25, 30000, 0, 2, true));
        Assert.True(code.Success);
        var quote = await discounts.CreateQuoteAsync(tenant.Id, 912, 912, key, grossToman, baseCostToman);
        if (alreadyBound)
        {
            Assert.True((await discounts.BindQuoteMessageAsync(quote.Id, tenant.Id, 912, 912, 41)).Success);
            var resolved = await discounts.QuoteAsync(tenant.Id, "delivery-25", grossToman, baseCostToman, TenantDiscountScopes.Purchase);
            Assert.True(resolved.Success);
            var selected = await discounts.SelectQuoteCodeAsync(quote.Id, tenant.Id, 912, 912, 41, key,
                new TenantDiscountSelection(resolved.Value.Code.Id, resolved.Value.Code.UpdatedAtUtc, resolved.Value.Price),
                grossToman, baseCostToman);
            Assert.True(selected.Success);
            quote = selected.Value;
            Assert.True(quote.NetToman < quote.GrossToman);
        }
        return (tenant, selection, quote);
    }

    /// <summary>Exercises the actual quote renderer against a controlled Telegram transport.</summary>
    /// <param name="service">Scoped storefront service.</param>
    /// <param name="client">Controlled SDK transport, optionally wrapped in the production foreground decorator.</param>
    /// <param name="tenant">Persisted fixture storefront.</param>
    /// <param name="selection">Authoritative catalog selection.</param>
    /// <param name="quote">Open fixture quote targeting message 41.</param>
    /// <param name="token">Optional caller cancellation, independent from foreground deadlines.</param>
    /// <returns>The renderer's confirmed-binding result; asynchronous delivery exceptions propagate unchanged.</returns>
    /// <remarks>Reflection only reaches the existing private renderer; every render, Telegram request and database transition is production code.</remarks>
    /// <exception cref="TelegramForegroundDeliveryTimeoutException">The actual foreground decorator's edit budget expires.</exception>
    /// <exception cref="OperationCanceledException">The caller or transport cancels.</exception>
    /// <example><code>await RenderPurchaseDeliveryQuoteAsync(service, client, tenant, selection, quote);</code></example>
    private static Task<bool> RenderPurchaseDeliveryQuoteAsync(TenantBotService service, ITelegramBotClient client,
        BotInstance tenant, XuiV3PurchaseSelection selection, TenantDiscountQuote quote, CancellationToken token = default) =>
        (Task<bool>)typeof(TenantBotService).GetMethod("RenderPurchaseDiscountQuoteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, new object[] { client, new ChatId(912), 41, tenant, selection, quote, token, null! })!;

    /// <summary>Routes an exact-message payment callback through the real wallet and customer checkout handlers.</summary>
    /// <param name="provider">Fixture services supplying bot-scoped conversation state.</param>
    /// <param name="service">Scoped storefront service.</param>
    /// <param name="client">Success-only callback transport, separate from the failed edit transport.</param>
    /// <param name="action">Server-issued quoted or legacy payment callback, never a price.</param>
    /// <param name="messageId">Real fixture message identity, 41 for the original or 42 for confirmed fallback delivery.</param>
    /// <returns>A task after the callback's production admission or stale-button rejection has completed.</returns>
    /// <remarks>The bot context must already identify the fixture tenant. Wallet approval and normal tariff resolution remain active.</remarks>
    /// <example><code>await PressPurchaseDeliveryCallbackAsync(provider, service, client, "TN:PAYCARD:" + quote.SelectionKey, 41);</code></example>
    private static async Task PressPurchaseDeliveryCallbackAsync(ServiceProvider provider, TenantBotService service,
        StorefrontClient client, string action, int messageId)
    {
        var customer = new CredUser { TelegramUserId = 912 };
        var state = await provider.GetRequiredService<UserStateStore>().GetUserStatus(912);
        var callback = new CallbackQuery { Id = Guid.NewGuid().ToString("N"), Data = action,
            From = new Telegram.Bot.Types.User { Id = 912 }, Message = new Message { Id = messageId, Chat = new Chat { Id = 912 } } };
        var wallet = (Task<bool>)typeof(TenantBotService).GetMethod("TryHandleCustomerWalletAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, new object[] { client, new Update { CallbackQuery = callback }, customer, state, CancellationToken.None })!;
        if (!await wallet)
            await (Task)typeof(TenantBotService).GetMethod("HANDLECUSTOMERCALLBACKASYNC", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, new object[] { client, callback, customer, state, CancellationToken.None })!;
    }

    /// <summary>Controls edit acknowledgement and records replacement sends while preserving the shared Telegram-shaped fixture responses.</summary>
    /// <remarks>The stalled edit observes the decorator's real cancellation token. Replacement message 42 intentionally differs from original message 41.</remarks>
    private sealed class PurchaseQuoteDeliveryClient : StorefrontClient
    {
        /// <summary>Exception returned by the single edit attempt, or null for successful delivery.</summary>
        public Exception? EditFailure { get; init; }
        /// <summary>Whether the edit waits for real deadline cancellation instead of synthesizing a timeout.</summary>
        public bool StallEdit { get; init; }
        /// <summary>Optional caller-cancellation action invoked exactly at the edit boundary.</summary>
        public Action? BeforeEdit { get; init; }
        /// <summary>Number of actual SDK edit requests observed.</summary>
        public int Edits { get; private set; }
        /// <summary>Number of actual SDK replacement send requests observed.</summary>
        public int Sends { get; private set; }
        /// <inheritdoc />
        public override async Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is EditMessageTextRequest)
            {
                Edits++;
                BeforeEdit?.Invoke();
                if (StallEdit) await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
                if (EditFailure != null) throw EditFailure;
            }
            if (request is SendMessageRequest) Sends++;
            var response = await base.SendRequest(request, cancellationToken);
            return request is SendMessageRequest
                ? (TResponse)(object)new Message { Id = 42, Chat = new Chat { Id = 912 } }
                : response;
        }
    }
}
